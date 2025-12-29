# Regression Test Scenarios

This directory contains JSON fixtures for regression testing the pathfinder's mint-along-path feature.

## Purpose

Each JSON file captures a specific bug scenario with:
- The block number to test at (frozen blockchain state)
- Source and sink addresses
- Expected behavior
- Description of what was broken

## Usage

The `RegressionTests.cs` test class loads all JSON files from this directory and verifies:
1. A path can be computed
2. Edges are correctly ordered (collateral before mint)
3. Validation passes without exceptions

## Adding New Scenarios

When a mint-along-path bug is discovered:

1. Note the block number when it occurred
2. Record the source and sink addresses
3. Document the error message
4. Create a new JSON file following the schema below

## Schema

```json
{
  "name": "Descriptive name",
  "block": 43193632,
  "source": "0x...",
  "sink": "0x...",
  "description": "What happened and why it was a bug",
  "expectedError": "Error message if it should fail (null if should succeed)",
  "minFlow": "1000000000000000000"
}
```

## Existing Scenarios

### mint-path-bug-001.json
- **Block**: 43193632
- **Date**: November 17, 2025
- **Issue**: Dictionary iteration order caused edges to be returned in wrong order
- **Error**: `ERC1155InsufficientBalance` - group tried to mint before receiving all collateral
