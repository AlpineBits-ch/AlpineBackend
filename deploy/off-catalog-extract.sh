#!/usr/bin/env bash
#
# off-catalog-extract.sh - build the pantry product catalog extracts from the Open Food Facts family.
#
# WHAT THIS IS
#
#   The pantry resolves scanned barcodes to product names from a local table. That table is loaded
#   from filtered extracts of the Open Food Facts family of databases, and this script is what
#   produces them. It filters each published export to the markets we serve, projects the seven
#   columns the pantry stores, and writes newline-delimited JSON that
#   POST /api/v1/pantry/catalog/import reads directly.
#
# THE FAMILY IS FOUR DATABASES, NOT ONE
#
#   A barcode lives in exactly one of them, split by what kind of thing it is:
#
#     food      groceries.                     ~4.66M rows.  Parquet, 7.2 GiB.
#     beauty    cosmetics and personal care.   ~69k rows.    Parquet, 57 MiB.
#     products  everything else, cleaning included. ~44k rows. JSONL, 36 MiB.
#
#   Measured coverage for CH/DE/AT/FR, which is what this script filters to:
#
#     beauty    19,290 importable rows. Hygiene, shampoo, deodorant, toothpaste, soap. This is the
#               bulk of what a household restocks that is not food, and the reason to run it.
#     products  15,786 importable rows, but only ~2,200 of those are household or cleaning supplies.
#               The rest is books, laptops and tobacco. Worth loading - a name is a name, and it is
#               36 MiB - but nobody should expect detergent coverage to feel solved. Cleaning
#               products are genuinely poorly covered by open data.
#
#   Pet food (openpetfoodfacts, ~19 MiB) is the fourth. Not extracted here because the pantry has no
#   use for it yet; the service knows the source and will attribute a live-fetched one correctly.
#
#   Each flavour is imported separately, because the import is tagged with the source it came from
#   and that tag is what tells a client which database to credit. See the curl lines printed at the
#   end of a run.
#
# WHY BULK IS THE RECOMMENDED WAY TO POPULATE, AND WHY IT IS NOT A PREREQUISITE
#
#   The service also asks the API about single barcodes, inline on a scan and again on a background
#   sweep, so an instance that never runs this script still works: the first scan of a common
#   product resolves live, and anything else asks the user for a name exactly as it did before the
#   catalog existed. What the script buys is coverage on day one instead of coverage accumulated one
#   scan at a time.
#
#   Bulk is nonetheless the recommended path, for two reasons that both come from Open Food Facts
#   rather than from us. Their API documentation asks for it directly - "for bulk data needs,
#   download the data as a CSV or JSONL file directly rather than making repeated API calls" - and
#   they rate-limit product reads to 15 per minute per IP address, which is a limit on a whole
#   deployment and not on a household. At that rate a market's worth of products is measured in
#   weeks of being the noisiest client on a volunteer-run database. The API is not the problem; it
#   is fast and it is reliable. It is simply not a loading mechanism.
#
#   (An earlier version of this comment said bulk was chosen because the API returned HTTP 503 about
#   half the time. That measurement was taken against the search endpoint, which is heavily loaded.
#   The product-by-barcode endpoint used here answers in 44-66 ms. The claim was wrong and the two
#   reasons above are the real ones.)
#
# WHY BEAUTY IS PARQUET AND PRODUCTS IS JSONL
#
#   Not a preference. Hugging Face hosts food.parquet and beauty.parquet and nothing else. The Open
#   Products Facts data page links to a products Parquet, but the link points at food.parquet - a
#   copy-paste error on their side, verified against the repository file listing. So that flavour is
#   read from its JSONL export instead, where the per-language names are flat product_name_de /
#   _fr / _it / _en fields rather than the nested list<{lang, text}> the Parquet uses. That is the
#   only reason there are two projections below.
#
# TESTING AGAINST THE STAGING ENVIRONMENT
#
#   Open Food Facts run a staging instance at https://world.openfoodfacts.net (HTTP basic auth
#   off / off) and ask that applications be tested against it rather than production. Point
#   PRODUCT_CATALOG_API_BASE_URL there for any manual verification of the live lookup. This script
#   reads the published exports rather than the API and is unaffected.
#
# WHY THIS IS A SCRIPT AND NOT CODE IN THE SERVICE
#
#   The obvious alternative is to take a Parquet reader (Parquet.Net) into Guild.Application and do
#   all of this in-process, on a schedule, with no human involved. It was rejected.
#
#   The food export alone is ~7.2 GiB compressed and ~21 GiB decompressed, 4.6 million rows, and the
#   product_name column is a nested list<{lang, text}> that has to be unnested to get at the German,
#   French and Italian names - which are the entire reason the Parquet is used instead of the much
#   smaller CSV, since the CSV has no per-language name columns and no normalised quantity unit at
#   all. Doing that work inside the chat service means a columnar-format dependency in the
#   dependency graph, in the container image and in the patch queue, plus seven gigabytes of
#   download and tens of gigabytes of scratch disk on an application host. All of it for a job that
#   runs about once a month.
#
#   Running it here instead means the service ingests a format that needs no library, the seven
#   gigabytes are handled by whatever machine already has the disk, and the artefact is a file that
#   can be checksummed, kept, diffed against the last one and re-imported if an import half-fails.
#   The honest cost is that a refresh is not self-service: somebody, or a cron entry on a build box,
#   has to run this and post the result. At a cadence of weeks that is the cheaper side of the
#   trade. If the cadence ever becomes daily, revisit it - that is the condition under which the
#   in-process reader starts to earn its place.
#
# LICENCE - READ THIS BEFORE CHANGING THE PROJECTION
#
#   All of these databases are licensed under the Open Database License (ODbL) v1.0. The table this
#   produces is a Derivative Database, share-alike attaches to it, and ODbL 4.6 obliges us to offer
#   recipients a free machine-readable copy - which is what GET /api/v1/pantry/catalog/export
#   serves. Three rules follow and none is negotiable:
#
#     1. The extract contains product data and nothing else. No household data, no user-entered
#        names, nothing about who scanned what. It lands in its own table, product_catalog_entries,
#        which is never merged with pantry_barcodes (the table of names households typed).
#     2. Anywhere a catalog-sourced name is shown, the source and the licence are shown with it.
#        The API does this by returning them on the scan response.
#     3. Each flavour is imported under its own --source, because ODbL 4.3 requires the notice to
#        name the database being credited. Importing beauty rows as "openfoodfacts" would credit the
#        wrong database and link to a product page that does not exist.
#
#   Images are NOT extracted. They are CC-BY-SA 3.0 rather than ODbL, which is a second share-alike
#   regime with per-photo attribution, and Open Food Facts warns that packaging and trademark rights
#   in them may belong to third parties it cannot license. Adding an image column here is a separate
#   decision, not a tweak.
#
# REQUIREMENTS
#
#   duckdb (a single static binary, https://duckdb.org). Chosen because it reads remote Parquet and
#   remote gzipped JSON with projection and predicate pushdown, so the 7.2 GiB is streamed and
#   filtered rather than materialised, and because it is one binary with no runtime to install.
#
# USAGE
#
#   ./off-catalog-extract.sh [flavours] [country-regex] [output-dir]
#
#   flavours     comma-separated: food, beauty, products. Default "beauty,products", which is the
#                cheap half (93 MiB of download) and the half most instances are missing. Pass
#                "food,beauty,products" for everything, or "food" for the original behaviour.
#   country-regex  default Switzerland, Germany, Austria and France.
#   output-dir   default the working directory.
#
#   NOTE: the argument list changed when the siblings were added. It used to be
#   [output.ndjson] [country-regex]. Output names are now derived per flavour, because three
#   flavours cannot share one filename.
#
set -euo pipefail

