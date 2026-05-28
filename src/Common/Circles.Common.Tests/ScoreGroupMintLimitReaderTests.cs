namespace Circles.Common.Tests;

[TestFixture]
public sealed class ScoreGroupMintLimitReaderTests
{
    [Test]
    public void NormalizeFilter_Null_ReturnsNull()
    {
        Assert.That(ScoreGroupMintLimitReader.NormalizeFilter(null), Is.Null);
    }

    [Test]
    public void NormalizeFilter_Whitespace_ReturnsNull()
    {
        Assert.That(ScoreGroupMintLimitReader.NormalizeFilter("   "), Is.Null);
        Assert.That(ScoreGroupMintLimitReader.NormalizeFilter(""), Is.Null);
    }

    [Test]
    public void NormalizeFilter_MixedCase_Lowercases()
    {
        Assert.That(
            ScoreGroupMintLimitReader.NormalizeFilter("0x24c9BA1fB88533B0cD2aEa37DaD75B809eEcF2C0"),
            Is.EqualTo("0x24c9ba1fb88533b0cd2aea37dad75b809eecf2c0"));
    }

    [Test]
    public void NormalizeFilter_WrappedWhitespace_Trimmed()
    {
        Assert.That(
            ScoreGroupMintLimitReader.NormalizeFilter("  0xABC  "),
            Is.EqualTo("0xabc"));
    }
}
