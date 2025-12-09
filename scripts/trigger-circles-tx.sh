#!/usr/bin/env bash
# trigger-circles-tx.sh
# Runs a Circles transaction trigger script (no permanent install required)
# Supports both EOA and Safe wallets (circles.garden style)

set -e

# Ensure the seed phrase is available
if [[ -z "$CIRCLES_SEED_PHRASE" ]]; then
    echo "Error: CIRCLES_SEED_PHRASE is not set." >&2
    echo "Source your .env.local or export the variable before running." >&2
    exit 1
fi

# Create a temp directory for the script execution
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

# Create minimal package.json (includes Safe SDK for Safe wallet support)
cat > "$TEMP_DIR/package.json" << 'EOF'
{"type":"module","dependencies":{"ethers":"^6","bip39":"^3","@safe-global/protocol-kit":"^5","@safe-global/types-kit":"^1"}}
EOF

# Create the script
cat > "$TEMP_DIR/run.mjs" << 'SCRIPT'
import { ethers } from 'ethers';
import { mnemonicToEntropy, validateMnemonic } from 'bip39';
import Safe from '@safe-global/protocol-kit';

const SEED_PHRASE = process.env.CIRCLES_SEED_PHRASE;
const RPC_URL = process.env.CIRCLES_RPC_URL || 'https://rpc.gnosischain.com';
const HUB_V2_ADDRESS = process.env.CIRCLES_HUB_ADDRESS || '0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8';
const TRANSFER_AMOUNT = process.env.CIRCLES_AMOUNT || '1';
const RECIPIENT = process.env.CIRCLES_RECIPIENT;
const SAFE_ADDRESS = process.env.CIRCLES_SAFE_ADDRESS; // Optional: explicitly set Safe address

const HUB_V2_ABI = [
  'function safeTransferFrom(address from, address to, uint256 id, uint256 value, bytes data)',
  'function balanceOf(address account, uint256 id) view returns (uint256)',
];

// circles.garden uses bip39.mnemonicToEntropy directly as the private key
function derivePrivateKey(phrase) {
  return mnemonicToEntropy(phrase.trim());
}

async function findSafeForOwner(provider, ownerAddress) {
  // Query the Circles RPC to find avatar info - if it's a Safe, we can detect it
  // For now, we'll check a known pattern: try to get code at potential Safe addresses
  // A simpler approach: check if the owner has deployed a Safe via the Safe factory

  // Use Circles RPC to find the avatar
  const circlesRpc = process.env.CIRCLES_RPC_URL?.includes('aboutcircles')
    ? process.env.CIRCLES_RPC_URL
    : 'https://rpc.aboutcircles.com';

  try {
    const response = await fetch(circlesRpc, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'circles_query',
        params: [{
          Namespace: 'V_Crc',
          Table: 'Avatars',
          Columns: ['avatar'],
          Filter: [{
            Type: 'Conjunction',
            ConjunctionType: 'Or',
            Predicates: [
              { Type: 'FilterPredicate', FilterType: 'Equals', Column: 'avatar', Value: [ownerAddress.toLowerCase()] }
            ]
          }],
          Limit: 1
        }],
        id: 1
      })
    });
    const data = await response.json();
    if (data.result?.Rows?.length > 0) {
      return null; // Owner is directly registered, no Safe needed
    }
  } catch (e) {
    // Ignore errors, continue with Safe detection
  }

  // Check if there's a Safe registered with circles that this EOA owns
  // We'll need to query for Safes - for now, require explicit CIRCLES_SAFE_ADDRESS
  return null;
}

if (!SEED_PHRASE) {
  console.error('Error: CIRCLES_SEED_PHRASE environment variable is required');
  process.exit(1);
}

if (!validateMnemonic(SEED_PHRASE.trim())) {
  console.error('Error: Invalid seed phrase');
  process.exit(1);
}

console.log('Circles Transaction Trigger');
console.log('================================');
console.log('RPC URL:', RPC_URL);
console.log('Hub V2: ', HUB_V2_ADDRESS);

const provider = new ethers.JsonRpcProvider(RPC_URL);
const privateKey = derivePrivateKey(SEED_PHRASE);
const eoaWallet = new ethers.Wallet(privateKey, provider);
const eoaAddress = await eoaWallet.getAddress();

