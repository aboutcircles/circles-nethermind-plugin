#!/usr/bin/env node
/**
 * Payment Gateway On-Chain Test Script
 *
 * Tests routed transfers and group minting with payment gateways on Gnosis mainnet.
 * Creates reproducible test fixtures by logging block numbers for regression tests.
 *
 * Prerequisites:
 *   npm install ethers
 *
 * Environment Variables:
 *   TX_PRIVATE_KEY     - Private key for the Safe owner account
 *   SAFE_ADDRESS       - Address of the Safe to use (must have EOA as owner)
 *   GNOSIS_RPC_URL     - RPC URL for Gnosis mainnet (default: public RPC)
 *   PATHFINDER_URL     - Pathfinder API URL (default: localhost:8080)
 *
 * Usage:
 *   node scripts/payment-gateway-test.mjs [command]
 *
 * Commands:
 *   check-safe         - Check Safe registration with Circles
 *   register-safe      - Register Safe as Circles organization
 *   deploy-gateway     - Deploy payment gateway and trust groups
 *   trust-groups       - Trust groups from existing gateway
 *   test-transfer      - Execute routed transfer to payment gateway
 *   full               - Run complete test sequence
 *   check-consented    - Check if avatar has consented flow enabled (trusts Router)
 *   enable-consented   - Enable consented flow by trusting Router from Safe
 *   analyze-tx         - Analyze transaction (hops, tokens, router involvement)
 *   help               - Show help message
 */

import { ethers } from "ethers";

// Contract addresses on Gnosis mainnet
const ADDRESSES = {
  hubV2: "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8",
  router: "0xdc287474114cc0551a81ddc2eb51783fbf34802f",
  paymentGatewayFactory: "0x186725D8fe10a573DC73144F7a317fCae5314F19",
  // Target groups for testing (small, for testing)
  groups: {
    "4Birthday": "0xaa9081197e02f2fdacfc65e7606743fa2d005208",
    MunichBazis: "0xda43d07ee6a375c96b26bbf571576228ec86f243",
  },
};

// ABIs (minimal, just what we need)
const HUB_ABI = [
  "function avatars(address) view returns (address)",
  "function isHuman(address) view returns (bool)",
  "function isGroup(address) view returns (bool)",
  "function isOrganization(address) view returns (bool)",
  "function isTrusted(address truster, address trustee) view returns (bool)",
  "function trustMarkers(address truster, address trustee) view returns (uint256 expiry)",
  "function trust(address trustee, uint96 expiry) external",
  "function registerHuman(address inviter, bytes32 metadataDigest) external",
  "function registerOrganization(string name, bytes32 metadataDigest) external",
  "function personalMint() external",
  "function operateFlowMatrix(address[] flowVertices, tuple(uint16 streamSinkId, uint192 amount)[] flow, tuple(uint16 sourceCoordinate, uint16[] flowEdgeIds, bytes data)[] streams, bytes packedCoordinates) external",
  "event RegisterHuman(address indexed avatar, address indexed inviter)",
  "event RegisterOrganization(address indexed organization, string name)",
  // ERC1155 events for transaction analysis
  "event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)",
  "event TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values)",
];

const PAYMENT_GATEWAY_FACTORY_ABI = [
  "function createGateway(string name, bytes32 metadataDigest) external returns (address)",
  // Note: Event order is (owner, gateway) not (gateway, owner)
  "event GatewayCreated(address indexed owner, address indexed gateway, string name)",
];

const PAYMENT_GATEWAY_ABI = [
  "function setTrust(address trustReceiver, uint96 expiry) external",
  "function owner() view returns (address)",
  "event Trust(address indexed truster, address indexed trustee, uint256 expiryTime)",
];

const SAFE_ABI = [
  "function getOwners() view returns (address[])",
  "function getThreshold() view returns (uint256)",
  "function execTransaction(address to, uint256 value, bytes data, uint8 operation, uint256 safeTxGas, uint256 baseGas, uint256 gasPrice, address gasToken, address refundReceiver, bytes signatures) external returns (bool)",
  "function nonce() view returns (uint256)",
];

