# Regression Analysis - December 10, 2025

## Summary

**Total Tests:** 107
**Passing:** 75 (41 exact + 34 within tolerance)
**Failing:** 31
**Expected Failures:** 1 (circles_getNetworkSnapshot)

---

## Issue Categories

### Category 1: New Methods Not in Production (18 failures) - EXPECTED

These methods exist in staging but are **not yet deployed** to production. They return "method not supported" from production.

| Method | Test Cases | Resolution |
|--------|------------|------------|
| `circles_getAggregatedTrustRelationsEnriched` | 3 (addr1, addr2, addr3) | Add to expectedFailures |
| `circles_getProfileView` | 3 (addr1, addr2, addr3) | Add to expectedFailures |
| `circles_getTransactionHistoryEnriched` | 3 (addr1, recent / with limit / block range) | Add to expectedFailures |
| `circles_getTrustNetworkSummary` | 3 (addr1, addr2, addr3 with maxDepth) | Add to expectedFailures |
| `circles_getValidInviters` | 3 (addr1 default min / addr2 min 96 / addr3 min 50) | Add to expectedFailures |
| `circles_searchProfileByAddressOrName` | 3 (by address prefix / by address / by text) | Add to expectedFailures |

**Action:** Add all 6 methods to `expectedFailures` in config.

---

### Category 2: circles_events Filter Queries (5 failures) - PRODUCTION BUG

Production returns "Internal error" for these filter queries while staging works correctly.

| Test Case | Local | Remote |
|-----------|-------|--------|
| `filter: blockNumber < 100 OR blockNumber > 40000000` | Works | Internal error |
| `filter: blockNumber > 38000000 AND blockNumber < 38001000` | Works | Internal error |
| `filter: combined with test addr3 and block range` | Works | Internal error |
| `filter: nested AND/OR with test addr3` | Works | Internal error |
| `filter: transactionHash IS NOT NULL` | Works | Internal error |

**Root Cause:** Production has a bug with complex filter expressions in `circles_events`. Staging has the fix.

**Action:** Add these specific test cases to `expectedFailures` until production is updated.

---

### Category 3: circles_getTrustRelations (2 failures) - DATA BUG

| Test Case | Issue |
|-----------|-------|
| `circles_getTrustRelations (addr2)` | All trust limits show `0` in local vs actual values in remote |
| `circles_getTrustRelations (addr3)` | Same issue - all limits are `0` |

**Root Cause Analysis:**

Looking at the diff:
- **Local:** `{"user":"0x0cc035ddf238cc4ba7b81d5096880c264f0a42a1","limit":0}`
- **Remote:** `{"user":"0x0cc035ddf238cc4ba7b81d5096880c264f0a42a1","limit":100}`

The staging indexer is returning `limit: 0` for all trust relations, while production returns the correct values (50, 100, etc.).

**Investigation Required:**
1. Check if trust limit data is being indexed correctly in staging
2. Check the `CrcV2_Trust` table for limit values
3. Verify the SQL query in `GetTrustRelations` method

---

### Category 4: circles_getTokenBalances (6 failures) - DATA BUG

All 6 token balance tests fail (v1 addr1/2/3, v2 addr1/2/3).

**Issues Identified:**

#### Issue 4a: Wrong `tokenOwner` for ERC20 Wrapped Tokens

```
Local:  "tokenOwner": "0x6d5e20f62c177765f73aee343a307d949c08b9dc"  (wrapper address)
Remote: "tokenOwner": "0x227642ebd3a801e7b44a5bb956c02c2d97ca71f0"  (actual owner)
```

For wrapped tokens (`CrcV2_ERC20WrapperDeployed_Demurraged` / `CrcV2_ERC20WrapperDeployed_Inflationary`), staging is returning the **wrapper contract address** as `tokenOwner` instead of the **underlying token owner**.

**Investigation Required:**
1. Check `GetTokenBalances` query for wrapped tokens
2. The `tokenOwner` should resolve to the underlying token's owner, not the wrapper address
3. Look at how `circlesType` is used in the cache query

#### Issue 4b: Balance Precision Differences (Expected)

