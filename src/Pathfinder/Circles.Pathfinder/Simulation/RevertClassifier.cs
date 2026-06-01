using System.Globalization;
using System.Numerics;

namespace Circles.Pathfinder.Simulation;

/// <summary>
/// Classifies eth_call revert reasons into actionable categories.
/// "bug" = pathfinder produced invalid output (graph stale, logic error).
/// "input" = user request cannot succeed (sink can't receive ERC1155, etc.).
/// "simulation" = canary eth_call limitation, not a real failure (e.g., operator approval).
/// "unknown" = needs manual investigation.
///
/// Circles V2 error encoding (circles-contracts-v2/src/errors/Errors.sol):
///   CirclesErrorAddressUintArgs(address, uint256, uint8) — selector 0x5e418dba
///     Type 0 (0x00-0x1F): CirclesHubOperatorNotApprovedForSource(source, streamIndex)
///     Type 1 (0x20-0x3F): CirclesHubFlowEdgeIsNotPermitted(receiver, circlesId)
///     Type 2 (0x40-0x5F): CirclesHubGroupMintPolicyRejectedBurn(burner, toTokenId)
///     Type 3 (0x60-0x7F): CirclesHubGroupMintPolicyRejectedMint(minter, toTokenId)
///     Type 4 (0x80-0x9F): CirclesDemurrageAmountExceedsMaxUint192(account, circlesId)
///     Type 5 (0xA0-0xBF): CirclesDemurrageDayBeforeLastUpdatedDay(account, lastDayUpdated)
///   CirclesErrorOneAddressArg(address, uint8) — selector 0xc14c0700
///     Type 0 (0x00-0x1F): CirclesHubMustBeHuman(avatar)
///     Type 1 (0x20-0x3F): CirclesAvatarMustBeRegistered(avatar)
/// </summary>
public static class RevertClassifier
{
    // Hub.sol Circles-specific error selectors
    // CirclesErrorAddressUintArgs(address,uint256,uint8)
    private const string CirclesErrorAddressUintArgs = "0x5e418dba";
    // CirclesErrorOneAddressArg(address,uint8)
    private const string CirclesErrorOneAddressArg = "0xc14c0700";
    // Hub.sol flow matrix netted flow error
    private const string NettedFlowMismatch = "0xb8c358de";
    // OpenZeppelin ERC1155 errors
    private const string ERC1155InsufficientBalance = "0x03dee4c5";
    private const string ERC1155InvalidReceiver = "0x57f447ce";

    // ABI layout after selector: each param is 32 bytes (64 hex chars).
    // CirclesErrorAddressUintArgs: address(32) + uint256(32) + uint8(32) = 96 bytes.
    // Code byte is the last byte of the 3rd word → offset 95 from param start.
    private const int AddressUintArgsCodeByteHexOffset = 8 + 64 + 64 + 62; // selector + p1 + p2 + 31 padding bytes

    // CirclesErrorOneAddressArg: address(32) + uint8(32) = 64 bytes.
    // Code byte is the last byte of the 2nd word → offset 63 from param start.
    private const int OneAddressArgCodeByteHexOffset = 8 + 64 + 62; // selector + p1 + 31 padding bytes

    public static (string Category, string Label) Classify(string? revertData)
    {
        if (string.IsNullOrEmpty(revertData))
            return ("unknown", "empty_revert");

        var lower = revertData.ToLowerInvariant();

        // CirclesErrorAddressUintArgs — 6 error types distinguished by code byte
        if (Contains(lower, CirclesErrorAddressUintArgs))
        {
            var codeByte = ExtractCodeByte(lower, CirclesErrorAddressUintArgs, AddressUintArgsCodeByteHexOffset);
            return ClassifyAddressUintArgs(codeByte);
        }

        // CirclesErrorOneAddressArg — type 0 = MustBeHuman, type 1 = AvatarMustBeRegistered
        if (Contains(lower, CirclesErrorOneAddressArg))
        {
            var codeByte = ExtractCodeByte(lower, CirclesErrorOneAddressArg, OneAddressArgCodeByteHexOffset);
            return ClassifyOneAddressArg(codeByte);
        }

        if (Contains(lower, NettedFlowMismatch))
            return ("bug", "netted_flow_mismatch");

        if (Contains(lower, ERC1155InsufficientBalance))
            return ("bug", "insufficient_balance");

        if (Contains(lower, ERC1155InvalidReceiver))
            return ("input", "invalid_receiver");

        // Generic revert string patterns
        if (lower.Contains("execution reverted"))
            return ("unknown", "generic_revert");

        return ("unknown", "unrecognized");
    }

