namespace Circles.Index.Data.Model;

public record CirclesTrustDto(
    string Timestamp,
    string BlockNumber,
    string TransactionHash,
    string UserAddress,
    string CanSendToAddress,
    int Limit,
    string Cursor
);
