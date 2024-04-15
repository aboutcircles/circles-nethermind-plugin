namespace Circles.Index.Data.Model;

public record CirclesTransferDto(
    string Timestamp,
    string BlockNumber,
    string TransactionHash,
    string FromAddress,
    string ToAddress,
    string? Amount,
    string TokenAddress,
    string Cursor
);
