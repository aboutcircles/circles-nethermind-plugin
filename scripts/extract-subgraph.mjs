#!/usr/bin/env node
/**
 * Subgraph Extraction Tool
 *
 * Extracts a minimal subgraph for a source→sink transfer from the test environment.
 * The extracted subgraph contains only the addresses and data needed to find a path,
 * enabling fast unit tests without database dependencies.
 *
 * Prerequisites:
 *   TEST_ENV_URL environment variable must be set
 *
 * Usage:
 *   node scripts/extract-subgraph.mjs \
 *     --source 0x4b6F72008e7ACa33De36B6565eF30264626B21dB \
 *     --sink 0x1f6db4d3cd8a506307952897a5b6d3bdedffbd1e \
 *     --block 44288768 \
 *     --max-hops 6
 *
 * Output:
 *   JSON subgraph to stdout (redirect to file)
 */

import { parseArgs } from "node:util";

// Parse command line arguments
const { values } = parseArgs({
  options: {
    source: { type: "string", short: "s" },
    sink: { type: "string", short: "k" },
    block: { type: "string", short: "b" },
    "max-hops": { type: "string", short: "m", default: "6" },
    fixture: { type: "string", short: "f" },
    help: { type: "boolean", short: "h" },
  },
});

if (values.help) {
  console.log(`
Subgraph Extraction Tool

Extracts a minimal subgraph for unit testing pathfinder scenarios.

Usage:
  node scripts/extract-subgraph.mjs [options]

Options:
  --source, -s <address>    Source address (required unless --fixture)
  --sink, -k <address>      Sink address (required unless --fixture)
  --block, -b <number>      Block number (required unless --fixture)
  --max-hops, -m <number>   Maximum hops for BFS (default: 6)
  --fixture, -f <file>      Extract subgraph for existing fixture JSON
  --help, -h                Show this help

Environment:
  TEST_ENV_URL              Test environment URL (required)

Examples:
  # Extract subgraph for a specific scenario
  node scripts/extract-subgraph.mjs \\
    --source 0x4b6F72008e7ACa33De36B6565eF30264626B21dB \\
    --sink 0x1f6db4d3cd8a506307952897a5b6d3bdedffbd1e \\
    --block 44288768 > subgraph.json

  # Extract and update an existing fixture
  node scripts/extract-subgraph.mjs \\
    --fixture RegressionScenarios/payment-gateway-group-mint-001.json
`);
  process.exit(0);
}

const TEST_ENV_URL = process.env.TEST_ENV_URL;
if (!TEST_ENV_URL) {
  console.error("ERROR: TEST_ENV_URL environment variable not set");
  console.error("Set it to: https://staging.circlesubi.network/test-env");
  process.exit(1);
}

// If fixture provided, load source/sink/block from it
let source = values.source;
let sink = values.sink;
let block = values.block ? parseInt(values.block) : null;
let fixtureData = null;

if (values.fixture) {
  const fs = await import("fs");
  const path = await import("path");

  let fixturePath = values.fixture;
  if (!path.default.isAbsolute(fixturePath)) {
    fixturePath = path.default.resolve(process.cwd(), fixturePath);
  }

  try {
    fixtureData = JSON.parse(fs.default.readFileSync(fixturePath, "utf-8"));
    source = source || fixtureData.source;
    sink = sink || fixtureData.sink;
    block = block || fixtureData.block;
    console.error(`Loaded fixture: ${fixtureData.name || fixtureData.id}`);
  } catch (e) {
    console.error(`ERROR: Failed to read fixture: ${e.message}`);
    process.exit(1);
  }
}

if (!source || !sink || !block) {
  console.error("ERROR: --source, --sink, and --block are required");
  console.error("Run with --help for usage information");
  process.exit(1);
}

const maxHops = parseInt(values["max-hops"]) || 6;

console.error(`Extracting subgraph:`);
console.error(`  Source: ${source}`);
console.error(`  Sink: ${sink}`);
console.error(`  Block: ${block}`);
console.error(`  Max hops: ${maxHops}`);

// Test environment client functions
async function checkHealth() {
  const response = await fetch(`${TEST_ENV_URL}/health`);
  if (!response.ok) {
    throw new Error(`Health check failed: ${response.status}`);
  }
  return response.json();
}

async function createSession(blockNumber, features = ["db"], ttl = "30m") {
  const response = await fetch(`${TEST_ENV_URL}/api/v1/session`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ block: blockNumber, features, ttl }),
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`Failed to create session: ${response.status} ${text}`);
  }

  return response.json();
}

