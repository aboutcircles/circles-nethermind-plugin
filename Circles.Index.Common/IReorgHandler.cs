namespace Circles.Index.Common;

public interface IReorgHandler
{
    Task ReorgAt(long block);
}