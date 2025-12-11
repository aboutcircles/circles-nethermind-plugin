using Circles.Rpc.Host;

namespace Circles.Rpc.Host.Tests;

[TestFixture]
public class CursorUtilsTests
{
    [Test]
    public void EncodeDecodeCursor_RoundTripsValues()
    {
        var cursor = CursorUtils.EncodeCursor(1234, 12, 7);
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.EqualTo(1234));
            Assert.That(tx, Is.EqualTo(12));
            Assert.That(log, Is.EqualTo(7));
        });
    }

    [Test]
    public void DecodeCursor_InvalidInputReturnsNulls()
    {
        var (block, tx, log) = CursorUtils.DecodeCursor("not-base64");

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void EncodeDecodeCursorWithBatch_RoundTripsValues()
    {
        var cursor = CursorUtils.EncodeCursorWithBatch(9999, 8, 4, 1);
        var (block, tx, log, batch) = CursorUtils.DecodeCursorWithBatch(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.EqualTo(9999));
            Assert.That(tx, Is.EqualTo(8));
            Assert.That(log, Is.EqualTo(4));
            Assert.That(batch, Is.EqualTo(1));
        });
    }

    [Test]
    public void DecodeCursorWithBatch_WhenBatchMissing_DefaultsToZero()
    {
        var cursor = CursorUtils.EncodeCursor(55, 2, 3);
        var (_, _, _, batch) = CursorUtils.DecodeCursorWithBatch(cursor);

        Assert.That(batch, Is.EqualTo(0));
    }

    [Test]
    public void DecodeCursorWithBatch_InvalidInputReturnsNulls()
    {
        var (block, tx, log, batch) = CursorUtils.DecodeCursorWithBatch("invalid");

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
            Assert.That(batch, Is.Null);
        });
    }
}