// Configuration
function getConfig() {
  const privateKey = process.env.TX_PRIVATE_KEY;
  if (!privateKey) {
    throw new Error("TX_PRIVATE_KEY environment variable required");
  }

  const safeAddress = process.env.SAFE_ADDRESS;
  if (!safeAddress) {
    throw new Error("SAFE_ADDRESS environment variable required");
  }

  return {
    privateKey,
    safeAddress,
    rpcUrl: process.env.GNOSIS_RPC_URL || "https://rpc.gnosischain.com",
    pathfinderUrl: process.env.PATHFINDER_URL || "http://localhost:8080",
  };
}

// Read-only configuration (no private key required)
function getReadOnlyConfig() {
  return {
    rpcUrl: process.env.GNOSIS_RPC_URL || "https://rpc.gnosischain.com",
    safeAddress: process.env.SAFE_ADDRESS, // Optional for read-only
  };
}

// Helper to execute transaction from Safe with single owner
async function executeSafeTransaction(signer, safeAddress, to, data, value = 0n) {
  const safe = new ethers.Contract(safeAddress, SAFE_ABI, signer);

  // Get nonce
  const nonce = await safe.nonce();
  console.log(`  Safe nonce: ${nonce}`);

  // For threshold=1 Safe, we can use a pre-approved signature
  // Format: {32-byte r: owner address padded}{32-byte s: 0}{1-byte v: 1}
  const ownerAddress = await signer.getAddress();
  const signature =
    ethers.zeroPadValue(ownerAddress, 32) +
    ethers.zeroPadValue("0x00", 32).slice(2) +
    "01";

  console.log(`  Executing Safe transaction to ${to}...`);

  // Operation 0 = CALL
  const tx = await safe.execTransaction(
    to,
    value,
    data,
    0, // operation: CALL
    0, // safeTxGas
    0, // baseGas
    0, // gasPrice
    ethers.ZeroAddress, // gasToken
    ethers.ZeroAddress, // refundReceiver
    signature,
    { gasLimit: 2000000 }
  );

  const receipt = await tx.wait();
  console.log(`  Transaction hash: ${receipt.hash}`);
  console.log(`  Block number: ${receipt.blockNumber}`);
  console.log(`  Gas used: ${receipt.gasUsed}`);

  return receipt;
}

// Check if Safe is registered with Circles
async function checkSafeRegistration(provider, safeAddress) {
  const hub = new ethers.Contract(ADDRESSES.hubV2, HUB_ABI, provider);

  const avatar = await hub.avatars(safeAddress);
  const isHuman = await hub.isHuman(safeAddress);
  const isOrg = await hub.isOrganization(safeAddress);
  const isGroup = await hub.isGroup(safeAddress);

  return {
    isRegistered: avatar !== ethers.ZeroAddress,
    avatar,
    isHuman,
    isOrganization: isOrg,
    isGroup,
  };
}

// Register Safe as Circles organization
async function registerSafeAsOrganization(signer, safeAddress) {
  const hub = new ethers.Contract(ADDRESSES.hubV2, HUB_ABI, signer);

  const name = "Payment Gateway Test Safe";
  const metadataDigest = ethers.keccak256(ethers.toUtf8Bytes(name));

  const data = hub.interface.encodeFunctionData("registerOrganization", [
    name,
    metadataDigest,
  ]);

  console.log("Registering Safe as Circles organization...");
  return await executeSafeTransaction(signer, safeAddress, ADDRESSES.hubV2, data);
}

// Deploy payment gateway
async function deployPaymentGateway(signer, safeAddress) {
  const factory = new ethers.Contract(
    ADDRESSES.paymentGatewayFactory,
    PAYMENT_GATEWAY_FACTORY_ABI,
    signer
  );

  const name = `Test Gateway ${Date.now()}`;
  const metadataDigest = ethers.zeroPadValue("0x00", 32);

  const data = factory.interface.encodeFunctionData("createGateway", [
    name,
    metadataDigest,
  ]);

  console.log(`Deploying payment gateway "${name}"...`);
  const receipt = await executeSafeTransaction(
    signer,
    safeAddress,
    ADDRESSES.paymentGatewayFactory,
    data
  );

  // Find GatewayCreated event from factory address
  const factoryLower = ADDRESSES.paymentGatewayFactory.toLowerCase();
  for (const log of receipt.logs) {
    if (log.address.toLowerCase() === factoryLower && log.topics.length >= 3) {
      // Event: GatewayCreated(address indexed owner, address indexed gateway, string name)
      // topics[0] = event signature, topics[1] = owner, topics[2] = gateway
      const gatewayAddress = "0x" + log.topics[2].slice(26); // Extract address from 32-byte topic
      console.log(`  Gateway deployed at: ${gatewayAddress}`);
      return gatewayAddress;
    }
  }

  throw new Error("GatewayCreated event not found in receipt");
}

