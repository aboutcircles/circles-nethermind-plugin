namespace Circles.Index.Common;

public class Range<T>
    where T : struct
{
    public T? Min { get; set; }
    public T? Max { get; set; }

    public bool HasValue => Min.HasValue && Max.HasValue;
}
