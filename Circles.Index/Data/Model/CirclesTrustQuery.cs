namespace Circles.Index.Data.Model;

public class CirclesTrustQuery
{
    public Range<long> BlockNumberRange { get; set; }= new();
    public string? TransactionHash { get; set; }
    public string? UserAddress { get; set; }
    public string? CanSendToAddress { get; set; }
    public string? Cursor { get; set; }
    public int? Limit { get; set; }
    public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
    public QueryMode Mode { get; set; } = QueryMode.And;
}
