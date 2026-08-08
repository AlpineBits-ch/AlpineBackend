# Extending the pantry product catalog beyond food

Status: phases 1-3 and 5-7 implemented 2026-08-08 (2,071 tests green). Phase 4 is a documented gap.
Research measured 2026-08-08 against live data.

Frontend contract: `Guild.Application/docs/product-catalog-frontend-guide.md`.

**No migration was needed.** The plan assumed a `ProductType` column; it turned out to be redundant,
because `product_type` maps one-to-one onto the existing `Source` column and storing both would be
the same fact twice. `MaxSourceLength` was already 32, and the longest new value is 17 characters.
The foresight that put a `Source` column on the table before there was a second source is what made
this a change of rows rather than of schema.

The pantry resolves scanned barcodes from `product_catalog_entries`, filled from Open Food Facts.
That covers groceries. It does not cover the other half of what a household actually restocks:
washing-up liquid, laundry detergent, shampoo, toothpaste, bin bags, kitchen roll. This document
picks the source for that half and plans the work.

## The criterion that decided it

The requirement was a free-or-cheap provider that permits, ideally encourages, caching. That filter
is much sharper than it first looks, because of a constraint the catalog already carries.

`ProductCatalogEntry` is a Derivative Database under ODbL 1.0, and ODbL 4.6 obliges us to offer
recipients a machine-readable copy of it. `GET /api/v1/pantry/catalog/export` is that offer. So a
new source must be redistributable by us, in bulk, to anyone who asks.

Every commercial barcode API fails that test, and not by a technicality:

- **Barcode Lookup** requires that on termination you "delete all Product Data previously provided
  to you (including any cached and backed up Product Data)". A cache we are licensed to hold, not
  one we own. We could not put it in an export we hand out.
- **UPCitemdb**, **EAN-Search**, **Go-UPC**, **Barcode Spider**, **eandata** all licence query
  results for the subscriber's use. None of them permit republishing the result set.

Mixing any of them into `product_catalog_entries` would either breach their terms (because the
4.6 export republishes it) or break the 4.6 obligation (because we would have to hold rows back).
Keeping them apart means a third table outside the ODbL boundary, with its own retention rules and
its own attribution surface. That is a large amount of new architecture to buy a dataset we cannot
give away.

Meanwhile the Open Food Facts family does not merely permit bulk caching, it asks for it: "for bulk
data needs, download the data as a CSV or JSONL file directly rather than making repeated API
calls." Same ODbL, same export obligation already satisfied, same attribution machinery already
built.

**So the recommendation is to stay inside the ODbL family and add its non-food siblings.** Not
because nothing else exists, but because everything else costs a second legal boundary.

## What was measured

Downloaded the full exports and counted, rather than trusting the marketing pages. Script:
`scratchpad/measure_off_siblings.py`.

| | Open Products Facts | Open Beauty Facts |
|---|---|---|
| Export size (gzip) | 35.8 MB JSONL | 87.1 MB JSONL / 57 MB Parquet |
| Total products | 43,921 | 69,340 |
| Named in de/fr/it/en | 25,002 (56.9%) | 38,645 (55.7%) |
| Tagged CH/DE/AT/FR | 19,105 | 27,005 |
| ...of those, importable | **15,786** | **19,290** |
| Tagged Switzerland | 438 (285 named) | 862 (566 named) |
| Carries a quantity | 12.6% | 32.8% |

Combined: **35,076 importable rows** for our markets, from 123 MB of download.

The 1,300 Swiss-tagged figure quoted in `ProductCatalog.cs` is confirmed exactly, two databases
still sum to 1,300 today.

### The two siblings are not equally worth having

Open Beauty Facts is the prize. Its DACH+FR categories are hygiene 2,607, hair 1,290, shampoos 893,
showers-and-baths 677, deodorants 604, shower gels 536, soaps 508, toothpastes 642. That is
precisely the everyday-consumable half the pantry is missing, and 32.8% of it carries a pack size.