async function executeQuery(sessionId, sql, maxRows = 1000000) {
  const response = await fetch(
    `${TEST_ENV_URL}/api/v1/session/${sessionId}/query`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sql, maxRows }),
    }
  );

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`Query failed: ${response.status} ${text}`);
  }

  return response.json();
}

async function deleteSession(sessionId) {
  try {
    await fetch(`${TEST_ENV_URL}/api/v1/session/${sessionId}`, {
      method: "DELETE",
    });
  } catch {
    // Ignore cleanup errors
  }
}

// BFS to find reachable addresses
function bfs(trustEdges, startAddresses, maxDepth, direction = "outgoing") {
  const visited = new Set(startAddresses.map((a) => a.toLowerCase()));
  const queue = startAddresses.map((a) => ({ addr: a.toLowerCase(), depth: 0 }));
  const result = new Set(visited);

  while (queue.length > 0) {
    const { addr, depth } = queue.shift();

    if (depth >= maxDepth) continue;

    for (const [truster, trustee] of trustEdges) {
      const trusterLower = truster.toLowerCase();
      const trusteeLower = trustee.toLowerCase();

      let nextAddr = null;
      if (direction === "outgoing" && trusterLower === addr && !visited.has(trusteeLower)) {
        nextAddr = trusteeLower;
      } else if (direction === "incoming" && trusteeLower === addr && !visited.has(trusterLower)) {
        nextAddr = trusterLower;
      }

      if (nextAddr) {
        visited.add(nextAddr);
        result.add(nextAddr);
        queue.push({ addr: nextAddr, depth: depth + 1 });
      }
    }
  }

  return result;
}