The balance values differ slightly due to demurrage calculations at different timestamps:
- Local:  `245654007470882788823`
- Remote: `245605203882638931836`

This is within the 5% tolerance for balance methods, so it's NOT the cause of failures.

---

## Detailed Bug Analysis

### BUG-001: Trust Relations Return Zero Limits

**Severity:** HIGH
**Affected Methods:** `circles_getTrustRelations`
**Symptoms:** All V1 trust limits return `0` instead of actual values (50, 100, etc.)

**Root Cause Found:**

In [CacheWarmupService.cs:1145-1163](src/Cache/Circles.Cache.Service/Services/CacheWarmupService.cs#L1145-L1163), the V1 trust relations SQL query is missing the `limit` column:

```csharp
// CURRENT (BUGGY):
const string v1Sql = @"
    SELECT ""user"" as truster, ""canSendTo"" as trustee
    FROM ""V_CrcV1_TrustRelations""";

// ...
v1TrustData[key] = 0L;  // Hardcoded to 0!
```

The V1 trust view `V_CrcV1_TrustRelations` **does** include a `limit` column (verified on staging DB), but the query doesn't select it.

**Fix Required:**
```csharp
const string v1Sql = @"
    SELECT ""user"" as truster, ""canSendTo"" as trustee, ""limit""
    FROM ""V_CrcV1_TrustRelations""";

// In the loop:
var limit = v1Reader.GetFieldValue<BigInteger>(2);
long limitLong = limit > long.MaxValue ? long.MaxValue : (long)limit;
v1TrustData[key] = limitLong;  // Use actual limit value
```

---

### BUG-002: Wrong TokenOwner for Wrapped Tokens

**Severity:** HIGH
**Affected Methods:** `circles_getTokenBalances`
**Symptoms:** Demurraged ERC20 wrappers show wrapper address as tokenOwner; inflationary wrappers work correctly

**Root Cause Found:**

The `Erc20WrapperAddresses` cache was keyed by **avatar address**, but an avatar can have **multiple wrappers** (one demurraged, one inflationary). Only the last wrapper per avatar was stored, losing ~870 wrappers.

**Evidence:**
- Database: 7643 total wrappers, 6773 unique avatars
- Cache: Only 6773 entries (one per avatar)
- Inflationary wrappers (circlesType=1) worked because they were inserted last
- Demurraged wrappers (circlesType=0) were overwritten

**Fix Applied:**
Changed cache key from avatar → wrapper address in:
- [CacheContainer.cs:34](src/Cache/Circles.Cache.Service/CacheContainer.cs#L34) - Changed type
- [CacheWarmupService.cs:461-476](src/Cache/Circles.Cache.Service/Services/CacheWarmupService.cs#L461-L476) - Key by wrapper
- [CacheWarmupService.cs:1568-1571](src/Cache/Circles.Cache.Service/Services/CacheWarmupService.cs#L1568-L1571) - Incremental update
- [NotificationListenerService.cs:529-532](src/Cache/Circles.Cache.Service/Services/NotificationListenerService.cs#L529-L532) - Live updates

---

## Action Items

### Completed

1. **Config Updates:** Added 24 expected failures to `scripts/rpc-regression-config.json`:
   - 18 new methods not yet in production
   - 5 circles_events filter queries (production bug)
   - 1 circles_getNetworkSnapshot (always differs)

2. **BUG-001 FIXED:** Added `limit` column to V1 trust relations query in [CacheWarmupService.cs:1145-1167](src/Cache/Circles.Cache.Service/Services/CacheWarmupService.cs#L1145-L1167)

3. **BUG-002 FIXED:** Changed `Erc20WrapperAddresses` cache key from avatar to wrapper address to support avatars with multiple wrappers

### Next Steps

1. Rebuild docker images
2. Redeploy cache service to staging
3. Run regression test again
4. Expected outcome: Only 24 expected failures (new methods + events filters)

---

## Test Results Reference

**Regression Run:** `RegressionTestResults/20251210_142911/`
**Config Used:** `scripts/rpc-regression-config.json`

```
Local URL:  http://135.181.238.49:8081 (staging)
Remote URL: https://rpc.aboutcircles.com (production)
```