// Trust groups from payment gateway
async function trustGroupsFromGateway(signer, safeAddress, gatewayAddress, groups) {
  const gateway = new ethers.Contract(gatewayAddress, PAYMENT_GATEWAY_ABI, signer);

  // Trust expiry: 1 year from now
  const expiry = BigInt(Math.floor(Date.now() / 1000) + 365 * 24 * 60 * 60);

  for (const [name, groupAddress] of Object.entries(groups)) {
    console.log(`Trusting group "${name}" (${groupAddress})...`);

    const data = gateway.interface.encodeFunctionData("setTrust", [
      groupAddress,
      expiry,
    ]);

    await executeSafeTransaction(signer, safeAddress, gatewayAddress, data);
  }
}

// Compute path using pathfinder API
async function computePath(pathfinderUrl, source, sink, amount) {
  // Build URL with query parameters
  const params = new URLSearchParams({
    from: source,
    to: sink,
    amount: amount.toString(),
  });
  const url = `${pathfinderUrl}/findPath?${params}`;

  console.log(`Computing path from ${source} to ${sink}...`);
  console.log(`  Target flow: ${ethers.formatEther(amount)} CRC`);

  const response = await fetch(url);

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`Pathfinder request failed: ${response.status} ${text}`);
  }

  const result = await response.json();
  console.log(`  Max flow: ${ethers.formatEther(result.maxFlow || "0")} CRC`);
  console.log(`  Transfer steps: ${result.transfers?.length || 0}`);

  return result;
}

// Build operateFlowMatrix calldata from pathfinder response
function buildFlowMatrixCalldata(sender, receiver, transfers) {
  // Step 1: Build vertex list
  const vertexSet = new Set();
  vertexSet.add(sender.toLowerCase());
  vertexSet.add(receiver.toLowerCase());
  for (const t of transfers) {
    vertexSet.add(t.from.toLowerCase());
    vertexSet.add(t.to.toLowerCase());
    vertexSet.add(t.tokenOwner.toLowerCase());
  }

  // Sort by numeric value
  const flowVertices = Array.from(vertexSet).sort((a, b) => {
    const bigA = BigInt(a);
    const bigB = BigInt(b);
    return bigA < bigB ? -1 : bigA > bigB ? 1 : 0;
  });

  // Build index lookup
  const idx = {};
  flowVertices.forEach((v, i) => {
    idx[v] = i;
  });

  // Step 2: Build flow edges and coordinates
  const flowEdges = [];
  const coordinates = [];
  const receiverLower = receiver.toLowerCase();
  const senderLower = sender.toLowerCase();
  const terminalEdgeIndices = [];

  for (let i = 0; i < transfers.length; i++) {
    const t = transfers[i];
    const amount = BigInt(t.value);
    const toAddr = t.to.toLowerCase();

    // streamSinkId = 1 if this edge goes to receiver, 0 otherwise
    const streamSinkId = toAddr === receiverLower ? 1 : 0;

    flowEdges.push({ streamSinkId, amount });

    if (streamSinkId === 1) {
      terminalEdgeIndices.push(i);
    }

    // Pack coordinates: tokenOwner, from, to
    coordinates.push(idx[t.tokenOwner.toLowerCase()]);
    coordinates.push(idx[t.from.toLowerCase()]);
    coordinates.push(idx[toAddr]);
  }

  // Ensure at least one terminal edge
  if (terminalEdgeIndices.length === 0 && transfers.length > 0) {
    flowEdges[transfers.length - 1].streamSinkId = 1;
    terminalEdgeIndices.push(transfers.length - 1);
  }

  // Step 3: Build stream
  const senderCoordinate = idx[senderLower];
  const terminalEdgeIds = terminalEdgeIndices;

  // Step 4: Pack coordinates into bytes
  const packedCoordinates = new Uint8Array(coordinates.length * 2);
  coordinates.forEach((coord, i) => {
    packedCoordinates[i * 2] = (coord >> 8) & 0xff;
    packedCoordinates[i * 2 + 1] = coord & 0xff;
  });

  // Step 5: ABI encode
  const hub = new ethers.Contract(ADDRESSES.hubV2, HUB_ABI);
  const abiCoder = new ethers.AbiCoder();

  // Encode the complex call manually
  const flowEdgeTuples = flowEdges.map((e) => [e.streamSinkId, e.amount]);
  const streams = [
    [
      senderCoordinate,
      terminalEdgeIds,
      "0x", // empty data
    ],
  ];

  const calldata = hub.interface.encodeFunctionData("operateFlowMatrix", [
    flowVertices,
    flowEdgeTuples,
    streams,
    ethers.hexlify(packedCoordinates),
  ]);

  return calldata;
}

