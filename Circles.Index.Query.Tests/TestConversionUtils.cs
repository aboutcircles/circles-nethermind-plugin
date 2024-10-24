using System.Numerics;
using Circles.Index.Utils;

namespace Circles.Index.Query.Tests;

public class TestConversionUtils
{
    // [SetUp]
    // public void Setup()
    // {
    // }
    //
    // [Test]
    // public void ConvertCrcToCircles()
    // {
    //     ConversionUtils.CrcToCircles();
    // }
    //
    // [Test]
    // public void ConvertCirclesToCrc()
    // {
    //     ConversionUtils.CirclesToCrc();
    // }
    //
    // [Test]
    // public void ConvertCirclesToStaticCircles()
    // {
    //     ConversionUtils.CirclesToStaticCircles();
    // }
    //
    // [Test]
    // public void ConvertStaticCirclesToCircles()
    // {
    //     ConversionUtils.StaticCirclesToCircles();
    // }
    
    
    [Test]
    public void TestConvertCirclesToStaticCircles()
    {
        decimal staticCirclesBalance = ConversionUtils.CirclesToCrc(0.13151319940322485m);
    }
}