FLAVOURS="${1:-beauty,products}"
COUNTRIES="${2:-en:switzerland|en:germany|en:austria|en:france}"
OUTDIR="${3:-.}"
STAMP="$(date +%F)"

command -v duckdb >/dev/null 2>&1 || {
  echo "duckdb is required: https://duckdb.org/docs/installation/" >&2
  exit 1
}

mkdir -p "${OUTDIR}"

# The four columns every projection ends with, in the order the importer expects. Kept in one place
# so a change to the pantry's stored columns is one edit rather than three.
#
# The unlocalised name is folded into name_en as a last resort, which is exactly what the live
# lookup does (see ProductCatalogLookupService.BuildEntry). The source files it in whichever
# language the contributor typed, so it cannot honestly be filed under a language column - but a
# product with only an unlocalised name is still better than a blank row, and English is where a
# reader is least likely to assume it is authoritative. Worth about six points of coverage on the
# siblings, where the per-language fill is thin.

extract_parquet() {
  local flavour="$1" url="$2" out="$3"

  echo "Reading  ${url}"
  echo "Filter   countries matching /${COUNTRIES}/"
  echo "Writing  ${out}"

  duckdb -c "
COPY (
  WITH filtered AS (
    SELECT code, product_name, brands, product_quantity, product_quantity_unit
    FROM read_parquet('${url}')
    WHERE code IS NOT NULL
      AND length(code) BETWEEN 6 AND 64
      -- countries_tags is a list; any market we serve is enough to keep the row.
      AND len(list_filter(countries_tags, t -> regexp_matches(t, '${COUNTRIES}'))) > 0
  ),
  named AS (
    SELECT
      code AS barcode,
      -- product_name is list<{lang, text}>. Pulling one language out of it is the whole reason this
      -- uses the Parquet export: the tab-separated CSV has a single unlabelled name column and no
      -- product_name_de/_fr/_it at all, which in a trilingual market loses most of the value.
      nullif(trim(list_extract(list_filter(product_name, n -> n.lang = 'de'), 1).text), '') AS name_de,
      nullif(trim(list_extract(list_filter(product_name, n -> n.lang = 'fr'), 1).text), '') AS name_fr,
      nullif(trim(list_extract(list_filter(product_name, n -> n.lang = 'it'), 1).text), '') AS name_it,
      coalesce(
        nullif(trim(list_extract(list_filter(product_name, n -> n.lang = 'en'), 1).text), ''),
        -- 'main' is the family's tag for the unlocalised name. A flavour that does not use it
        -- yields NULL here rather than an error, which is the intended no-op.
        nullif(trim(list_extract(list_filter(product_name, n -> n.lang = 'main'), 1).text), '')
      ) AS name_en,
      nullif(trim(brands), '') AS brand,
      try_cast(product_quantity AS DECIMAL(12,3)) AS quantity,
      nullif(trim(product_quantity_unit), '') AS quantity_unit
    FROM filtered
  )
  SELECT * FROM named
  -- A row with no name in any of the four languages can never fill anything, so it is dropped here
  -- rather than shipped and skipped at import. Measured: about 4.6% of the Swiss food subset.
  WHERE coalesce(name_de, name_fr, name_it, name_en) IS NOT NULL
  -- A pack size of zero or less is not a pack size; null is the normal case, since only 43.5% of
  -- Swiss food products and 12.6% of general products carry a quantity at all.
) TO '${out}' (FORMAT JSON, ARRAY false);
"
}

