# Circles Nethermind Plug-in

A [Nethermind](https://www.nethermind.io/nethermind-client) plugin to index and
query [Circles](https://www.aboutcircles.com/) protocol events.

## Quickstart

The repository contains a docker-compose file to start a Nethermind node with the Circles plugin installed. There are
configurations for Gnosis Chain, Chiado and Spaceneth (a local testnet).

The quickstart configurations use [lighthouse](https://github.com/sigp/lighthouse) as consensus engine and spin up a
postgres database to store the indexed data. The spaceneth configuration comes with a local blockscout instance.

### 1. Clone the repository

```bash
git clone https://github.com/CirclesUBI/circles-nethermind-plugin.git
cd circles-nethermind-plugin
```

### 2. Create a jwtsecret

For the use with Gnosis Chain and Chiado, a shared secret is required to authenticate requests between the execution and
consensus engine.

```bash
# Gnosis Chain
mkdir -p ./.state/jwtsecret-gnosis
openssl rand -hex 32 > ./.state/jwtsecret-gnosis/jwt.hex
```

```bash
# Chiado
mkdir -p ./.state/jwtsecret-chiado
openssl rand -hex 32 > ./.state/jwtsecret-chiado/jwt.hex
```

### 3. Set up the .env file

Copy the `.env.example` file to `.env` and adjust the values to your needs.

```bash
cp .env.example .env
```

### 4. Run node

Choose if your want to run your node on Gnosis Chain or Chiado.

```bash
# Gnosis chain
docker compose -f docker-compose.gnosis.yml up -d
```

```bash
# Chiado
docker compose -f docker-compose.chiado.yml up -d
```

That's it! The node must be fully synced before you can start querying the Circles events.
Once synced you can use the node like any other RPC node, but with the added benefit of querying Circles events directly
at the same RPC endpoint.

#### Ports:

* `30303/tcp` (nethermind p2p)
* `30303/udp` (nethermind p2p)
* `8545/tcp` (nethermind rpc)
* `5432/tcp` (postgres)
* `9000/tcp` (consensus p2p)
* `9000/udp` (consensus p2p)
* `5054/tcp` (consensus metrics)

#### Volumes

* `./.state` - Directory containing all host mapped docker volumes
    * `./.state/consensus-chiado|consensus-chiado` - Lighthouse consensus engine data
    * `./.state/nethermind-chiado|nethermind-gnosis` - Nethermind data
    * `./.state/postgres-chiado|postgres-gnosis` - Postgres data
    * `./.state/jwtsecret-chiado|jwtsecret-gnosis` - Shared secret between execution and consensus engine

### Spaceneth

The process of setting up a local only node is a bit more involved. However, by using this approach, you gain
the possibility to manipulate the node's time and don't need any xDai, which is useful for testing purposes.

```bash
docker compose -f docker-compose.spaceneth.yml up -d
```

#### Deploying the Circles contracts

Since a new spaceneth node is empty except for the genesis block, you need to deploy the Circles contracts yourself.

```bash
# Clone the Circles contracts submodules
git submodule update --init --recursive
```

To deploy the contracts, we add a script to the Circles contracts repository that deploys the contracts to the spaceneth
node.

```bash
# Add the deploy script to the Circles contracts repository
./add-deploy-script-tp-v2-repo.sh
```

As a last step, we need to replace the `tload` and `tstore` based reentrancy guards with a more classic approach.
Spaceneth does not support these instructions.

1. Open `circles-contracts-v2/src/hub/Hub.sol`
2. Replace this modifier:
   ```solidity
   modifier nonReentrant(uint8 _code) {
       assembly {
           if tload(0) { revert(0, 0) }
           tstore(0, 1)
       }
       _;
       assembly {
           tstore(0, 0)
       }
   }
   ```

   with this modifier:

   ```solidity
   bool private _reentrancyGuard;
   modifier nonReentrant(uint8 _code) {
       if (_reentrancyGuard) {
           revert CirclesReentrancyGuard(_code);
       }
       _reentrancyGuard = true;
       _;
       _reentrancyGuard = false;
   }
   ```
3. Open `circles-contracts-v2/foundry.toml`
4. Remove this line:
   ```toml
   evm_version = 'cancun'
   ```

Now you can deploy the contracts to the spaceneth node.

```bash
# Deploy the contracts
npm install && ./deploy.sh
```

#### Blockscout

You can access the blockscout instance at `http://localhost:4000`.

#### Get a funded account

You can get a funded account private key by running:

```bash
npm install
node createFundedAccount.js
```

#### Manipulate time

You can fast-forward the time by running:

```bash
curl -X POST -H "Content-Type: application/json" -d '{"fake_time": "+1d x1"}' http://localhost:5000/set_time
```

**Explanation:**

```json
{
  "fake_time": "+1d x1"
}
```

`+1d` means to offset the current time by 1 day. `x1` means that the time will pass as real time. If you want to
fast-forward the time, you can increase the number of `x` (e.g. `x10`).

_NOTE: This will restart the nethermind node._

#### Reset the spaceneth node

If you want to start over, you can reset the spaceneth node by running:

```bash
# Stop the stack
docker compose -f docker-compose.spaceneth.yml down
``` 

```bash
# Delete all persisted data
sudo rm -rf .state/nethermind-spaceneth
sudo rm -rf .state/postgres-spaceneth
sudo rm -rf .state/postgres2-spaceneth
sudo rm -rf .state/redis-spaceneth
```

```bash
# Start the stack again
docker compose -f docker-compose.spaceneth.yml up
```

## Circles RPC methods

The plugin extends the Nethermind JSON-RPC API with additional methods to query Circles events and aggregate values.  
You can find some concrete examples in the [v1-example-requests.md](v1-example-requests.md)
and [v2-example-requests.md](v2-example-requests.md) files.

### circles_getTotalBalance / circlesV2_getTotalBalance

This method allows you to query the total Circles holdings of an address.

### circles_getTokenBalance / circlesV2_getTokenBalances

This method allows you to query all individual Circles holdings of an address.

### circles_query

This method allows you to query Circles events. The method takes a single parameter, which is a JSON object with the
following properties:

* `namespace` - The protocol namespace to query (System, CrcV1 or CrcV2).
* `table` - The table to query (e.g. `Signup`, `Trust`, etc.).
* `columns` - An array of column names to return or `[]` to return all columns of the table.
* `filter` - Filters that can be used e.g. for pagination or to search for specific values.
* `order` - A list of columns to order the results by.
* `distinct` - If set to `true`, only distinct rows are returned.
* `limit` - The maximum number of rows to return (defaults to max. 1000).

_NOTE: There is no default order, so make sure to always add sensible order columns._

#### Available namespaces, tables and columns

Every table has at least the following columns:

* `blockNumber` - The block number the event was emitted in.
* `timestamp` - The unix timestamp of the event.
* `transactionIndex` - The index of the transaction in the block.
* `logIndex` - The index of the log in the transaction.

Tables for batch events have an additional `batchIndex` column.
The items of a batch are treated like individual events that can only be distinguished by the `batchIndex`.

Namespaces and tables:

* `System`
    * `Block`
* `CrcV1`
    * `HubTransfer`
    * `OrganizationSignup`
    * `Signup`
    * `Transfer`
    * `Trust`
* `CrcV2`
    * `ApprovalForAll`
    * `DiscountCost`
    * `InviteHuman`
    * `PersonalMint`
    * `RegisterGroup`
    * `RegisterHuman`
    * `RegisterOrganization`
    * `RegisterShortName`
    * `Stopped`
    * `TransferBatch`
    * `TransferSingle`
    * `Trust`
    * `UpdateMetadataDigest`
    * `URI`
* `V_CrcV2`
    * `Transfers` (view combining `TransferBatch` and `TransferSingle`)
    * `Avatars` (view combining `RegisterHuman`, `InviteHuman`, `RegisterGroup` and `RegisterOrganization`)
    * `TrustRelations` (view filtered to represent all current `Trust` relations)

#### Available filter types

* `Equals`
* `NotEquals`
* `GreaterThan`
* `GreaterThanOrEquals`
* `LessThan`
* `LessThanOrEquals`
* `Like`
* `NotLike`
* `In`
* `NotIn`

#### Pagination

You can use the combination of `blockNumber`, `transactionIndex` and `logIndex`
(+ `batchIndex` in the case of batch events) together with a `limit` to paginate through the results.