    /// <summary>
    /// Classify CirclesErrorAddressUintArgs by code byte type (Hub.sol line 569, 729, 827).
    /// </summary>
    private static (string Category, string Label) ClassifyAddressUintArgs(int codeByte)
    {
        if (codeByte < 0)
            return ("unknown", "truncated_circles_error"); // Can't parse code byte

        return codeByte switch
        {
            // Type 0: OperatorNotApprovedForSource — msg.sender not approved for source avatar.
            // Canary artifact: eth_call uses source as msg.sender but Hub.sol requires an approved
            // operator via isApprovedForAll(source, msg.sender). Path itself may be valid.
            < 0x20 => ("simulation", "operator_not_approved"),
            // Type 1: FlowEdgeIsNotPermitted — isTrusted check failed. Pathfinder bug.
            < 0x40 => ("bug", "flow_edge_not_permitted"),
            // Type 2: GroupMintPolicyRejectedBurn — group mint policy rejected. Pathfinder bug.
            < 0x60 => ("bug", "group_mint_policy_rejected_burn"),
            // Type 3: GroupMintPolicyRejectedMint — group mint policy rejected. Pathfinder bug.
            < 0x80 => ("bug", "group_mint_policy_rejected_mint"),
            // Type 4: DemurrageAmountExceedsMaxUint192 — overflow. Pathfinder bug.
            < 0xA0 => ("bug", "demurrage_overflow"),
            // Type 5: DemurrageDayBeforeLastUpdatedDay — temporal issue. Pathfinder bug.
            < 0xC0 => ("bug", "demurrage_day_error"),
            // Unknown type
            _ => ("unknown", "unrecognized_code_byte")
        };
    }

    /// <summary>
    /// Classify CirclesErrorOneAddressArg by code byte type.
    /// </summary>
    private static (string Category, string Label) ClassifyOneAddressArg(int codeByte)
    {
        if (codeByte < 0)
            return ("unknown", "truncated_circles_error"); // Can't parse code byte

        return codeByte switch
        {
            // Type 0: MustBeHuman — avatar is not a human (Organization/Group used where Human required).
            // Canary artifact: same root cause as operator_not_approved.
            < 0x20 => ("simulation", "must_be_human"),
            // Type 1: AvatarMustBeRegistered — unregistered address in flow vertices. Pathfinder bug.
            < 0x40 => ("bug", "avatar_not_registered"),
            // Higher types exist but are less common in operateFlowMatrix context
            _ => ("unknown", "unrecognized_code_byte")
        };
    }

    /// <summary>
    /// Extract the uint8 code byte from a Circles error's ABI-encoded revert data.
    /// Returns -1 if the data is too short or non-hex.
    /// </summary>
    internal static int ExtractCodeByte(string lowerData, string selector, int codeByteHexOffset)
    {
        // Find where the selector starts in the data (may be prefixed by "execution reverted: ")
        var selectorHex = selector[2..]; // strip "0x"
        int selectorPos = lowerData.IndexOf(selectorHex, StringComparison.Ordinal);
        if (selectorPos < 0)
            return -1;

        // Code byte position relative to selector start
        int absPos = selectorPos + codeByteHexOffset;
        if (absPos + 2 > lowerData.Length)
            return -1;

        var hex = lowerData.AsSpan(absPos, 2);
        int high = HexVal(hex[0]);
        int low = HexVal(hex[1]);
        if (high < 0 || low < 0)
            return -1;

        return (high << 4) | low;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1
    };