// Execute routed transfer
async function executeRoutedTransfer(signer, safeAddress, pathfinderUrl, source, sink, amount) {
  // Compute path
  const pathResult = await computePath(pathfinderUrl, source, sink, amount);

  if (!pathResult.transfers || pathResult.transfers.length === 0) {
    throw new Error("No path found");
  }

  // Build calldata
  const calldata = buildFlowMatrixCalldata(source, sink, pathResult.transfers);

  console.log("Executing routed transfer...");
  const receipt = await executeSafeTransaction(
    signer,
    safeAddress,
    ADDRESSES.hubV2,
    calldata
  );

  return {
    receipt,
    path: pathResult,
  };
}

// Main command handlers
async function cmdCheckSafe() {
  const config = getConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);

  console.log(`Checking Safe: ${config.safeAddress}`);

  const status = await checkSafeRegistration(provider, config.safeAddress);
  console.log("Registration status:");
  console.log(`  Is registered: ${status.isRegistered}`);
  console.log(`  Avatar address: ${status.avatar}`);
  console.log(`  Is human: ${status.isHuman}`);
  console.log(`  Is organization: ${status.isOrganization}`);
  console.log(`  Is group: ${status.isGroup}`);

  // Also check Safe properties
  const safe = new ethers.Contract(config.safeAddress, SAFE_ABI, provider);
  const owners = await safe.getOwners();
  const threshold = await safe.getThreshold();
  console.log("Safe properties:");
  console.log(`  Owners: ${owners.join(", ")}`);
  console.log(`  Threshold: ${threshold}`);

  return status;
}

async function cmdRegisterSafe() {
  const config = getConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);
  const signer = new ethers.Wallet(config.privateKey, provider);

  console.log(`Safe: ${config.safeAddress}`);
  console.log(`Signer: ${await signer.getAddress()}`);

  // Check current status
  const status = await checkSafeRegistration(provider, config.safeAddress);
  if (status.isRegistered) {
    console.log("Safe is already registered with Circles");
    return status;
  }

  const receipt = await registerSafeAsOrganization(signer, config.safeAddress);

  console.log("\n=== FIXTURE DATA ===");
  console.log(`Block number: ${receipt.blockNumber}`);
  console.log(`Safe address: ${config.safeAddress}`);
  console.log("====================\n");

  return receipt;
}

async function cmdTrustGroups(gatewayAddress) {
  const config = getConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);
  const signer = new ethers.Wallet(config.privateKey, provider);

  console.log(`Safe: ${config.safeAddress}`);
  console.log(`Gateway: ${gatewayAddress}`);

  await trustGroupsFromGateway(
    signer,
    config.safeAddress,
    gatewayAddress,
    ADDRESSES.groups
  );

  console.log("\n=== GROUPS TRUSTED ===");
  console.log(`Gateway: ${gatewayAddress}`);
  console.log(`Trusted groups: ${Object.keys(ADDRESSES.groups).join(", ")}`);
  console.log("======================\n");

  return gatewayAddress;
}