Open Products Facts is thinner than its row count suggests. Its DACH+FR top categories are
home-garden 1,587, household-supplies 1,117, printed-publications 1,066, laptops 1,025, printed
media 966, health-beauty 608, household-cleaning-supplies 480, household-chemicals 406, tobacco
410, books 269. Actual cleaning and household supplies are roughly 2,200 rows; the rest is books,
electronics and cigarettes.

It is still worth importing (36 MB is nothing, and a name is a name), but nobody should expect
detergent coverage to feel solved. **Cleaning products are genuinely poorly covered by open data.**

## Two API findings that shape the implementation

Both verified live against the production API.

**1. The flavours are siloed by default.** A beauty barcode returns HTTP 404 from
`world.openfoodfacts.org`, and a food barcode 404s from the beauty host. Naively supporting three
sources would triple our consumption of a 15 req/min per-IP budget that is shared by the whole
deployment.

**2. `product_type=all` collapses them into one request.** Verified:

```
GET world.openfoodfacts.org/api/v3/product/0000527000057.json?product_type=all
  -> 200 status=success product_type=beauty   ("Kiehls - Cream #gel")
GET world.openfoodfacts.org/api/v3/product/6950547800073.json?product_type=all
  -> 200 status=success product_type=product  ("Mini voiture telecommander")
GET world.openfoodfacts.org/api/v3/product/3600523351893.json?product_type=all
  -> 404 result.id=product_not_found
```

One endpoint, one rate limit, one miss row per barcode. **The existing live-lookup architecture
survives intact**; it gains a query parameter, not a redesign.

Without the parameter, the wrong-flavour reply is a 404 whose body reads
`result.id = "product_found_with_a_different_product_type"` and names the flavour in
`errors[0].field.value`. Worth handling defensively, see the bug below.

## A bug this work must fix regardless

`ProductCatalogLookupService.SaysNotFound()` treats any `status: "failure"` as `Absent`, and
`Absent` consumes a backoff attempt and eventually settles the miss permanently.

Once non-food barcodes are in play, `product_found_with_a_different_product_type` is a `failure`
status for a product that **demonstrably exists**. An instance whose `PRODUCT_CATALOG_API_BASE_URL`
points at a flavour host, or any code path that loses the `product_type=all` parameter, would mark
real shampoo as permanently absent after three attempts. That is the one miss-table outcome the
class was carefully designed to avoid, arrived at from a direction it did not anticipate.

Fix: treat `product_found_with_a_different_product_type` as its own outcome. With
`product_type=all` it should never occur, which is exactly why it should be logged rather than
silently folded into `Absent`.

## A second bug: attribution is hardcoded

`ProductCatalogService.ToDto` returns `SourceName = OpenFoodFactsName` and
`SourceUrl = ProductUrl(barcode)`, both hardcoded to the food database, for every row regardless of
`Entry.Source`. Today that is correct because there is one source. The moment a beauty row exists it
becomes a false attribution statement on every scan response, and attribution is the obligation the
whole `ProductCatalogSources` class exists to discharge.

## Plan

### Phase 1 - make the model source-aware (no behaviour change)

1. `ProductCatalogSources`: add `OpenBeautyFacts = "openbeautyfacts"` and
   `OpenProductsFacts = "openproductsfacts"`. Replace the three hardcoded
   `OpenFoodFacts*` constants with lookups keyed on the stored `Source`: display name, site URL and
   `ProductUrl(source, barcode)`. The licence, licence URL and ODbL 4.3 attribution notice stay
   shared, all three databases are ODbL 1.0.
2. Fix `ToDto` to resolve name and URL from `match.Entry.Source`.
3. Add `ProductType` (nullable, max 16) to `ProductCatalogEntry`, holding the API's own
   `product_type` (`food` / `beauty` / `product`). This is what lets a live-fetched row know which
   source, and therefore which attribution, applies to it.
4. EF-generated migration for the new column. Generated, not hand-edited.

