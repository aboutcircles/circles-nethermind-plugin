namespace Circles.Common;

public class DelegateEqualityComparer<T>(Func<T, T, bool> equals, Func<T, int> getHashCode)
    : IEqualityComparer<T> where T : IIndexEvent
{
    public bool Equals(T? x, T? y)
    {
        if (x is null && y is null) return true;
        if (x is null || y is null) return false;
        return equals(x, y);
    }

    public int GetHashCode(T obj)
    {
        return getHashCode(obj);
    }
}
