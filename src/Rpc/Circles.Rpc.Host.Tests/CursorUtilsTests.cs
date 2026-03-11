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

    // ═══════════════════════════════════════════════════════════════════════
    // BUG-6 regression tests: malformed cursor silently returns page 1
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DecodeCursor_ValidBase64ButWrongFormat_ReturnsNulls()
    {
        // Valid base64 but not "block:tx:log" format — just "hello"
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void DecodeCursor_ValidBase64WithTwoParts_ReturnsNulls()
    {
        // Valid base64 "123:456" — only 2 parts, needs 3
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("123:456"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void DecodeCursor_NegativeValues_ParsesSuccessfully()
    {
        // Negative values are technically valid base64-encoded cursor data
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("-1:-2:-3"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        // Currently these parse successfully — we may want to reject them
        Assert.Multiple(() =>
        {
            Assert.That(block, Is.EqualTo(-1));
            Assert.That(tx, Is.EqualTo(-2));
            Assert.That(log, Is.EqualTo(-3));
        });
    }

    [Test]
    public void DecodeCursor_TamperedCursor_NonNumericParts_ReturnsNulls()
    {
        // Tampered cursor with non-numeric parts
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("abc:def:ghi"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void DecodeCursor_EmptyString_ReturnsNulls()
    {
        var (block, tx, log) = CursorUtils.DecodeCursor("");

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void DecodeCursor_Null_ReturnsNulls()
    {
        var (block, tx, log) = CursorUtils.DecodeCursor(null);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void DecodeCursor_OverflowValues_ReturnsNulls()
    {
        // Values that overflow long/int
        var cursor = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("99999999999999999999:1:1"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        // Should return nulls because long.Parse overflows
        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // wasMalformed output parameter tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DecodeCursor_ValidCursor_WasMalformedIsFalse()
    {
        var cursor = CursorUtils.EncodeCursor(100, 5, 2);
        CursorUtils.DecodeCursor(cursor, out var wasMalformed);
        Assert.That(wasMalformed, Is.False);
    }

    [Test]
    public void DecodeCursor_NullCursor_WasMalformedIsFalse()
    {
        // Null = "no cursor" not "bad cursor"
        CursorUtils.DecodeCursor(null, out var wasMalformed);
        Assert.That(wasMalformed, Is.False);
    }

    [Test]
    public void DecodeCursor_EmptyCursor_WasMalformedIsFalse()
    {
        CursorUtils.DecodeCursor("", out var wasMalformed);
        Assert.That(wasMalformed, Is.False);
    }

    [Test]
    public void DecodeCursor_GarbageCursor_WasMalformedIsTrue()
    {
        CursorUtils.DecodeCursor("not-valid-base64!", out var wasMalformed);
        Assert.That(wasMalformed, Is.True);
    }

    [Test]
    public void DecodeCursor_ValidBase64ButWrongFormat_WasMalformedIsTrue()
    {
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("just-text"));
        CursorUtils.DecodeCursor(cursor, out var wasMalformed);
        Assert.That(wasMalformed, Is.True);
    }

    [Test]
    public void EncodeCursor_LargeBlockNumber_RoundTrips()
    {
        // Test with max realistic block number
        var cursor = CursorUtils.EncodeCursor(long.MaxValue, int.MaxValue, int.MaxValue);
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.EqualTo(long.MaxValue));
            Assert.That(tx, Is.EqualTo(int.MaxValue));
            Assert.That(log, Is.EqualTo(int.MaxValue));
        });
    }
}
