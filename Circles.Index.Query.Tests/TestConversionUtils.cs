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
        var inflation = 0.07m;
        var year = 4;
        var baseAmount = 8m;
        var inflatedAmount = baseAmount;
        for (int i = 0; i < year; i++)
        {
            inflatedAmount *= 1 + inflation;
        }

        
        
        decimal circlesBalance = 20;
        decimal staticCirclesBalance = ConversionUtils.CirclesToStaticCircles(circlesBalance, DateTime.Now);
    }
}