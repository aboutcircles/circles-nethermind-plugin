namespace Circles.Index.Data.Model;

public record CirclesSignupDto(
    string Timestamp,
    string BlockNumber,
    string TransactionHash,
    string CirclesAddress,
    string? TokenAddress,
    string Cursor
);