async function cmdDeployGateway() {
  const config = getConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);
  const signer = new ethers.Wallet(config.privateKey, provider);

  console.log(`Safe: ${config.safeAddress}`);
  console.log(`Signer: ${await signer.getAddress()}`);

  // Check Safe is registered
  const status = await checkSafeRegistration(provider, config.safeAddress);
  if (!status.isRegistered) {
    throw new Error(
      "Safe is not registered with Circles. Run 'register-safe' first."
    );
  }

  // Deploy gateway
  const gatewayAddress = await deployPaymentGateway(signer, config.safeAddress);

  // Trust groups
  await trustGroupsFromGateway(
    signer,
    config.safeAddress,
    gatewayAddress,
    ADDRESSES.groups
  );

  console.log("\n=== GATEWAY DEPLOYED ===");
  console.log(`Gateway address: ${gatewayAddress}`);
  console.log(`Trusted groups: ${Object.keys(ADDRESSES.groups).join(", ")}`);
  console.log("========================\n");

  return gatewayAddress;
}

async function cmdTestTransfer(gatewayAddress, sourceAddress, amount) {
  const config = getConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);
  const signer = new ethers.Wallet(config.privateKey, provider);

  if (!gatewayAddress) {
    throw new Error("Gateway address required. Run 'deploy-gateway' first or provide as argument.");
  }

  if (!sourceAddress) {
    throw new Error("Source address required (a Circles avatar with CRC balance).");
  }

  const transferAmount = amount ? ethers.parseEther(amount) : ethers.parseEther("1");

  console.log(`Testing routed transfer:`);
  console.log(`  Source: ${sourceAddress}`);
  console.log(`  Sink (Gateway): ${gatewayAddress}`);
  console.log(`  Amount: ${ethers.formatEther(transferAmount)} CRC`);

  const result = await executeRoutedTransfer(
    signer,
    config.safeAddress,
    config.pathfinderUrl,
    sourceAddress,
    gatewayAddress,
    transferAmount
  );

  console.log("\n=== TEST RESULT ===");
  console.log(`Status: SUCCESS`);
  console.log(`Block number: ${result.receipt.blockNumber}`);
  console.log(`Transaction hash: ${result.receipt.hash}`);
  console.log(`Transfer steps: ${result.path.transfers.length}`);
  console.log("===================\n");

  // Output fixture data
  console.log("\n=== FIXTURE DATA (for regression test) ===");
  const fixture = {
    id: `payment-gateway-group-mint-${Date.now()}`,
    name: "Payment Gateway Group Mint Test",
    category: "payment-gateway",
    block: result.receipt.blockNumber,
    source: sourceAddress,
    sink: gatewayAddress,
    description: "Routed transfer to payment gateway with group trust. Tests router insertion and edge ordering fixes.",
    shouldFindPath: true,
    minFlow: transferAmount.toString(),
    expectedRevertReason: null,
    runOnAnvil: true,
    discoveredAt: new Date().toISOString(),
    fixedIn: "Pipeline reorder + SortEdgesForMintDependencies",
    tags: ["payment-gateway", "group-minting", "router", "regression"],
  };
  console.log(JSON.stringify(fixture, null, 2));
  console.log("==========================================\n");

  return result;
}

async function cmdFull() {
  console.log("=== PAYMENT GATEWAY ON-CHAIN TEST ===\n");

  // Step 1: Check Safe
  console.log("Step 1: Checking Safe registration...\n");
  const status = await cmdCheckSafe();

  // Step 2: Register if needed
  if (!status.isRegistered) {
    console.log("\nStep 2: Registering Safe with Circles...\n");
    await cmdRegisterSafe();
  } else {
    console.log("\nStep 2: Safe already registered, skipping.\n");
  }

  // Step 3: Deploy gateway
  console.log("Step 3: Deploying payment gateway...\n");
  const gatewayAddress = await cmdDeployGateway();

  // Note: Step 4 (test transfer) requires a source with CRC balance
  console.log("Step 4: Transfer test requires a source address with CRC balance.");
  console.log("Run manually with:");
  console.log(`  node scripts/payment-gateway-test.mjs test-transfer ${gatewayAddress} <source-address> [amount]`);

  return { status, gatewayAddress };
}

