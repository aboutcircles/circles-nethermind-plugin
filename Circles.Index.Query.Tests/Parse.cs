using System.Numerics;
using System.Text.RegularExpressions;

namespace MyTests;

[TestFixture]
public class DecodeSafeTransferFromTests
{
    [Test]
    public void ParseSafeTransferFromManuallyTest()
    {
        // The giant call data (hex-encoded) from your example:
        // (All whitespace and newlines removed, lower/uppercase doesn't matter)
        string callDataHex = @"
f242432a0000000000000000000000004afdce8b4a37a7b87623798f08f18ffbe7ea88a200000000000000000000000097fd8f7829a019946329f6d2e763a727410475180000000000000000000000004afdce8b4a37a7b87623798f08f18ffbe7ea88a2000000000000000000000000000000000000000000000001841056868ed352b000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000000000c12c1e50abb450d6205ea2c3fa861b3b834d13e8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c4f242432a0000000000000000000000004afdce8b4a37a7b87623798f08f18ffbe7ea88a20000000000000000000000002408d464f2c8025d3a13301b0209a63496182d990000000000000000000000004afdce8b4a37a7b87623798f08f18ffbe7ea88a2000000000000000000000000000000000000000000000001841056868ed352b000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b56a6b7f6012ee5bef1cdf95df25e5045c7727c739000000000000000000000000000927c000000000000000000000000000004e200000000000000000000000000000000000000000000000000000000067ee807800000000000000000000000000000000000000000000000000000000000012345843b889c09d5726b1198f93995a62eabc18f53300027c5c04bc5ddcd2f7d61d282c4c90c653f11e4dcd78cb79f601c17af3ec71b07e093e42b1a0bab925c3551c000000000000000000000000000000000000000000000000000000000000000000000000000000000001ad000000000000000000000000000000000000000000000000fd90fad33ee8b58f32c00aceead1358e4afc23f90000000000000000000000000000000000000000000000000000000000000041000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000e0fde6caee2d7e480b77c760fb7c22af8fff7a958231f55f433e50b85ac5ad87ebc5d60fa59cc348d01b7a73d629a01e41849ff82999bd85ca6bb3c3b8a467cf07000000000000000000000000000000000000000000000000000000000000002532e56ff54526026631f60275192284efbb70a1489e38d70aaa331d67827e5eb71d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000034226f726967696e223a2268747470733a2f2f6170702e6d657472692e78797a222c2263726f73734f726967696e223a66616c736500000000000000000000000000000000000000000000000000000000000000
".Replace("\n", "")
            .Replace("\r", "")
            .Replace(" ", "")
            .ToLower()
            .Trim();

        // The known 4-byte signature for EIP-1155 safeTransferFrom:
        // Big-endian hex on the wire is 0xf2 42 43 2a
        const string safeTransferSig = "f242432a";

        // 1) Find all occurrences of the signature in the hex data
        MatchCollection matches = Regex.Matches(callDataHex, safeTransferSig, RegexOptions.IgnoreCase);
        var sigLocations = new List<int>();
        foreach (Match m in matches)
        {
            // Each character is half a byte. So dividing the start index by 2 => byte offset.
            int byteOffset = m.Index / 2;
            sigLocations.Add(byteOffset);
        }

        Console.WriteLine($"Found {sigLocations.Count} occurrence(s) of 0x{safeTransferSig} in the call data.");

        // 2) Decode each occurrence from the ABI viewpoint:
        //    function safeTransferFrom(address from, address to, uint256 id, uint256 amount, bytes data)
        //
        //    4 bytes  - function signature
        //    32 bytes - arg0 (address from) in a padded 256-bit word
        //    32 bytes - arg1 (address to)
        //    32 bytes - arg2 (uint256 id)
        //    32 bytes - arg3 (uint256 amount)
        //    32 bytes - arg4 (offset or inline for 'data')
        //
        // If arg4 is non-zero, we parse dynamic data: first 32 bytes = length, then next 'length' bytes = the data.

        foreach (int offset in sigLocations)
        {
            Console.WriteLine($"\nDecoding safeTransferFrom at byte offset {offset}...");
                
            // Let's define where arguments begin: after the 4-byte signature
            // So arg0 starts at offset+4
            // Each argument is a 32-byte word => 64 hex characters
            int arg0Start = (offset + 4) * 2;     // address 'from'
            int arg1Start = (offset + 4 + 32) * 2;  // address 'to'
            int arg2Start = (offset + 4 + 64) * 2;  // uint256 'id'
            int arg3Start = (offset + 4 + 96) * 2;  // uint256 'amount'
            int arg4Start = (offset + 4 + 128) * 2; // bytes pointer/offset

            // Make sure we actually have enough data for at least these static arguments
            if (callDataHex.Length < arg4Start + 64)
            {
                Console.WriteLine("  Not enough remaining hex to parse 5 arguments here. Skipping...");
                continue;
            }

            // Extract each 32-byte (64-hex) word
            string arg0Word = callDataHex.Substring(arg0Start, 64);
            string arg1Word = callDataHex.Substring(arg1Start, 64);
            string arg2Word = callDataHex.Substring(arg2Start, 64);
            string arg3Word = callDataHex.Substring(arg3Start, 64);
            string arg4Word = callDataHex.Substring(arg4Start, 64);

            // Convert them according to standard ABI rules:
            string addressFrom = HexToAddress(arg0Word);
            string addressTo   = HexToAddress(arg1Word);
            BigInteger tokenId = HexToBigInteger(arg2Word);
            BigInteger amount  = HexToBigInteger(arg3Word);

            Console.WriteLine($"  from    = {addressFrom}");
            Console.WriteLine($"  to      = {addressTo}");
            Console.WriteLine($"  tokenId = {tokenId}");
            Console.WriteLine($"  amount  = {amount}");

            // 3) Parse the dynamic 'bytes data'
            //    The arg4Word is typically the offset (in bytes) to the data region
            //    measured from the start of the arguments (i.e. offset 0 is right after the signature).
            //    If it's zero, that often means empty data.
            BigInteger dataOffset = HexToBigInteger(arg4Word);
            if (dataOffset == 0)
            {
                // Probably means the 'data' argument is empty
                Console.WriteLine("  data    = (empty or 0 offset)");
            }
            else
            {
                // The data offset is from (offset + 4) bytes after start-of-callData
                // So total start location is (offset + 4 + dataOffset).
                // We'll do a naive parse here.
                long dataSectionStart = offset + 4 + (long)dataOffset;
                long dataSectionStartHex = dataSectionStart * 2;

                if (dataSectionStartHex + 64 > callDataHex.Length)
                {
                    Console.WriteLine("  data    = (offset points out of range, can't parse)");
                }
                else
                {
                    // first 32 bytes in that section is the length
                    string lengthWord = callDataHex.Substring((int)dataSectionStartHex, 64);
                    BigInteger dataLength = HexToBigInteger(lengthWord);

                    long dataStart = (long)dataSectionStartHex + 64; // skip length
                    long dataEnd   = dataStart + (long)dataLength * 2;
                    if (dataEnd > callDataHex.Length)
                    {
                        Console.WriteLine("  data    = (declared length extends beyond end of call data)");
                    }
                    else
                    {
                        string dataHex = callDataHex.Substring((int)dataStart, (int)(dataLength * 2));
                        Console.WriteLine($"  data    = 0x{dataHex} (length: {dataLength} bytes)");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Takes a 64-hex-character string (32 bytes), which in standard
    /// Solidity ABI is a right-padded address. We return the last 20 bytes as "0x..." 
    /// </summary>
    private static string HexToAddress(string word32Hex)
    {
        if (word32Hex.Length != 64)
            throw new ArgumentException($"Expected 64 hex characters (32 bytes), got {word32Hex.Length}");
            
        // The last 40 hex chars represent 20 bytes = the address
        string last20 = word32Hex.Substring(word32Hex.Length - 40);
        return "0x" + last20;
    }

    /// <summary>
    /// Interpret a 64-hex-character string as a big-endian 256-bit integer.
    /// </summary>
    private static BigInteger HexToBigInteger(string word32Hex)
    {
        if (word32Hex.Length != 64)
            throw new ArgumentException($"Expected 64 hex characters, got {word32Hex.Length}");

        // BigInteger in C# can parse big-endian hex if we prepend "0x" and specify AllowHexSpecifier
        return BigInteger.Parse("0" + word32Hex, System.Globalization.NumberStyles.AllowHexSpecifier);
    }
}