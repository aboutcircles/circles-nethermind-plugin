#!/usr/bin/env npx ts-node
/**
 * Trigger small Circles transactions for subscription testing
 * 
 * This script sends tiny Circles transactions to generate events
 * that can be observed via WebSocket subscriptions.
 * 
 * Usage:
 *   CIRCLES_SEED_PHRASE="your seed phrase here" npx ts-node scripts/trigger-circles-tx.ts
 * 
 * Environment Variables:
 *   CIRCLES_SEED_PHRASE    - Required: 12/24 word circles.garden key phrase (set in .env.local)
 *   CIRCLES_RPC_URL        - Optional: RPC endpoint (default: https://rpc.gnosis.gateway.fm)
 *   CIRCLES_HUB_ADDRESS    - Optional: Circles Hub V2 address
 *   CIRCLES_AMOUNT         - Optional: Amount to transfer in wei (default: 1)
 *   CIRCLES_RECIPIENT      - Optional: Recipient address (default: self-transfer)
 */

import { ethers } from 'ethers';

// Configuration from environment
const SEED_PHRASE = process.env.CIRCLES_SEED_PHRASE;
const RPC_URL = process.env.CIRCLES_RPC_URL || 'https://rpc.gnosis.gateway.fm';
const HUB_V2_ADDRESS = process.env.CIRCLES_HUB_ADDRESS || '0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8';
const TRANSFER_AMOUNT = process.env.CIRCLES_AMOUNT || '1'; // 1 wei of Circles
const RECIPIENT = process.env.CIRCLES_RECIPIENT; // If not set, self-transfer

// Circles Hub V2 ABI (minimal for transfers)
const HUB_V2_ABI = [
  'function safeTransferFrom(address from, address to, uint256 id, uint256 value, bytes data)',
  'function balanceOf(address account, uint256 id) view returns (uint256)',
  'event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)'
];

function deriveGardenWallet(phrase: string, provider: ethers.JsonRpcProvider) {
  try {
    const mnemonic = ethers.Mnemonic.fromPhrase(phrase.trim());
    const entropyHex = mnemonic.entropy.startsWith('0x')
      ? mnemonic.entropy
      : `0x${mnemonic.entropy}`;
    const wallet = new ethers.Wallet(entropyHex, provider);
    return wallet;
  } catch (error) {
    throw new Error('Invalid circles.garden key phrase. Please copy it exactly as shown in 5ecret-garden.');
  }
}

async function main() {
  // Validate seed phrase
  if (!SEED_PHRASE) {
    console.error('❌ Error: CIRCLES_SEED_PHRASE environment variable is required');
    console.error('');
    console.error('Usage:');
    console.error('  CIRCLES_SEED_PHRASE="word1 word2 ... word12" npx ts-node scripts/trigger-circles-tx.ts');
    console.error('');
    console.error('⚠️  Security Warning: Never share your seed phrase!');
    process.exit(1);
  }

  // Validate mnemonic
  if (!ethers.Mnemonic.isValidMnemonic(SEED_PHRASE)) {
    console.error('❌ Error: Invalid seed phrase');
    process.exit(1);
  }

  console.log('🔄 Circles Transaction Trigger');
  console.log('================================');
  console.log(`RPC URL: ${RPC_URL}`);
  console.log(`Hub V2:  ${HUB_V2_ADDRESS}`);
  console.log('Derivation: circles.garden entropy → private key');
  console.log('');

  try {
    // Create provider and wallet
    const provider = new ethers.JsonRpcProvider(RPC_URL);
    const wallet = deriveGardenWallet(SEED_PHRASE, provider);
    const address = await wallet.getAddress();

    console.log(`Wallet:  ${address}`);

    // Get network info
    const network = await provider.getNetwork();
    console.log(`Network: ${network.name} (chainId: ${network.chainId})`);

    // Check native balance for gas
    const balance = await provider.getBalance(address);
    console.log(`xDAI Balance: ${ethers.formatEther(balance)} xDAI`);

    if (balance === 0n) {
      console.error('❌ Error: No xDAI for gas. Please fund the wallet first.');
      process.exit(1);
    }

    // Connect to Hub V2
    const hub = new ethers.Contract(HUB_V2_ADDRESS, HUB_V2_ABI, wallet);

    // Calculate token ID from address (address as uint256)
    const tokenId = BigInt(address);
    console.log(`Token ID: ${tokenId}`);

    // Check Circles balance
    const circlesBalance = await hub.balanceOf(address, tokenId);
    console.log(`Circles Balance: ${circlesBalance} (raw)`);

    if (circlesBalance === 0n) {
      console.error('❌ Error: No Circles tokens to transfer');
      console.error('   The wallet needs to have minted Circles first.');
      process.exit(1);
    }

    // Determine recipient
    const recipient = RECIPIENT || address; // Self-transfer if no recipient
    const amount = BigInt(TRANSFER_AMOUNT);

    if (amount > circlesBalance) {
      console.error(`❌ Error: Insufficient balance. Have ${circlesBalance}, want to send ${amount}`);
      process.exit(1);
    }

    console.log('');
    console.log('📤 Sending transaction...');
    console.log(`   From:   ${address}`);
    console.log(`   To:     ${recipient}`);
    console.log(`   Amount: ${amount} (raw wei)`);

    // Send the transfer
    const tx = await hub.safeTransferFrom(
      address,
      recipient,
      tokenId,
      amount,
      '0x' // empty data
    );

    console.log(`   Tx Hash: ${tx.hash}`);
    console.log('');
    console.log('⏳ Waiting for confirmation...');

    const receipt = await tx.wait();
    
    console.log('');
    console.log('✅ Transaction confirmed!');
    console.log(`   Block:    ${receipt.blockNumber}`);
    console.log(`   Gas Used: ${receipt.gasUsed}`);
    console.log(`   Status:   ${receipt.status === 1 ? 'Success' : 'Failed'}`);

    // Parse events
    const transferEvents = receipt.logs
      .filter((log: any) => log.address.toLowerCase() === HUB_V2_ADDRESS.toLowerCase())
      .map((log: any) => {
        try {
          return hub.interface.parseLog({ topics: log.topics as string[], data: log.data });
        } catch {
          return null;
        }
      })
      .filter(Boolean);

    if (transferEvents.length > 0) {
      console.log('');
      console.log('📋 Events emitted:');
      transferEvents.forEach((event: any, i: number) => {
        console.log(`   [${i + 1}] ${event.name}`);
        if (event.name === 'TransferSingle') {
          console.log(`       operator: ${event.args.operator}`);
          console.log(`       from:     ${event.args.from}`);
          console.log(`       to:       ${event.args.to}`);
          console.log(`       id:       ${event.args.id}`);
          console.log(`       value:    ${event.args.value}`);
        }
      });
    }

    console.log('');
    console.log('🎉 Done! This transaction should appear in subscription events.');

  } catch (error: any) {
    console.error('');
    console.error('❌ Transaction failed:', error.message);
    if (error.data) {
      console.error('   Data:', error.data);
    }
    process.exit(1);
  }
}

main();
