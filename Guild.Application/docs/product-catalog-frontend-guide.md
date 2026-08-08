# Product catalog: search and scan - frontend contract

Base URL: `https://api.venta.gg`

Everything below is live on the gateway. Guild endpoints are reached under `/api/v1/guild/**`; the
gateway strips the `guild` segment before forwarding, so the service-internal paths you may see in
other docs (`/api/v1/pantry/...`) are the same routes without the prefix. **Use the `/api/v1/guild/`
form.**

All responses are `application/json` with **camelCase** property names. Dates are ISO-8601 UTC.

---

## 1. Search products by keyword

The one you asked for. Searches this instance's own copy of the product catalog and covers **every**
product database at once - groceries, cosmetics, cleaning and household products - because they all
live in one table.

```
GET /api/v1/guild/pantry/catalog/search?q={term}&limit={n}&offset={n}
Authorization: Bearer {token}
Accept-Language: de-CH,de;q=0.9,fr;q=0.8
```

### Query parameters

| Param | Type | Required | Default | Notes |
|---|---|---|---|---|
| `q` | string | yes | - | Minimum **3 characters**. Shorter returns an empty result set, not an error. Matches product names in de/fr/it/en and the brand, case-insensitively, anywhere in the string. `%` and `_` are treated literally. |
| `limit` | int | no | `25` | Clamped to 1-100. |
| `offset` | int | no | `0` | For paging. Ordering is stable (by barcode), so paging is safe. |

`Accept-Language` decides which language the `name` comes back in. Region is ignored (`de-CH` and
`de-DE` both mean German). Weights are honoured. If you send nothing, the order tried is de, fr, it,
en.

### Response `200 OK`

```json
{
  "query": "shampoo",
  "results": [
    {
      "barcode": "3600523351893",
      "name": "Elsève Total Repair Shampoo",
      "language": "de",
      "brand": "L'Oréal",
      "quantity": 250,
      "quantityUnit": "ml",
      "source": "openbeautyfacts",
      "sourceName": "Open Beauty Facts",
      "sourceUrl": "https://world.openbeautyfacts.org/product/3600523351893",
      "license": "Open Database License (ODbL) v1.0",
      "licenseUrl": "https://opendatacommons.org/licenses/odbl/1-0/",
      "attribution": "Contains information from Open Beauty Facts, which is made available here under the Open Database License (ODbL).",
      "importedAt": "2026-08-08T06:12:44Z"
    }
  ],
  "count": 37,
  "countIsLowerBound": false,
  "limit": 25,
  "offset": 0,
  "attribution": "Contains information from Open Food Facts and Open Beauty Facts, which is made available here under the Open Database License (ODbL).",
  "license": "Open Database License (ODbL) v1.0",
  "licenseUrl": "https://opendatacommons.org/licenses/odbl/1-0/"
}
```

### Fields you need to handle

- **`barcode`** is the EAN/GTIN. This is the key for everything else: pass it to the scan endpoint,
  deep-link to `sourceUrl`, or store it.
- **`language`** is the language the `name` is actually in, which is **not always what you asked
  for**. A French-speaking flat searching for a product the database only has in German gets the
  German name and this field says `"de"`. Show the name anyway; a wrong-language name is one the user
  can read off the packet, a missing one costs them typing.
- **`quantity` / `quantityUnit`** are the **pack size on the packaging** (250 ml), not a stock count.
  Null far more often than not - about 13% of general products and 33% of cosmetics carry one. Use it
  as a suggestion ("250 ml?"), never as something you fill in silently.
- **`brand`** is free text from the source and is inconsistently cased and sometimes comma-joined
  (`"Nutella, Ferrero"`). Display as-is; do not try to parse it.
- **`countIsLowerBound`**: when `true`, `count` is capped and means "at least this many". Render
  `"500+"`, not `"500"`.

### Attribution - this one is not optional

The data is licensed under ODbL, which obliges us to credit the database wherever a name from it is
shown. **Render `attribution` under any list or detail view that displays these names**, and link
`licenseUrl`. The per-result `attribution` credits that one product's database; the top-level one
credits every database on the page. Either is sufficient; the top-level one is usually what you
want under a result list.

`sourceName` differs per row (`Open Food Facts`, `Open Beauty Facts`, `Open Products Facts`,
`Open Pet Food Facts`) - do not hardcode it, and do not assume `sourceUrl`'s host.

### Other statuses

| Status | When |
|---|---|
| `200` | Always, including no matches (`results: []`, `count: 0`). A short or empty `q` is also a 200 with an empty list. |
| `401` | Missing or expired token. |