// Check if an avatar has consented flow enabled (trusts the Router)
async function cmdCheckConsented(avatarAddress) {
  const config = getReadOnlyConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);

  const checkAddress = avatarAddress || config.safeAddress;
  if (!checkAddress) {
    throw new Error("Avatar address required. Usage: check-consented <avatar> or set SAFE_ADDRESS");
  }
  console.log(`Checking consented flow status for: ${checkAddress}`);
  console.log(`Router address: ${ADDRESSES.router}`);

  const hub = new ethers.Contract(ADDRESSES.hubV2, HUB_ABI, provider);

  // Check if avatar trusts the router
  const isTrusted = await hub.isTrusted(checkAddress, ADDRESSES.router);

  // Get trust expiry (returns 0 if not trusted)
  let trustExpiry;
  try {
    trustExpiry = await hub.trustMarkers(checkAddress, ADDRESSES.router);
  } catch {
    trustExpiry = 0n;
  }

  let expiryDate = "N/A";
  if (trustExpiry > 0n) {
    try {
      // Handle very large expiry timestamps
      const expiryMs = Number(trustExpiry) * 1000;
      if (expiryMs > 8.64e15) {
        expiryDate = "Far future (year 2100+)";
      } else {
        expiryDate = new Date(expiryMs).toISOString();
      }
    } catch {
      expiryDate = "Far future";
    }
  }

  console.log("\n=== CONSENTED FLOW STATUS ===");
  console.log(`  Avatar: ${checkAddress}`);
  console.log(`  Trusts Router: ${isTrusted ? "YES ✅" : "NO ❌"}`);
  console.log(`  Trust Expiry: ${expiryDate}`);
  console.log(`  Consented Flow: ${isTrusted ? "ENABLED" : "DISABLED"}`);
  console.log("=============================\n");

  return { isTrusted, trustExpiry, expiryDate };
}

// Enable consented flow by trusting the Router from the Safe
async function cmdEnableConsented() {
  const config = getConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);
  const signer = new ethers.Wallet(config.privateKey, provider);

  console.log(`Safe: ${config.safeAddress}`);
  console.log(`Router: ${ADDRESSES.router}`);

  // Check current status
  const hub = new ethers.Contract(ADDRESSES.hubV2, HUB_ABI, provider);
  const currentlyTrusted = await hub.isTrusted(config.safeAddress, ADDRESSES.router);

  if (currentlyTrusted) {
    console.log("\n⚠️  Safe already trusts the Router (consented flow already enabled)");
    await cmdCheckConsented();
    return;
  }

  // Trust expiry: far future (year 2100 = 4102444800)
  const expiryTimestamp = 4102444800n;

  const data = hub.interface.encodeFunctionData("trust", [
    ADDRESSES.router,
    expiryTimestamp,
  ]);

  console.log("\nEnabling consented flow (Safe → trust → Router)...");
  const receipt = await executeSafeTransaction(
    signer,
    config.safeAddress,
    ADDRESSES.hubV2,
    data
  );

  console.log("\n=== CONSENTED FLOW ENABLED ===");
  console.log(`  Block number: ${receipt.blockNumber}`);
  console.log(`  Transaction hash: ${receipt.hash}`);
  console.log(`  Trust expiry: ${new Date(Number(expiryTimestamp) * 1000).toISOString()}`);
  console.log("==============================\n");

  // Verify
  await cmdCheckConsented();

  return receipt;
}

