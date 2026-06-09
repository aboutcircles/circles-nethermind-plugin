#!/usr/bin/env node
// Build operateFlowMatrix calldata from pathfinder transfer steps.
// Input (stdin): {"from":"0x...","to":"0x...","transfers":[{from,to,tokenOwner,value}],"wrapperMap":{"0xwrapper":"0xavatar",...}}
// If wrapperMap is provided, wrapper tokenOwner addresses are resolved to underlying avatars.
// Output (stdout): 0x-prefixed calldata hex string
// Exit: 0=success, 1=error

import { ethers } from "ethers";

const HUB_ABI = [
  "function operateFlowMatrix(address[] flowVertices, tuple(uint16 streamSinkId, uint192 amount)[] flow, tuple(uint16 sourceCoordinate, uint16[] flowEdgeIds, bytes data)[] streams, bytes packedCoordinates) external",
];

function readStdin() {
  return new Promise((resolve, reject) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => (data += chunk));
    process.stdin.on("end", () => resolve(data));
    process.stdin.on("error", reject);
  });
}

function buildCalldata(sender, receiver, transfers) {
  // Step 1: Build sorted vertex list
  const vertexSet = new Set();
  vertexSet.add(sender.toLowerCase());
  vertexSet.add(receiver.toLowerCase());
  for (const t of transfers) {
    vertexSet.add(t.from.toLowerCase());
    vertexSet.add(t.to.toLowerCase());
    vertexSet.add(t.tokenOwner.toLowerCase());
  }

  const flowVertices = Array.from(vertexSet).sort((a, b) => {
    const bigA = BigInt(a);
    const bigB = BigInt(b);
    return bigA < bigB ? -1 : bigA > bigB ? 1 : 0;
  });

  const idx = {};
  flowVertices.forEach((v, i) => {
    idx[v] = i;
  });

  // Step 2: Build flow edges and detect terminal edges.
  //
  // Terminal edge detection: In Hub.sol's operateFlowMatrix, each stream
  // declares flowEdgeIds — the indices of edges that deliver flow to the
  // stream's sink (the receiver). The pathfinder produces a SINGLE stream
  // with all paths converging at the receiver, so every edge whose `to`
  // equals the receiver is a terminal edge (the last edge of its respective
  // path within the stream). This is correct for single-stream output.
  //
  // This would break if:
  //   - Multiple streams were used (each with its own sink), or
  //   - An intermediate edge happened to route through the receiver and
  //     continue onward (receiver appears mid-path). The pathfinder does
  //     not produce such paths — the receiver is always a pure sink.
  const flowEdges = [];
  const coordinates = [];
  const receiverLower = receiver.toLowerCase();
  const senderLower = sender.toLowerCase();
  const terminalEdgeIndices = [];

  // Check for self-loop at receiver (aggregate pattern in quantized mode)
  let hasSelfLoop = false;
  let selfLoopIndex = -1;
  for (let i = 0; i < transfers.length; i++) {
    const t = transfers[i];
    if (
      t.from.toLowerCase() === receiverLower &&
      t.to.toLowerCase() === receiverLower
    ) {
      hasSelfLoop = true;
      selfLoopIndex = i;
      break;
    }
  }

  for (let i = 0; i < transfers.length; i++) {
    const t = transfers[i];
    const amount = BigInt(t.value);
    const toAddr = t.to.toLowerCase();
    const fromAddr = t.from.toLowerCase();

    let isTerminal;
    if (hasSelfLoop) {
      isTerminal = i === selfLoopIndex;
    } else {
      // All edges reaching the receiver are terminal — correct because the
      // pathfinder outputs a single stream where the receiver is a pure sink
      // (never an intermediary). Each such edge is the final hop of one path.
      isTerminal = toAddr === receiverLower;
    }

    const streamSinkId = isTerminal ? 1 : 0;
    flowEdges.push({ streamSinkId, amount });

    if (isTerminal) {
      terminalEdgeIndices.push(i);
    }

    // Pack coordinates: tokenOwner, from, to
    coordinates.push(idx[t.tokenOwner.toLowerCase()]);
    coordinates.push(idx[fromAddr]);
    coordinates.push(idx[toAddr]);
  }

  if (terminalEdgeIndices.length === 0 && transfers.length > 0) {
    flowEdges[transfers.length - 1].streamSinkId = 1;
    terminalEdgeIndices.push(transfers.length - 1);
  }

  // Step 3: Build stream
  const streams = [
    [
      idx[senderLower], // sourceCoordinate
      terminalEdgeIndices, // flowEdgeIds
      "0x", // empty data
    ],
  ];

  // Step 4: Pack coordinates into bytes
  const packedCoordinates = new Uint8Array(coordinates.length * 2);
  coordinates.forEach((coord, i) => {
    packedCoordinates[i * 2] = (coord >> 8) & 0xff;
    packedCoordinates[i * 2 + 1] = coord & 0xff;
  });

  // Step 5: ABI encode
  const iface = new ethers.Interface(HUB_ABI);
  const flowEdgeTuples = flowEdges.map((e) => [e.streamSinkId, e.amount]);

  return iface.encodeFunctionData("operateFlowMatrix", [
    flowVertices,
    flowEdgeTuples,
    streams,
    ethers.hexlify(packedCoordinates),
  ]);
}

try {
  const raw = await readStdin();
  const input = JSON.parse(raw);

  if (!input.from || !input.to || !Array.isArray(input.transfers)) {
    throw new Error(
      'Input must have "from", "to", and "transfers" array fields'
    );
  }

  for (let i = 0; i < input.transfers.length; i++) {
    const t = input.transfers[i];
    if (!t.from || !t.to || !t.tokenOwner || !t.value) {
      throw new Error(
        `Transfer[${i}] missing required fields (from, to, tokenOwner, value)`
      );
    }
  }

  // Resolve wrapper tokenOwner addresses to underlying avatars
  const wrapperMap = input.wrapperMap || {};
  const receiverLower = input.to.toLowerCase();

  // Filter display-only self-loop edges (sink→sink in quantized mode)
  // and resolve wrapper addresses
  const resolved = input.transfers
    .filter(
      (t) =>
        !(
          t.from.toLowerCase() === receiverLower &&
          t.to.toLowerCase() === receiverLower
        )
    )
    .map((t) => {
      const ownerLower = t.tokenOwner.toLowerCase();
      return {
        ...t,
        tokenOwner: (wrapperMap[ownerLower] || t.tokenOwner).toLowerCase(),
      };
    });

  const calldata = buildCalldata(input.from, input.to, resolved);
  process.stdout.write(calldata);
} catch (err) {
  process.stderr.write(`Error: ${err.message}\n`);
  process.exit(1);
}
