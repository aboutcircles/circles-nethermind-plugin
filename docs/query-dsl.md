# Query DSL (`circles_query`)

`circles_query` lets you query any indexed table or view via a JSON DSL.

Good to know up front:

- Request property names are **case-insensitive** (`Namespace` vs `namespace` both work).
- If you don’t know what’s available, call `circles_tables()` first.
- If you omit or exceed `Limit`, the server caps results at **1000 rows**.

---

## Request shape

`circles_query(query: SelectDto)`

`SelectDto` fields:

- `Namespace` (string, required)
- `Table` (string, required)
- `Columns` (string[], optional)
  - `[]` means: all columns (`SELECT *`)
- `Filter` (IFilterPredicateDto[], optional)
- `Order` (OrderByDto[], optional)
- `Limit` (number, optional)
  - `1..1000` is respected
  - `null`, `<=0`, or `>1000` becomes `1000`
- `Distinct` (bool, optional; default `false`)

Example (latest avatars):

```bash
curl -s http://localhost:8545/ \
  -H 'content-type: application/json' \
  --data '{
    "jsonrpc":"2.0",
    "id":1,
    "method":"circles_query",
    "params":[{
      "Namespace":"V_Crc",
      "Table":"Avatars",
      "Columns":[],
      "Filter":[],
      "Order":[
        {"Column":"blockNumber","SortOrder":"DESC"},
        {"Column":"transactionIndex","SortOrder":"DESC"},
        {"Column":"logIndex","SortOrder":"DESC"}
      ],
      "Limit":25
    }]
  }' | jq
```

---

## Ordering

`Order` is a list of:

```json
{ "Column": "blockNumber", "SortOrder": "DESC" }
```

Notes:

- Any `SortOrder` that’s not `"DESC"` is treated as `"ASC"`.
- There is **no default order**. If you care about determinism/pagination, always set an explicit order.

---

## Filtering

`Filter` is an array of predicates. Top-level filter entries are effectively combined with **AND**.

There are two predicate types:

### 1) `FilterPredicate`

```json
{
  "Type": "FilterPredicate",
  "Column": "blockNumber",
  "FilterType": "GreaterThanOrEquals",
  "Value": 12000000
}
```

Supported `FilterType` values:

- `Equals`, `NotEquals`
- `GreaterThan`, `GreaterThanOrEquals`
- `LessThan`, `LessThanOrEquals`
- `Like`, `ILike`, `NotLike`
- `In`, `NotIn`
- `IsNull`, `IsNotNull`

### 2) `Conjunction`

Use conjunctions to group predicates with `And` / `Or`.

```json
{
  "Type": "Conjunction",
  "ConjunctionType": "Or",
  "Predicates": [
    { "Type": "FilterPredicate", "Column": "from", "FilterType": "Equals", "Value": "0x..." },
    { "Type": "FilterPredicate", "Column": "to",   "FilterType": "Equals", "Value": "0x..." }
  ]
}
```

Important constraint: filter columns must exist in the table schema, otherwise the query fails.

---

## Pagination pattern

Most event tables can be paginated with keyset pagination using the standard tuple:

- `blockNumber`
- `transactionIndex`
- `logIndex`

Some batch-style tables also have `batchIndex`.

Typical pattern:

1) Order by those columns descending.
2) Add an “older-than” filter that matches the last row of the previous page.

Pseudo filter for “everything older than (b, tx, log)”: (expressed as nested conjunctions)

```
(blockNumber < b)
OR (blockNumber = b AND transactionIndex < tx)
OR (blockNumber = b AND transactionIndex = tx AND logIndex < log)
```

You can see a full working example of this pattern in [`../general-example-requests.md`](../general-example-requests.md).

---

## Discovering schema

- Live: `circles_tables()`
- Static snapshot: [`../all_tables.html`](../all_tables.html)

If you need a stable target for codegen/tests, pin to `all_tables.html`. For runtime discovery, prefer `circles_tables()`.

---

## Response shape

The response is a table-like object:

```json
{
  "columns": ["blockNumber", "timestamp", "..."],
  "rows": [
    [123, 1700000000, "0x..."],
    ...
  ]
}
```

Big integer fields are serialized as **strings**.