    private static bool Contains(string data, string selector)
        => data.Contains(selector[2..]); // strip 0x prefix for matching

    /// <summary>
    /// Decoded fields of an OpenZeppelin ERC1155InsufficientBalance revert (selector 0x03dee4c5):
    /// ERC1155InsufficientBalance(address sender, uint256 balance, uint256 needed, uint256 tokenId).
    /// For the Circles canary this is the cache-balance-drift signal: <see cref="Holder"/> tried to
    /// send <see cref="Needed"/> of token <see cref="Token"/> but held only <see cref="Balance"/>
    /// on-chain (at the eth_call's graphBlock). needed ≫ balance ⇒ the graph source fed an inflated
    /// balance. tokenId is uint160(token), so <see cref="Token"/> is its low 20 bytes.
    /// </summary>
    public readonly record struct InsufficientBalanceInfo(
        string Holder, BigInteger Balance, BigInteger Needed, string Token);

    /// <summary>
    /// Decode the four ABI words of an ERC1155InsufficientBalance revert. Returns null if the data
    /// does not contain the selector, is too short, or is non-hex.
    /// </summary>
    public static InsufficientBalanceInfo? TryDecodeInsufficientBalance(string? revertData)
    {
        if (string.IsNullOrEmpty(revertData))
            return null;

        var lower = revertData.ToLowerInvariant();
        var selectorHex = ERC1155InsufficientBalance[2..]; // "03dee4c5"
        int selectorPos = lower.IndexOf(selectorHex, StringComparison.Ordinal);
        if (selectorPos < 0)
            return null;

        // After the selector: sender(32) + balance(32) + needed(32) + tokenId(32) = 256 hex chars.
        int start = selectorPos + selectorHex.Length;
        if (start + 256 > lower.Length)
            return null;

        var holder = AddressFromWord(lower, start);
        var balance = UintFromWord(lower, start + 64);
        var needed = UintFromWord(lower, start + 128);
        var token = AddressFromWord(lower, start + 192);

        if (holder is null || token is null || balance is null || needed is null)
            return null;

        return new InsufficientBalanceInfo(holder, balance.Value, needed.Value, token);
    }

    // An ABI address word: 32 bytes (64 hex) with the 20-byte address right-aligned in the low bytes.
    private static string? AddressFromWord(string lowerData, int wordHexStart)
    {
        var word = lowerData.AsSpan(wordHexStart, 64);
        foreach (var c in word)
            if (HexVal(c) < 0)
                return null;
        return "0x" + word[24..].ToString(); // last 40 hex = 20-byte address
    }

    // An ABI uint256 word as a non-negative BigInteger. The "0" prefix guards the sign bit
    // (BigInteger.Parse treats a leading hex digit ≥ 8 as negative otherwise).
    private static BigInteger? UintFromWord(string lowerData, int wordHexStart)
    {
        var word = lowerData.AsSpan(wordHexStart, 64);
        foreach (var c in word)
            if (HexVal(c) < 0)
                return null;
        return BigInteger.Parse("0" + word.ToString(), NumberStyles.HexNumber);
    }

    /// <summary>
    /// Low-cardinality bucket for the needed/on-chain-balance ratio (used as a metric label
    /// on <c>circles_canary_balance_drift_total</c>). Pure logic, kept next to the decoder so
    /// both canary paths and the test suite share one definition.
    /// </summary>
    public static string DriftBucket(BigInteger needed, BigInteger onChain)
    {
        if (onChain <= 0)
            return needed > 0 ? "zero_balance" : "none";
        if (needed <= onChain)
            return "le_1x";
        var floorRatio = needed / onChain; // integer floor; needed > onChain ⇒ ≥ 1
        if (floorRatio >= 100) return "ge_100x";
        if (floorRatio >= 10) return "ge_10x";
        if (floorRatio >= 2) return "ge_2x";
        return "ge_1x";
    }
}