Tests: attribution resolves per source; an unknown source value falls back to the food database
rather than throwing.

### Phase 2 - one request, all flavours

1. Append `product_type=all` to the lookup query and add `product_type` to the `fields` list.
2. Map `product_type` to the `Source` value on the built entry, so a live-fetched shampoo is stored
   as `openbeautyfacts` and attributed to Open Beauty Facts.
3. Add `LookupKind.WrongFlavour` (or an equivalent) for
   `product_found_with_a_different_product_type`, treated like `Unreachable` for backoff purposes
   (short cool-off, no attempt consumed) and logged, since it indicates misconfiguration.

Tests: a beauty barcode resolves and is attributed to Open Beauty Facts; `product_not_found` still
takes the absence backoff; the wrong-flavour body does not consume an attempt.

### Phase 3 - bulk extract for the siblings

`deploy/off-catalog-extract.sh` gains the two siblings. It cannot be one code path, because the two
exports differ in shape:

- **Beauty**: `beauty.parquet` on Hugging Face, 57 MB. Same nested `list<{lang, text}>`
  `product_name` column as food, so the existing DuckDB projection works unchanged. Trivial to add.
- **Products**: no Parquet exists. The Open Products Facts data page links to `food.parquet`, which
  is a copy-paste error on their side; the Hugging Face repo contains only `food.parquet` and
  `beauty.parquet`. Use the JSONL export (36 MB) via DuckDB `read_json_auto`, where names are flat
  `product_name_de` / `_fr` / `_it` / `_en` fields rather than a nested list.

Both write the same NDJSON the existing importer reads, with `sourceVersion` tagged per flavour
(`obf-YYYY-MM-DD`, `opf-YYYY-MM-DD`) so the 4.6 export can say which snapshot each row came from.

Recommended filtering for Open Products Facts: drop `en:incorrect-product-type` (222 rows in
DACH+FR, self-declared wrong) and ISBN prefixes 978/979, both actively wrong rather than merely
irrelevant. Do **not** filter out electronics and tobacco; they are noise, but a household scanning
a barcode wants a name, and the storage cost is negligible. Flagging this as a judgment call rather
than a settled one.

### Phase 4 - the honest gap

Swiss retailer own-brands (M-Budget, Prix Garantie, Denner) remain almost entirely absent. 851
named Swiss rows across both siblings will not change that, and no free database covers them.

Options, none of which are recommended for this round:

- **GS1 Switzerland trustbox** is the authoritative Swiss source with genuine de/fr/it names. It is
  a data pool built for suppliers publishing their own data; consumer-side access runs through GS1
  membership or an accredited service provider, is priced (still unpublished, as
  `ProductCatalog.cs` already notes), and is not caching-friendly. Worth an email, not a dependency.
- **opengtindb.org** is free, German, and its 26 categories do include household and cosmetics. But
  it publishes no licence, offers no bulk dump, serves a TXT API, and looks semi-dormant. Cannot go
  in the ODbL table without a licence statement.

The existing `PantryBarcode` learned-name path already handles this correctly: the household types
the name once and their own guild remembers it. That remains the answer for Swiss own-brands, and it
is a reasonable one.

### Rejected, with reasons

- **Datakick / gtinsearch.org** - CC0 and no registration required, which would have put it outside
  the ODbL boundary entirely. Measured: ~8,645 products total (the `page` parameter is ignored and
  returns ids 1-8655 whatever you ask for), 58.5% US UPC codes, ~104 Swiss, and it began returning
  403 under light use. Dormant.
- **Wikidata** (`P3962`) - CC0, but 2,506 items carry a GTIN worldwide. Not a product database.
- **Open Pet Food Facts** - 19 MB, same family, trivially addable later if pet supplies ever matter.
  Out of scope here.

### Phase 5 - a keyword product search endpoint (designed, not built)

Wanted for a frontend feature: search products by keyword rather than by barcode, covering every
provider, answering from our own data first and only then going live.

