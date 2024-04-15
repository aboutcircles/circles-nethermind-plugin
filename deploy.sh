RPC_URL=http://localhost:8545
CHAIN_ID=59122

echo "Creating funded EOA..."
PRIVATE_KEY=$(node createFundedAccount.js)

echo "Deploying v1 contracts ..."

V1_HUB_INFLATION="107"
V1_HUB_PERIOD="31556952"
V1_HUB_SYMBOL="CRC"
V1_HUB_NAME="Circles"
V1_HUB_SIGNUP_BONUS="50000000000000000000"
V1_HUB_INITIAL_ISSUANCE="92592592592592"
V1_HUB_TIMEOUT="7776000"

cd circles-contracts

npm install @openzeppelin/contracts@^3.4.0-solc-0.7
npm install @gnosis.pm/safe-contracts@^1.3.0
npm install @circles/safe-contracts@=1.0.14

cd contracts

V1_HUB_DEPLOYMENT=$(forge create Hub \
  --rpc-url ${RPC_URL} \
  --private-key ${PRIVATE_KEY} \
  --constructor-args ${V1_HUB_INFLATION} ${V1_HUB_PERIOD} ${V1_HUB_SYMBOL} ${V1_HUB_NAME} ${V1_HUB_SIGNUP_BONUS} ${V1_HUB_INITIAL_ISSUANCE} ${V1_HUB_TIMEOUT})

V1_HUB_ADDRESS=$(echo "$V1_HUB_DEPLOYMENT" | grep "Deployed to:" | awk '{print $3}')
echo "V1 Hub deployed at ${V1_HUB_ADDRESS}"

# Set the current date at 00:00 as unix time
INFLATION_DAY_ZERO=$(date -d "00:00" +%s)
BOOTSTRAP_ONE_YEAR=31540000

cd ../..

echo "Writing .env file to circles-contracts-v2/.env ..."
cat <<EOF > circles-contracts-v2/.env
PRIVATE_KEY=$PRIVATE_KEY
RPC_URL=$RPC_URL
CHAIN_ID=$CHAIN_ID
V1_HUB_ADDRESS=$V1_HUB_ADDRESS
INFLATION_DAY_ZERO=$INFLATION_DAY_ZERO
BOOTSTRAP_ONE_YEAR=$BOOTSTRAP_ONE_YEAR
VERIFIER_URL=https://localhost:8043
EOF

cd circles-contracts-v2/script/deployments/

echo "Installing dependencies ..."
npm i

echo "Deploying Circles contracts ..."
./genericDeploy.sh

cd ../../..

echo "Use this private key to interact with the contracts:"
PRIVATE_KEY=$(node createFundedAccount.js)
echo "PRIVATE_KEY=${PRIVATE_KEY}"
