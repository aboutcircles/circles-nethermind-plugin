namespace Circles.Pathfinder.Nodes;

public class TokenNode(int address, int tokenId) : Node(address)
{
    public int TokenId { get; } = tokenId;
}