**Cache-first is already the rule for barcode lookups and needs no change.**
`ProductCatalogService.ResolveForScanAsync` reads `product_catalog_entries` first, calls the API only
on a local miss, writes whatever comes back into the table, and records a `ProductCatalogMiss` with
an escalating 7d / 30d / 90d / never backoff so a barcode the source does not have is not re-asked.
A server-wide token bucket holds the whole deployment to 10 req/min against their 15. Since phase 2
that one request covers all four databases instead of one.

**Local search covers every provider for free**, because all four providers' rows land in the same
table. That is a direct payoff of the one-table-with-a-Source-column design.

**Live search, though, is a much weaker instrument than live barcode lookup, and the measurements
are unpleasant:**

- Search is rate-limited harder than product reads: **10 req/min/IP** against 15, and facet queries
  get 2. The documentation explicitly warns that search-as-you-type "will be blocked very quickly".
- **`search_terms` on API v2 is silently ignored.** Measured against Open Beauty Facts:
  `search_terms=shampoo` returns `count: 69749` and `search_terms=zzzzqqqxyz` returns `count: 69749`,
  which is the entire database both times. It answers 200 with plausible-looking products and has
  not searched for anything. This is the trap in this phase: a naive implementation looks like it
  works.
- Free text that actually filters is the **legacy `cgi/search.pl`**, which the docs deprecate but
  which does work: `shampoo` returns 2,548 of those 69,749.
- **There is no cross-flavour search.** `product_type=all` on the v2 search endpoint returns 503, so
  covering all providers live costs one request per flavour, three or four out of ten per minute,
  per user query.
- Search-a-licious (`search.openfoodfacts.org`) does real full text, but it is food-only and the
  hits it returned carried `last_indexed_datetime: 2024-02-29`.

Design that follows from the above:

1. `GET /api/v1/pantry/catalog/search?q=&lang=&limit=` answering from `product_catalog_entries`
   only. Needs a text index; the table currently has just the barcode primary key and `ImportedAt`.
   A `pg_trgm` GIN index over the four name columns and the brand suits short product-name queries
   better than `tsvector`, which is built for prose and would stem Swiss product names badly.
2. Live search strictly as a fallback when the local result set is empty, never per keystroke, on
   its own token bucket sized to their 10/min rather than sharing the barcode bucket, and off by
   default behind its own flag so an instance opts into being that client.
3. Whatever it returns is upserted into the catalog exactly as a barcode lookup is, so the second
   person to search the same term is served locally. A negative-result cache mirroring
   `ProductCatalogMiss`, keyed on the normalised term, stops a term the source cannot answer being
   re-asked.
4. Use `cgi/search.pl` and not v2 `search_terms`, with a test that asserts two different terms give
   different counts. That assertion is the only thing standing between this feature and silently
   returning arbitrary products.

**Shipped: step 1 only**, which was the recommendation. `GET /api/v1/pantry/catalog/search`, a
stored generated `search_text` column with a single `gin_trgm_ops` index, authenticated, paged,
wildcards escaped. Steps 2-4 (the live fallback) are deliberately not built: it is worth measuring
how often the local index actually comes up empty before paying for one query per six seconds.

Two notes from building it:

- `EF.Functions.ILike(x, pattern)` emits no `ESCAPE` clause, so the two-argument overload ignored the
  backslash escaping and a search for "35%" matched nothing. The three-argument overload naming the
  escape character is required. Caught by a test, not by review.
- Search is tested against real Postgres only. The column is database-computed and `ILIKE` has no
  InMemory implementation, so an InMemory suite would assert on a null column through a function
  that does not exist there and pass by finding nothing.

### Phase 6 - automatic import (implemented)

`ProductCatalogAutoImportService`, a hosted service that downloads the published exports and loads
them without anybody running a script.