### Notes for the UI

- **Debounce at 300 ms or more and do not search per keystroke.** The query is indexed and fast, but
  there is no reason to issue eight requests for one word.
- There is **no live third-party search behind this**. What is not in our catalog will not be found
  by searching harder. The scan path (below) is what reaches the live source, by barcode.
- Coverage is honest but uneven: groceries and cosmetics are good, cleaning products are thin, and
  Swiss retailer own-brands (M-Budget, Prix Garantie, Denner) are largely absent from open data. If a
  search comes up empty, offer "add manually" rather than implying the product does not exist.

---

## 2. Scan a barcode (existing endpoint, one additive change)

```
POST /api/v1/guild/channels/{channelId}/pantry-items/scan
Authorization: Bearer {token}
Accept-Language: de-CH,de;q=0.9
Content-Type: application/json

{ "barcode": "7617027080224", "quantity": 1, "name": null, "unit": null, "expiresAt": null }
```

Request fields: `barcode` (required), `quantity` (null falls back to what the house learned this
code means, then to 1), `name` (required only the first time a code is seen in this guild; sent
later it corrects and re-teaches the label), `unit`, `expiresAt`.

The response's `catalog` object is **the same shape as one search result**, so you can share the
rendering component.

```json
{
  "item": { "id": "...", "name": "Cornflakes", "quantity": 1 },
  "created": true,
  "learned": false,
  "catalog": {
    "barcode": "7617027080224",
    "name": "Cornflakes",
    "language": "de",
    "brand": "M-Budget, Migros",
    "quantity": 380,
    "quantityUnit": "g",
    "source": "openfoodfacts",
    "sourceName": "Open Food Facts",
    "sourceUrl": "https://world.openfoodfacts.org/product/7617027080224",
    "license": "Open Database License (ODbL) v1.0",
    "licenseUrl": "https://opendatacommons.org/licenses/odbl/1-0/",
    "attribution": "Contains information from Open Food Facts, which is made available here under the Open Database License (ODbL).",
    "importedAt": "2026-08-08T06:12:44Z"
  }
}
```

**What changed:** `catalog.barcode` is new (it was previously implicit), and `sourceName` /
`sourceUrl` / `attribution` now vary per database instead of always saying Open Food Facts. Both are
additive - nothing was removed or renamed.

- `catalog: null` means we could not resolve the barcode. Prompt for a name; that name is stored per
  guild and beats the catalog on every future scan in that household.
- `created` separates "added a new row" from "topped up the jar you already had" - they want
  different confirmations.
- `learned: true` means the name came from this household's own memory, not the catalog. When
  `learned` is true, `catalog` is null and **no attribution is owed**. The two are mutually
  exclusive: a catalog hit deliberately teaches the house nothing, so `learned` stays false until
  somebody confirms or corrects the name.

---

## 3. Catalog status (optional, no auth)

```
GET /api/v1/guild/pantry/catalog
```

Returns row counts per database and when the catalog was last refreshed. Useful for a settings or
about screen.

```json
{
  "source": "openfoodfacts",
  "sourceName": "Open Food Facts",
  "sources": [
    { "source": "openfoodfacts", "sourceName": "Open Food Facts", "sourceUrl": "https://world.openfoodfacts.org", "attribution": "...", "count": 128441 },
    { "source": "openbeautyfacts", "sourceName": "Open Beauty Facts", "sourceUrl": "https://world.openbeautyfacts.org", "attribution": "...", "count": 19290 }
  ],
  "count": 147731,
  "lastImportedAt": "2026-08-08T04:00:00Z",
  "sourceVersions": ["beauty-2026-08-08", "food-2026-08-01"],
  "exportUrl": "/api/v1/pantry/catalog/export",
  "notice": "..."
}
```

Note `exportUrl` is the **service-internal** path. Publicly it is
`https://api.venta.gg/api/v1/guild/pantry/catalog/export`. It exists to satisfy an ODbL obligation
and is not something the app needs to link.

---

## Quick reference

| Method | Public URL | Auth |
|---|---|---|
| `GET` | `https://api.venta.gg/api/v1/guild/pantry/catalog/search?q=` | Bearer |
| `POST` | `https://api.venta.gg/api/v1/guild/channels/{channelId}/pantry-items/scan` | Bearer |
| `GET` | `https://api.venta.gg/api/v1/guild/pantry/catalog` | none |
| `GET` | `https://api.venta.gg/api/v1/guild/pantry/catalog/export` | none |