console.log('EOA:    ', eoaAddress);

const network = await provider.getNetwork();
console.log('Network:', network.name, '(chainId:', network.chainId + ')');

// Determine if we're using a Safe or direct EOA
const safeAddress = SAFE_ADDRESS;
const useSafe = !!safeAddress;

if (useSafe) {
  console.log('Safe:   ', safeAddress);
} else {
  // Check if EOA has Circles balance directly
  const hub = new ethers.Contract(HUB_V2_ADDRESS, HUB_V2_ABI, provider);
  const eoaTokenId = BigInt(eoaAddress);
  const eoaBalance = await hub.balanceOf(eoaAddress, eoaTokenId);

  if (eoaBalance === 0n) {
    console.log('');
    console.log('EOA has no Circles. Looking for Safe...');
    console.log('Hint: Set CIRCLES_SAFE_ADDRESS if you know your Safe address.');
    console.error('');
    console.error('Error: No Circles tokens found and no Safe address provided.');
    console.error('Set CIRCLES_SAFE_ADDRESS to your Safe address in .env.local');
    process.exit(1);
  }
}

// Get the address that holds the Circles tokens
const circlesHolder = useSafe ? safeAddress : eoaAddress;
const hub = new ethers.Contract(HUB_V2_ADDRESS, HUB_V2_ABI, useSafe ? provider : eoaWallet);

const tokenId = BigInt(circlesHolder);
console.log('Token ID:', tokenId.toString());

const circlesBalance = await hub.balanceOf(circlesHolder, tokenId);
console.log('Circles Balance:', circlesBalance.toString(), '(raw)');

if (circlesBalance === 0n) {
  console.error('Error: No Circles tokens to transfer');
  console.error('   The wallet needs to have minted Circles first.');
  process.exit(1);
}

const recipient = RECIPIENT || circlesHolder; // Self-transfer if no recipient
const amount = BigInt(TRANSFER_AMOUNT);

if (amount > circlesBalance) {
  console.error('Error: Insufficient balance. Have', circlesBalance.toString(), 'want to send', amount.toString());
  process.exit(1);
}

console.log('');
console.log('Sending transaction...');
console.log('   From:  ', circlesHolder);
console.log('   To:    ', recipient);
console.log('   Amount:', amount.toString(), '(raw wei)');

let txHash, receipt;

if (useSafe) {
  // Use Safe SDK to send transaction through Safe
  const safeSdk = await Safe.default.init({
    provider: RPC_URL,
    signer: privateKey,
    safeAddress: safeAddress
  });

  // Encode the transfer call
  const hubInterface = new ethers.Interface(HUB_V2_ABI);
  const data = hubInterface.encodeFunctionData('safeTransferFrom', [
    circlesHolder, recipient, tokenId, amount, '0x'
  ]);

  const safeTransaction = await safeSdk.createTransaction({
    transactions: [{
      to: HUB_V2_ADDRESS,
      value: '0',
      data: data
    }]
  });

  const executeTxResponse = await safeSdk.executeTransaction(safeTransaction);
  txHash = executeTxResponse.hash;
  console.log('   Tx Hash:', txHash);
  console.log('');
  console.log('Waiting for confirmation...');

  receipt = await provider.waitForTransaction(txHash);
} else {
  // Direct EOA transaction
  const tx = await hub.safeTransferFrom(circlesHolder, recipient, tokenId, amount, '0x');
  txHash = tx.hash;
  console.log('   Tx Hash:', txHash);
  console.log('');
  console.log('Waiting for confirmation...');

  receipt = await tx.wait();
}

console.log('');
console.log('Transaction confirmed!');
console.log('   Block:   ', receipt.blockNumber);
console.log('   Gas Used:', receipt.gasUsed?.toString() || 'N/A');
console.log('   Status:  ', receipt.status === 1 ? 'Success' : 'Failed');
console.log('');
console.log('Done! This transaction should appear in subscription events.');
SCRIPT

# Install deps and run
cd "$TEMP_DIR"
npm install --silent 2>/dev/null
node run.mjs