- **Siblings only.** Cosmetics is 87 MB and general products 36 MB as gzipped JSONL, which .NET
  streams with no dependency. The food export is **11.77 GB** compressed, which is why it stays with
  `deploy/off-catalog-extract.sh` and DuckDB. A test asserts food is not in the importable set, so
  nobody adds it thinking it was an oversight.
- **Off by default in code, on in `alpine-infra`.** The asymmetry with `LiveFillEnabled` is
  deliberate: a live lookup is one small request because somebody scanned something; this is a
  123 MB download that happens whether or not the pantry is used. Defaulting it on would have every
  self-hosted instance pulling the full export monthly from a volunteer-run service.
- **One pod.** The HPA runs 2-3 replicas and all of them start the service. A Postgres advisory lock
  makes exactly one do the work, and being connection-held means a crashed pod releases it by
  disconnecting rather than leaving a stuck flag that needs a human.
- **Graceful.** Batched upserts with no table lock (a concurrent scan sees the old row or the new
  one, never an empty table), a 50 ms pause between batches, and a configured quiet hour. A failure
  is a log line and a retry tomorrow.
- **Due-ness is judged from the data** (`MAX(imported_at)` per source), not a "last run" record, so a
  fresh pod, a restored database and a hand-run import all say the right thing.

Manual trigger: `POST /api/v1/pantry/catalog/import/auto?source=` (instance admin), returning 202
because a refresh takes minutes.

### Phase 7 - the admin console view (implemented)

`Echo/wwwroot/admin`, rail item **Product catalog**, administrator-only for the same reason
Federation is: every button in it writes a table each household on the instance reads. It wires all
five catalog endpoints - the info summary, the automatic-import trigger, the file import, the
keyword search as a spot check, and a link to the ODbL export.

Three things in it are load-bearing.

**Files are posted in 4 MB pieces, not in one request.** This is what makes a several-hundred-megabyte
grocery extract loadable from a browser at all, and it is safe only because the import is an upsert
keyed on the barcode: each piece is independent, re-posting one changes nothing, and a run that stops
halfway is finished by running the same file again. The alternative fails three ways - Kestrel caps a
body at 30 MB and the gateway in front of it still does, one request held open for a full import is
what proxy activity timeouts kill, and a connection dropped at 90% loses everything. Pieces are cut
at the last newline *byte* in the slice, never in decoded text, so a boundary landing inside a
multi-byte character cannot corrupt it. A piece with no newline in four megabytes is the wrong file
(a Parquet or a CSV) and is refused with that said plainly, because the server would otherwise reject
every line as malformed and report a successful import of nothing.

**The console must not build its URLs from the info response's `exportUrl`.** That field is the
service's own path; the gateway serves Guild at `/api/v1/guild/**` and strips the segment, so the
service-internal path answers 404 from the console's origin.

**A 30 MB body cap was silently blocking the one database big enough to need bulk loading.** Kestrel's
default rejected any extract over 30 MB before a row was read, so the curl line
`off-catalog-extract.sh` prints answered 413 for food and worked for everything else - which is
exactly the shape of a bug nobody reports, because the small cases all pass. The import endpoint now
lifts its own cap, after the administrator check and never before it: an unbounded body from an
anonymous caller is a different proposition from one from an operator. That is the service's own
limit only; a caller coming through the gateway meets the gateway's Kestrel first, which is why the
console chunks and the script now prints a `split -C 4m` loop.

**Not covered by a test:** the raw-body path through Wolverine's HTTP binding. The five new tests
call the endpoint method directly, which is where the cap logic lives, but nothing exercises
`Content-Type: application/x-ndjson` arriving through the real pipeline - this is the only endpoint
in the codebase that reads `HttpRequest.Body` itself. If Wolverine turns out to constrain the content
type, the first piece answers 415 and the fix is one header.

## Expected outcome

About 35,000 additional resolvable barcodes for our markets, dominated by personal care and hygiene,
with cleaning products improved but still thin. No new legal boundary, no new attribution
architecture, no additional rate-limit pressure, and two real bugs fixed on the way.
