# Catalog consolidation

Merges seller product feeds into `catalog.db`. Duplicate products are not inserted; the existing product is linked to the seller in `SellerProduct`.

```
dotnet run --project src/VtexChallenge -- [entries.json] [catalog.db]
dotnet test
```
Defaults: `ProductEntry.json`, `catalog.db`. The run modifies the db file, so try it on a copy first. Each run prints an **import id**; save it, it identifies everything that run created.

## Decisions on the ambiguities

| Question | Decision |
|---|---|
| What is "the same product"? | Normalized brand + normalized name. Category ignored (sellers disagree, e.g. Photo vs Photography). |
| Normalization | trim, collapse spaces, lowercase, strip accents, drop quote/inch marks (`55`, `55"`, `12.9''`), tiny PT to EN map (`Processador`, `Roteador`). |
| Missing brand | Falls back to name only, but only if exactly one candidate (no false merges). |
| Fuzzy matching | Not used. Would risk merging distinct items (Hockey Skates vs Hockey Stick). |
| Seller `Id` (GUID) vs `SellerProductId INTEGER` | Column changed to TEXT. Id is unique per seller, not global (many GUIDs repeat across sellers). |
| Invalid GUIDs / id reused by a seller for another product | Row rejected and reported. |
| Same seller, same product, different ids | One link kept (first), no new link, not reported. |
| Re-running the same file | Idempotent. |
| Existing product with missing brand/category | Gaps filled from feed, existing values never overwritten. |
| Malicious string in a feed | All writes are parameterized (EF Core); stored verbatim. |

## Schema changes (`SchemaUpgrader`, tracked by `PRAGMA user_version`)
- `SellerProduct.SellerProductId` INTEGER to TEXT, unique `(SellerName, SellerProductId)` and `(SellerName, ProductId)`.
- `Product.NormalizedKey` + index, backfilled for existing rows.

## Import state and failures
`ImportRun.State` is `Running`, `Completed` or `Failed` (`Error` holds the reason). The run row is committed before the data transaction, so a failed import leaves a record even though all its data is rolled back (imports are all or nothing). Failures recorded: malformed or non-array JSON, database errors. A run stuck in `Running` means the process was killed (including Ctrl+C, which is not handled).

Checked before importing: entries file exists, database file exists (SQLite would otherwise create an empty one), file is a SQLite database, schema is not newer than the tool.

Exit codes: `0` completed, `1` import failed (nothing saved), `2` bad input or database.

## Undoing an import
Each run inserts an `ImportRun` row (id, source, timestamps, counters). Every `Product`, `SellerProduct` and `RejectedEntry` created by that run carries its `ImportId`. To remove an import (order matters because of the FK):
```sql
DELETE FROM SellerProduct WHERE ImportId = '<id>';
DELETE FROM Product       WHERE ImportId = '<id>';
DELETE FROM RejectedEntry WHERE ImportId = '<id>';
DELETE FROM ImportRun     WHERE Id = '<id>';
```
Brand/category gaps filled on products that already existed are not reverted.

## Memory and scale
Neither the feed nor the catalog is loaded whole. The feed is streamed and processed in chunks of 500 entries. For each chunk only the needed rows are fetched: products sharing a normalized name (`Product.NormalizedName`, indexed) and the sellers' links using the chunk's ids or pointing at those products. Each chunk is saved and the EF change tracker cleared, so memory is bounded by the chunk size. Cost is a few queries per chunk.

## Known limits
Synonym map is hand written. The import runs in a single transaction, so a very large feed holds the SQLite write lock for its duration. Not built for scale beyond that, per the challenge brief.