// Analyze a transaction to show transfer hops, tokens, and router involvement
async function cmdAnalyzeTx(txHash) {
  if (!txHash) {
    throw new Error("Transaction hash required. Usage: analyze-tx <hash>");
  }

  const config = getReadOnlyConfig();
  const provider = new ethers.JsonRpcProvider(config.rpcUrl);

  console.log(`Analyzing transaction: ${txHash}`);

  const receipt = await provider.getTransactionReceipt(txHash);
  if (!receipt) {
    throw new Error(`Transaction not found: ${txHash}`);
  }

  console.log(`Block: ${receipt.blockNumber}`);
  console.log(`Status: ${receipt.status === 1 ? "SUCCESS ✅" : "FAILED ❌"}`);
  console.log(`Gas used: ${receipt.gasUsed}`);

  // Parse ERC1155 events from Hub V2
  const hub = new ethers.Contract(ADDRESSES.hubV2, HUB_ABI, provider);

  // Event signatures
  const TRANSFER_SINGLE_TOPIC = ethers.id("TransferSingle(address,address,address,uint256,uint256)");
  const TRANSFER_BATCH_TOPIC = ethers.id("TransferBatch(address,address,address,uint256[],uint256[])");

  const transfers = [];
  const burns = [];
  const mints = [];

  for (const log of receipt.logs) {
    // Only process Hub V2 logs
    if (log.address.toLowerCase() !== ADDRESSES.hubV2.toLowerCase()) continue;

    try {
      if (log.topics[0] === TRANSFER_SINGLE_TOPIC) {
        const from = "0x" + log.topics[2].slice(26);
        const to = "0x" + log.topics[3].slice(26);
        const decoded = ethers.AbiCoder.defaultAbiCoder().decode(
          ["uint256", "uint256"],
          log.data
        );
        const tokenId = decoded[0];
        const value = decoded[1];

        // Token ID in Circles V2 is the avatar address as uint256
        const tokenOwner = "0x" + tokenId.toString(16).padStart(40, "0");

        const transfer = {
          type: "single",
          from,
          to,
          tokenOwner,
          value,
          formatted: ethers.formatEther(value),
        };

        if (from === ethers.ZeroAddress) {
          mints.push(transfer);
        } else if (to === ethers.ZeroAddress) {
          burns.push(transfer);
        } else {
          transfers.push(transfer);
        }
      } else if (log.topics[0] === TRANSFER_BATCH_TOPIC) {
        const from = "0x" + log.topics[2].slice(26);
        const to = "0x" + log.topics[3].slice(26);
        const decoded = ethers.AbiCoder.defaultAbiCoder().decode(
          ["uint256[]", "uint256[]"],
          log.data
        );
        const tokenIds = decoded[0];
        const values = decoded[1];

        for (let i = 0; i < tokenIds.length; i++) {
          const tokenOwner = "0x" + tokenIds[i].toString(16).padStart(40, "0");
          const transfer = {
            type: "batch",
            from,
            to,
            tokenOwner,
            value: values[i],
            formatted: ethers.formatEther(values[i]),
          };

          if (from === ethers.ZeroAddress) {
            mints.push(transfer);
          } else if (to === ethers.ZeroAddress) {
            burns.push(transfer);
          } else {
            transfers.push(transfer);
          }
        }
      }
    } catch (e) {
      // Skip unparseable logs
    }
  }

  // Check if any groups are involved (need to query chain)
  const groupsInvolved = new Set();
  const allTokenOwners = [
    ...transfers.map(t => t.tokenOwner),
    ...burns.map(t => t.tokenOwner),
    ...mints.map(t => t.tokenOwner),
  ];

  for (const tokenOwner of allTokenOwners) {
    try {
      const isGroup = await hub.isGroup(tokenOwner);
      if (isGroup) {
        groupsInvolved.add(tokenOwner.toLowerCase());
      }
    } catch {
      // Ignore
    }
  }

  // Router involvement: if group tokens appear in transfers, router was involved
  // (Group tokens can only be created via router minting)
  const routerInvolved = groupsInvolved.size > 0;

  console.log("\n=== TRANSACTION ANALYSIS ===");

  console.log(`\n📊 Summary:`);
  console.log(`  Regular transfers: ${transfers.length}`);
  console.log(`  Burns (to 0x0): ${burns.length}`);
  console.log(`  Mints (from 0x0): ${mints.length}`);
  console.log(`  Router involved: ${routerInvolved ? "YES (mint-along-path) 🔄" : "NO"}`);
  console.log(`  Groups involved: ${groupsInvolved.size > 0 ? Array.from(groupsInvolved).join(", ") : "None"}`);

  if (transfers.length > 0) {
    console.log(`\n📤 Transfers:`);
    for (const t of transfers) {
      console.log(`  ${t.from.slice(0, 10)}... → ${t.to.slice(0, 10)}... | ${t.formatted} CRC | token: ${t.tokenOwner.slice(0, 10)}...`);
    }
  }

  if (burns.length > 0) {
    console.log(`\n🔥 Burns (collateral for minting):`);
    for (const t of burns) {
      console.log(`  ${t.from.slice(0, 10)}... → 0x0 | ${t.formatted} CRC | token: ${t.tokenOwner.slice(0, 10)}...`);
    }
  }

  if (mints.length > 0) {
    console.log(`\n✨ Mints (group tokens created):`);
    for (const t of mints) {
      const isGroupToken = groupsInvolved.has(t.tokenOwner.toLowerCase());
      const label = isGroupToken ? " [GROUP TOKEN]" : "";
      console.log(`  0x0 → ${t.to.slice(0, 10)}... | ${t.formatted} CRC | token: ${t.tokenOwner.slice(0, 10)}...${label}`);
    }
  }

  // Calculate totals
  const totalTransferred = transfers.reduce((sum, t) => sum + t.value, 0n);
  const totalBurned = burns.reduce((sum, t) => sum + t.value, 0n);
  const totalMinted = mints.reduce((sum, t) => sum + t.value, 0n);

  console.log(`\n💰 Totals:`);
  console.log(`  Total transferred: ${ethers.formatEther(totalTransferred)} CRC`);
  console.log(`  Total burned: ${ethers.formatEther(totalBurned)} CRC`);
  console.log(`  Total minted: ${ethers.formatEther(totalMinted)} CRC`);
  console.log("============================\n");

  return {
    block: receipt.blockNumber,
    status: receipt.status,
    transfers,
    burns,
    mints,
    routerInvolved,
    groupsInvolved: Array.from(groupsInvolved),
  };
}