extract_jsonl() {
  local flavour="$1" url="$2" out="$3"

  echo "Reading  ${url}"
  echo "Filter   countries matching /${COUNTRIES}/"
  echo "Writing  ${out}"

  # Columns are declared rather than sniffed. These exports carry hundreds of fields, many of them
  # deeply nested, and letting read_json_auto infer a schema over that is both slow and a good way
  # to have an unrelated field's type change break the extract. Declaring seven means seven are
  # read. product_quantity is taken as VARCHAR and cast afterwards because the source files it
  # inconsistently as a number and as a string.
  duckdb -c "
COPY (
  WITH filtered AS (
    SELECT *
    FROM read_json('${url}',
      format = 'newline_delimited',
      ignore_errors = true,
      columns = {
        code: 'VARCHAR',
        product_name: 'VARCHAR',
        product_name_de: 'VARCHAR',
        product_name_fr: 'VARCHAR',
        product_name_it: 'VARCHAR',
        product_name_en: 'VARCHAR',
        brands: 'VARCHAR',
        product_quantity: 'VARCHAR',
        product_quantity_unit: 'VARCHAR',
        countries_tags: 'VARCHAR[]',
        categories_tags: 'VARCHAR[]'
      })
    WHERE code IS NOT NULL
      AND length(code) BETWEEN 6 AND 64
      AND countries_tags IS NOT NULL
      AND len(list_filter(countries_tags, t -> regexp_matches(t, '${COUNTRIES}'))) > 0
      -- Rows the database itself flags as filed under the wrong product type. 222 of them in our
      -- markets, and they are wrong rather than merely irrelevant.
      AND NOT coalesce(list_contains(categories_tags, 'en:incorrect-product-type'), false)
      -- ISBN ranges. Books carry a GTIN and are therefore scannable, but a pantry that answers
      -- 'Der Zauberberg' to a barcode is not being helpful, and there are ~1,300 of them.
      AND substr(code, 1, 3) NOT IN ('978', '979')
  )
  SELECT
    code AS barcode,
    nullif(trim(product_name_de), '') AS name_de,
    nullif(trim(product_name_fr), '') AS name_fr,
    nullif(trim(product_name_it), '') AS name_it,
    -- Same last-resort fold as the Parquet path, here from the flat unlocalised column.
    coalesce(nullif(trim(product_name_en), ''), nullif(trim(product_name), '')) AS name_en,
    nullif(trim(brands), '') AS brand,
    try_cast(product_quantity AS DECIMAL(12,3)) AS quantity,
    nullif(trim(product_quantity_unit), '') AS quantity_unit
  FROM filtered
  WHERE coalesce(
    nullif(trim(product_name_de), ''), nullif(trim(product_name_fr), ''),
    nullif(trim(product_name_it), ''), nullif(trim(product_name_en), ''),
    nullif(trim(product_name), '')) IS NOT NULL
) TO '${out}' (FORMAT JSON, ARRAY false);
"
}

