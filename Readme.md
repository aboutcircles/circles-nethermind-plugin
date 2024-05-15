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
*  `30303/tcp` (nethermind p2p)
*  `30303/udp` (nethermind p2p)
*  `8545/tcp` (nethermind rpc)
*  `5432/tcp` (postgres)
*  `9000/tcp` (consensus p2p)
*  `9000/udp` (consensus p2p)
*  `5054/tcp` (consensus metrics)

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

```bash
# Deploy the contracts
npm install && ./deploy.sh
```

#### Endpoints:

* `localhost:8545` - Nethermind RPC endpoint
* `localhost:5432` - Postgres database
* `localhost:3000` - Blockscout