// Main extraction logic
async function extractSubgraph() {
  // Check health
  console.error("Checking test environment health...");
  const health = await checkHealth();
  if (health.status !== "healthy") {
    throw new Error(`Test environment not healthy: ${health.status}`);
  }

  // Create session
  console.error(`Creating session at block ${block}...`);
  const session = await createSession(block);
  const sessionId = session.sessionId;
  console.error(`  Session ID: ${sessionId}`);

  try {
    // Step 1: Load ALL trust relationships
    console.error("Loading trust relationships...");
    const trustResult = await executeQuery(
      sessionId,
      `SELECT DISTINCT truster, trustee FROM "V_CrcV2_TrustRelations"`,
      2000000
    );

    const allTrust = trustResult.rows || [];
    console.error(`  Loaded ${allTrust.length} trust edges`);

    // Step 2: BFS from source (outgoing trust)
    console.error(`BFS from source (${maxHops} hops)...`);
    const sourceLower = source.toLowerCase();
    const sinkLower = sink.toLowerCase();

    const reachableFromSource = bfs(allTrust, [sourceLower], maxHops, "outgoing");
    console.error(`  Addresses reachable from source: ${reachableFromSource.size}`);

    // Step 3: BFS from sink (incoming trust - who can reach sink)
    console.error(`BFS to sink (${maxHops} hops)...`);
    const canReachSink = bfs(allTrust, [sinkLower], maxHops, "incoming");
    console.error(`  Addresses that can reach sink: ${canReachSink.size}`);

    // Step 4: Intersection = addresses that could be on a path
    const relevantAddresses = new Set();
    relevantAddresses.add(sourceLower);
    relevantAddresses.add(sinkLower);

    for (const addr of reachableFromSource) {
      if (canReachSink.has(addr)) {
        relevantAddresses.add(addr);
      }
    }
    console.error(`  Addresses on potential paths: ${relevantAddresses.size}`);

    // Step 5: Filter trust edges to only relevant addresses
    const relevantTrust = allTrust.filter(([truster, trustee]) => {
      const trusterLower = truster.toLowerCase();
      const trusteeLower = trustee.toLowerCase();
      return relevantAddresses.has(trusterLower) || relevantAddresses.has(trusteeLower);
    });
    console.error(`  Relevant trust edges: ${relevantTrust.length}`);

    // Step 6: Load balances for relevant addresses
    console.error("Loading balances...");
    const addressList = Array.from(relevantAddresses)
      .map((a) => `'${a}'`)
      .join(",");

    const balanceResult = await executeQuery(
      sessionId,
      `SELECT "demurragedTotalBalance", account, "tokenAddress", "isWrapped", "type"
       FROM "V_CrcV2_BalancesByAccountAndToken"
       WHERE LOWER(account) IN (${addressList})`,
      1000000
    );

    const balances = (balanceResult.rows || []).map(
      ([amount, holder, token, isWrapped, type]) => ({
        holder,
        token,
        amount: amount || "0",
        isWrapped: isWrapped === true || isWrapped === "true",
        isStatic: type === "static",
      })
    );
    console.error(`  Loaded ${balances.length} balance entries`);

    // Step 7: Load groups
    console.error("Loading groups...");
    const groupResult = await executeQuery(
      sessionId,
      `SELECT DISTINCT "group" FROM "CrcV2_RegisterGroup"`,
      100000
    );
    const allGroups = (groupResult.rows || []).map((r) => r[0].toLowerCase());

    // Filter to groups that are relevant (appear in trust edges)
    const relevantGroups = allGroups.filter((g) => relevantAddresses.has(g));
    console.error(`  Relevant groups: ${relevantGroups.length}`);

    // Step 8: Load group trusts for relevant groups
    console.error("Loading group trusts...");
    let groupTrusts = [];
    if (relevantGroups.length > 0) {
      const groupList = relevantGroups.map((g) => `'${g}'`).join(",");
      const groupTrustResult = await executeQuery(
        sessionId,
        `SELECT DISTINCT "group", trustee
         FROM "CrcV2_Trust"
         WHERE LOWER("group") IN (${groupList})`,
        100000
      );
      groupTrusts = (groupTrustResult.rows || []).map(([group, trustee]) => [
        group,
        trustee,
      ]);
    }
    console.error(`  Group trusts: ${groupTrusts.length}`);

    // Step 9: Load consented flow flags
    console.error("Loading consented flow flags...");
    const consentedResult = await executeQuery(
      sessionId,
      `SELECT avatar, "advancedUsageFlags"
       FROM (
         SELECT avatar, "advancedUsageFlags",
                ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY "blockNumber" DESC) as rn
         FROM "CrcV2_SetAdvancedUsageFlag"
         WHERE LOWER(avatar) IN (${addressList})
       ) sub
       WHERE rn = 1`,
      100000
    );

    const consentedAvatars = [];
    for (const [avatar, flags] of consentedResult.rows || []) {
      // Decode consented flow flag (byte 31 & 0x01)
      if (flags && flags.length >= 64) {
        // hex string without 0x prefix, or with
        const cleanFlags = flags.startsWith("0x") ? flags.slice(2) : flags;
        if (cleanFlags.length >= 64) {
          const lastByte = parseInt(cleanFlags.slice(62, 64), 16);
          if ((lastByte & 0x01) !== 0) {
            consentedAvatars.push(avatar.toLowerCase());
          }
        }
      }
    }
    console.error(`  Consented avatars: ${consentedAvatars.length}`);

    // Build subgraph object
    const subgraph = {
      trust: relevantTrust,
      balances,
      groups: relevantGroups,
      groupTrusts,
      consentedAvatars,
      stats: {
        addressCount: relevantAddresses.size,
        trustEdges: relevantTrust.length,
        balanceEntries: balances.length,
        groupCount: relevantGroups.length,
        groupTrustCount: groupTrusts.length,
        consentedCount: consentedAvatars.length,
      },
    };

    return subgraph;
  } finally {
    // Cleanup session
    console.error("Cleaning up session...");
    await deleteSession(sessionId);
  }
}

// Run extraction
try {
  const subgraph = await extractSubgraph();

  console.error("\n=== EXTRACTION COMPLETE ===");
  console.error(`  Addresses: ${subgraph.stats.addressCount}`);
  console.error(`  Trust edges: ${subgraph.stats.trustEdges}`);
  console.error(`  Balance entries: ${subgraph.stats.balanceEntries}`);
  console.error(`  Groups: ${subgraph.stats.groupCount}`);
  console.error(`  Consented avatars: ${subgraph.stats.consentedCount}`);

  // If fixture provided, merge subgraph into it
  if (fixtureData) {
    fixtureData.subgraph = subgraph;
    fixtureData.originalBlock = fixtureData.originalBlock || block;
    console.log(JSON.stringify(fixtureData, null, 2));
  } else {
    // Output just the subgraph
    console.log(JSON.stringify(subgraph, null, 2));
  }
} catch (error) {
  console.error(`\nERROR: ${error.message}`);
  process.exit(1);
}