declare -a IMPORTS=()

IFS=',' read -ra REQUESTED <<< "${FLAVOURS}"

for flavour in "${REQUESTED[@]}"; do
  flavour="$(echo "${flavour}" | tr -d '[:space:]')"
  [ -z "${flavour}" ] && continue

  out="${OUTDIR}/off-catalog-${flavour}-${STAMP}.ndjson"

  case "${flavour}" in
    food)
      extract_parquet food \
        "${OFF_PARQUET_URL:-hf://datasets/openfoodfacts/product-database/food.parquet}" "${out}"
      source_id="openfoodfacts"
      ;;
    beauty)
      extract_parquet beauty \
        "${OBF_PARQUET_URL:-hf://datasets/openfoodfacts/product-database/beauty.parquet}" "${out}"
      source_id="openbeautyfacts"
      ;;
    products)
      extract_jsonl products \
        "${OPF_JSONL_URL:-https://static.openproductsfacts.org/data/openproductsfacts-products.jsonl.gz}" \
        "${out}"
      source_id="openproductsfacts"
      ;;
    *)
      echo "Unknown flavour '${flavour}'. Expected one of: food, beauty, products." >&2
      exit 1
      ;;
  esac

  echo "Done: $(wc -l < "${out}") rows in ${out}"
  echo
  IMPORTS+=("${flavour}|${out}|${source_id}")
done

echo "Now load them. Each flavour goes in under its own source, which is what decides the database"
echo "a client credits and the product page it links to."
echo
echo "The easy way is the moderation console: sign in as an administrator, open Product catalog,"
echo "and use 'Load an extract file'. Pick the file, pick the database, press Load. It posts the"
echo "file in pieces and shows you how far along it is, which is the part curl below has to be"
echo "talked into doing."
echo
echo "With curl, the file has to be split first. A request body is capped at 30 MB by default and"
echo "the gateway in front of the service caps it too, so a food extract posted in one piece is"
echo "rejected before a single row is read. 'split -C 4m' cuts on line boundaries, so every piece"
echo "is valid NDJSON on its own:"
echo

for entry in "${IMPORTS[@]}"; do
  IFS='|' read -r flavour out source_id <<< "${entry}"
  echo "  split -C 4m -d \"${out}\" \"${out}.part.\""
  echo "  for part in \"${out}\".part.*; do"
  echo "    curl --fail-with-body -X POST \\"
  echo "      \"\$INSTANCE_URL/api/v1/pantry/catalog/import?source=${source_id}&sourceVersion=${flavour}-${STAMP}\" \\"
  echo "      -H \"Authorization: Bearer \$ADMIN_TOKEN\" \\"
  echo "      -H \"Content-Type: application/x-ndjson\" \\"
  echo "      --data-binary @\"\$part\" || break"
  echo "  done"
  echo
done

echo "The import is idempotent: it upserts on barcode, so re-running it is safe and a half-finished"
echo "one is fixed by running it again. It commits in small batches rather than reloading the table,"
echo "so scans keep resolving while it runs."
echo
echo "Contains information from Open Food Facts, Open Beauty Facts and Open Products Facts, which is"
echo "made available here under the Open Database License (ODbL)."
