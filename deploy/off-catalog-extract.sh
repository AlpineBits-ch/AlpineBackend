#!/usr/bin/env bash
#
# off-catalog-extract.sh - build the pantry product catalog extract from Open Food Facts.
#
# WHAT THIS IS
#
#   The pantry resolves scanned barcodes to product names from a local table. That table is loaded
#   from a filtered extract of Open Food Facts, and this script is what produces the extract. It
#   downloads the published Parquet export, filters it to the markets we serve, projects the seven
#   columns the pantry stores, and writes newline-delimited JSON that
#   POST /api/v1/pantry/catalog/import reads directly.
#
# WHY BULK IS THE RECOMMENDED WAY TO POPULATE, AND WHY IT IS NOT A PREREQUISITE
#
#   The service also asks the API about single barcodes, inline on a scan and again on a background
#   sweep, so an instance that never runs this script still works: the first scan of a common
#   grocery resolves live, and anything else asks the user for a name exactly as it did before the
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
# TESTING AGAINST THE STAGING ENVIRONMENT
#
#   Open Food Facts run a staging instance at https://world.openfoodfacts.net (HTTP basic auth
#   off / off) and ask that applications be tested against it rather than production. Point
#   PRODUCT_CATALOG_API_BASE_URL there for any manual verification of the live lookup. This script
#   reads the published Parquet export rather than the API and is unaffected.
#
# WHY THIS IS A SCRIPT AND NOT CODE IN THE SERVICE
#
#   The obvious alternative is to take a Parquet reader (Parquet.Net) into Guild.Application and do
#   all of this in-process, on a schedule, with no human involved. It was rejected.
#
#   The full export is ~7.2 GiB compressed and ~21 GiB decompressed, 4.6 million rows, and the
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
#   Open Food Facts data is licensed under the Open Database License (ODbL) v1.0. The table this
#   produces is a Derivative Database, share-alike attaches to it, and ODbL 4.6 obliges us to offer
#   recipients a free machine-readable copy - which is what GET /api/v1/pantry/catalog/export
#   serves. Two rules follow and neither is negotiable:
#
#     1. The extract contains product data and nothing else. No household data, no user-entered
#        names, nothing about who scanned what. It lands in its own table, product_catalog_entries,
#        which is never merged with pantry_barcodes (the table of names households typed).
#     2. Anywhere a catalog-sourced name is shown, the source and the licence are shown with it.
#        The API does this by returning them on the scan response.
#
#   Images are NOT extracted. They are CC-BY-SA 3.0 rather than ODbL, which is a second share-alike
#   regime with per-photo attribution, and Open Food Facts warns that packaging and trademark rights
#   in them may belong to third parties it cannot license. Adding an image column here is a separate
#   decision, not a tweak.
#
# REQUIREMENTS
#
#   duckdb (a single static binary, https://duckdb.org). Chosen because it reads remote Parquet with
#   projection and predicate pushdown, so the 7.2 GiB is streamed and filtered rather than
#   materialised, and because it is one binary with no runtime to install.
#
# USAGE
#
#   ./off-catalog-extract.sh [output.ndjson] [country-regex]
#
#   Defaults to off-catalog-$(date +%F).ndjson and to Switzerland, Germany, Austria and France.
#   Switzerland alone is on the order of tens of megabytes; adding DACH and France brings it to a
#   few hundred.
#
#   Then load it (instance administrator only; sourceVersion is what the 4.6 export reports as the
#   snapshot a row came from):
#
#     curl -X POST "$INSTANCE_URL/api/v1/pantry/catalog/import?sourceVersion=off-$(date +%F)" \
#          -H "Authorization: Bearer $ADMIN_TOKEN" \
#          -H "Content-Type: application/x-ndjson" \
#          --data-binary @off-catalog-$(date +%F).ndjson
#
#   The import is idempotent: it upserts on barcode, so re-running it is safe and a half-finished
#   one is fixed by running it again. It commits in small batches rather than reloading the table,
#   so scans keep resolving while it runs.
#
set -euo pipefail

OUTPUT="${1:-off-catalog-$(date +%F).ndjson}"
COUNTRIES="${2:-en:switzerland|en:germany|en:austria|en:france}"
SOURCE_URL="${OFF_PARQUET_URL:-hf://datasets/openfoodfacts/product-database/food.parquet}"

command -v duckdb >/dev/null 2>&1 || {
  echo "duckdb is required: https://duckdb.org/docs/installation/" >&2
  exit 1
}

echo "Reading  $SOURCE_URL"
echo "Filter   countries matching /$COUNTRIES/"
echo "Writing  $OUTPUT"

duckdb -c "
COPY (
  WITH filtered AS (
    SELECT
      code,
      product_name,
      brands,
      product_quantity,
      product_quantity_unit
    FROM read_parquet('${SOURCE_URL}')
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
      nullif(trim(list_extract(list_filter(product_name, n -> n.lang = 'en'), 1).text), '') AS name_en,
      nullif(trim(brands), '') AS brand,
      try_cast(product_quantity AS DECIMAL(12,3)) AS quantity,
      nullif(trim(product_quantity_unit), '') AS quantity_unit
    FROM filtered
  )
  SELECT * FROM named
  -- A row with no name in any of the four languages can never fill anything, so it is dropped here
  -- rather than shipped and skipped at import. Measured: about 4.6% of the Swiss subset.
  WHERE coalesce(name_de, name_fr, name_it, name_en) IS NOT NULL
  -- A pack size of zero or less is not a pack size; null is the normal case, since only 43.5% of
  -- Swiss products carry a quantity at all.
) TO '${OUTPUT}' (FORMAT JSON, ARRAY false);
"

echo
echo "Done: $(wc -l < "${OUTPUT}") rows in ${OUTPUT}"
echo "Contains information from Open Food Facts, which is made available here under the Open Database License (ODbL)."
