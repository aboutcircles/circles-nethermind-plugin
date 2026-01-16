# Trust Missing Avatars (Circles v2)

This helper finds **human avatars** that are trusted by at least one BaseGroup but are **not yet trusted by the router** and then calls:

- `enableCRCForRouting(baseGroup, crcArray)`

in batches (default `50`) until no missing avatars remain.

## Environment variables

Required:

* `DATABASE_URL` Postgres connection string
* `RPC_URL` JSON-RPC URL
* `PRIVATE_KEY` EOA private key (hex, with or without `0x`)
* `ROUTER_ADDRESS` router contract address

Optional:

* `BATCH_SIZE` (default `50`, must be in `[1..200]`)
* `GAS_LIMIT` (fixed gas limit; if unset, gas is estimated)
* `CONFIRMATIONS` (default `1`)

## Example `.env`

```env
DATABASE_URL=postgres://user:pass@localhost:5432/db
RPC_URL=https://your.rpc
PRIVATE_KEY=0xabc...
ROUTER_ADDRESS=0x...
BATCH_SIZE=50
GAS_LIMIT=
CONFIRMATIONS=1
```

## Run

From repo root:

```bash
dotnet run --project Circles.Index.TrustMissingAvatars/Circles.Index.TrustMissingAvatars.csproj
```

## Failure handling

* Sends transactions per BaseGroup, in batches.
* If a batch fails, bisects recursively to isolate failing avatars.
* Failing avatars are reported with the full exception text and the run stops with an error.
* Ctrl+C cancels the run.
