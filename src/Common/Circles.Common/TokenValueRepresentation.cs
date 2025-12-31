namespace Circles.Index.Common;

/// <summary>
/// The low bit (0x01) encodes the basic monetary model.
/// Bit 1 (0x02) is an orthogonal “wrapped” flag.
/// </summary>
[Flags]
public enum TokenValueRepresentation : long
{
    Demurraged = 0b00,
    Inflationary = 0b01,
    IsWrapped = 0b10,

    DemurragedWrapped = Demurraged | IsWrapped,
    InflationaryWrapped = Inflationary | IsWrapped
}