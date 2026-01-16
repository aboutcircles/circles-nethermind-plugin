# RPC reference

The plugin adds a JSON-RPC module called **`Circles`** to Nethermind.

- **Endpoint** (compose default): `http://localhost:8545`
- **Methods**: `circles_*` and `circlesV2_*`

Most methods return numbers as **strings** (especially big integers) to avoid JSON number limits.

For copy/paste requests, also see:

- [`../general-example-requests.md`](../general-example-requests.md)
- [`../v1-example-requests.md`](../v1-example-requests.md)
- [`../v2-example-requests.md`](../v2-example-requests.md)

---

## Health & schema discovery

### `circles_health()`

Quick “is the plugin alive / DB reachable?” check.

```bash
curl -s http://localhost:8545/ \
  -H 'content-type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"circles_health","params":[]}' | jq
```

### `circles_tables()`

Returns all indexed tables and columns grouped by namespace.

The response is an array of `DatabaseNamespace`:

- `Namespace` (string)
- `Tables[]`: `{ Table, Topic, Columns[] }`
- `Columns[]`: `{ Column, Type }`

```bash
curl -s http://localhost:8545/ \
  -H 'content-type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"circles_tables","params":[]}' | jq
```

Tip: there’s also a static schema snapshot at [`../all_tables.html`](../all_tables.html).

---

## Querying indexed data

### `circles_query(query)`

Queries one indexed table/view.

The request object is called `SelectDto` and supports column selection, filtering and ordering.

See **[`query-dsl.md`](query-dsl.md)** for the full DSL, including supported filter operators and pagination patterns.

---

## Event history

### `circles_events(address, fromBlock, toBlock?, eventTypes?, filters?, sortAscending?)`

Returns all Circles events affecting an address over a block range.

Notes:

- `address` can be `null` for an unfiltered query (be careful, this can get large).
- `fromBlock` can be `null`. If omitted, it defaults to **`head - 1000`**.
- `toBlock` can be `null`. If omitted, it effectively queries until the **current head**.
- `eventTypes` are strings like `"CrcV2_TransferSingle"` (format: `{Namespace}_{Table}`).
- `filters` is a list of `FilterPredicate` (combined with AND).
- Tables in namespaces starting with `V_` (views) and the `System` namespace are excluded.
- Default ordering is **descending** (`sortAscending = false`).

---

## Balances

### `circles_getTokenBalances(address)`

Returns per-token balances across v1 + v2 in a normalized shape.

Each entry includes multiple representations (`AttoCircles`, `StaticAttoCircles`, `AttoCrc`) and flags (`IsWrapped`, `IsInflationary`, `IsGroup`, …).

### `circles_getTotalBalance(address, asTimeCircles = true)`
### `circlesV2_getTotalBalance(address, asTimeCircles = true)`

Returns a string value:

- `asTimeCircles = true` (default): value formatted as **TimeCircles**
- `asTimeCircles = false`: raw integer (atto units)

---

## Trust graph helpers

### `circles_getTrustRelations(address)`

Returns incoming + outgoing trust relations for an avatar.

### `circles_getCommonTrust(address1, address2, version?)`

Returns the set of addresses that both avatars commonly trust.

---

## Profiles & tokens

### `circles_getAvatarInfo(address)` / `circles_getAvatarInfoBatch(addresses)`

Returns essential “avatar row” info (type, version, v1 token address, v2 token id, profile CID digest, …).

### `circles_getProfileCid(address)` / `circles_getProfileCidBatch(addresses)`

Returns the avatar’s profile CID (or `null` entries in batch).

### `circles_getProfileByCid(cid)` / `circles_getProfileByCidBatch(cids)`
### `circles_getProfileByAddress(avatar)` / `circles_getProfileByAddressBatch(avatars)`

Returns a denormalized profile object (name/description/image URLs, location fields, optional `shortName`, optional `avatarType`).

### `circles_searchProfiles(text, limit = 20, offset = 0, types?)`

Full-text search over profiles (name & description). If `types` is provided, results are filtered to those avatar types.

### `circles_getTokenInfo(tokenAddress)` / `circles_getTokenInfoBatch(tokenAddresses)`

Returns token metadata.

---

## Pathfinder (proxied)

### `circlesV2_findPath(flowRequest)`

Proxies `POST /findPath` to the pathfinder service.

The request is a `FlowRequest` and returns `MaxFlowResponse`:

- `MaxFlow` (string)
- `Transfers[]`: `{ From, To, TokenOwner, Value }`

The full pathfinder semantics and all request fields are documented here:

- [`../Circles.Pathfinder/README.md`](../Circles.Pathfinder/README.md)
- [`../Circles.Pathfinder/PATHFINDER.md`](../Circles.Pathfinder/PATHFINDER.md)

### `circles_getNetworkSnapshot()`

Proxies `GET /snapshot` to the pathfinder service (raw JSON).

---

## Subscriptions

### `eth_subscribe("circles", { address? })`

Streams Circles events as soon as they’re indexed.

This requires **Nethermind WebSockets** to be enabled and exposed (compose defaults only expose HTTP on `:8545`).

Example (firehose):

```bash
npx wscat -c ws://localhost:8546 -x '{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["circles",{}]}'
```

Example (filtered to one address):

```bash
npx wscat -c ws://localhost:8546 -x '{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["circles",{"address":"0xde374ece6fa50e781e81aac78e811b33d16912c7"}]}'
```