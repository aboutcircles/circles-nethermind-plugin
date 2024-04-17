namespace Circles.Index.Data.Model;

public class CirclesTransferQuery
{
    public QueryMode Mode { get; set; } = QueryMode.And;
    public Range<long> BlockNumberRange { get; set; } = new();
    public string? TransactionHash { get; set; }
    public string? TokenAddress { get; set; }
    public string? FromAddress { get; set; }
    public string? ToAddress { get; set; }
    public string? Cursor { get; set; }
    public int? Limit { get; set; }
    public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
}