// CLI
async function main() {
  const command = process.argv[2] || "help";

  try {
    switch (command) {
      case "check-safe":
        await cmdCheckSafe();
        break;

      case "register-safe":
        await cmdRegisterSafe();
        break;

      case "deploy-gateway":
        await cmdDeployGateway();
        break;

      case "trust-groups":
        await cmdTrustGroups(process.argv[3]);
        break;

      case "test-transfer":
        await cmdTestTransfer(process.argv[3], process.argv[4], process.argv[5]);
        break;

      case "full":
        await cmdFull();
        break;

      case "check-consented":
        await cmdCheckConsented(process.argv[3]);
        break;

      case "enable-consented":
        await cmdEnableConsented();
        break;

      case "analyze-tx":
        await cmdAnalyzeTx(process.argv[3]);
        break;

      case "help":
      default:
        console.log(`
Payment Gateway On-Chain Test Script

Usage: node scripts/payment-gateway-test.mjs <command> [args]

Commands:
  check-safe                    Check Safe registration with Circles
  register-safe                 Register Safe as Circles organization
  deploy-gateway                Deploy payment gateway and trust groups
  trust-groups <gateway>        Trust groups from an existing gateway
  test-transfer <gateway> <source> [amount]
                                Execute routed transfer to gateway
  full                          Run complete test sequence

  check-consented [avatar]      Check if avatar has consented flow enabled
  enable-consented              Enable consented flow (trust router from Safe)
  analyze-tx <txhash>           Analyze a transaction (hops, tokens, router)

  help                          Show this help message

Environment Variables:
  TX_PRIVATE_KEY     Private key for Safe owner
  SAFE_ADDRESS       Address of the Safe
  GNOSIS_RPC_URL     RPC URL (default: https://rpc.gnosischain.com)
  PATHFINDER_URL     Pathfinder API URL (default: http://localhost:8080)

Examples:
  # Check consented flow status
  node scripts/payment-gateway-test.mjs check-consented

  # Enable consented flow on Safe
  node scripts/payment-gateway-test.mjs enable-consented

  # Analyze a transaction
  node scripts/payment-gateway-test.mjs analyze-tx 0x2920e223b72d6121ae0cb310b733019c58f09343d1d57f34e24f6eb472964c9a

  # Execute routed transfer (0.1 CRC)
  node scripts/payment-gateway-test.mjs test-transfer 0x1f6db4d3cd8a506307952897a5b6d3bdedffbd1e 0x4b6F72008e7ACa33De36B6565eF30264626B21dB 0.1
`);
        if (command !== "help") {
          console.log(`Unknown command: ${command}`);
          process.exit(1);
        }
        break;
    }
  } catch (error) {
    console.error("\nError:", error.message);
    if (error.data) {
      console.error("Error data:", error.data);
    }
    process.exit(1);
  }
}

main();
