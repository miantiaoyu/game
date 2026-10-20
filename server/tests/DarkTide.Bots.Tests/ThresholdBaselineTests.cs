namespace DarkTide.Bots.Tests;

public class ThresholdBaselineTests
{
    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var original = ThresholdBotParameters.FairShareStart() with { RaisePotFraction = 0.75, RerouteMargin = 0.03, AdjustDown = 0.02, AdjustUp = 0.01 };

        var loaded = ThresholdBaseline.Load(ThresholdBaseline.Save(original));

        for (int s = 0; s < 3; s++)
        for (int k = 0; k <= EquityTable.MaxOpponents; k++)
        {
            Assert.Equal(original.Fold[s, k], loaded.Fold[s, k], 4);
            Assert.Equal(original.Raise[s, k], loaded.Raise[s, k], 4);
        }
        Assert.Equal(0.75, loaded.RaisePotFraction);
        Assert.Equal(0.03, loaded.RerouteMargin);
        Assert.Equal(0.02, loaded.AdjustDown);
        Assert.Equal(0.01, loaded.AdjustUp);
    }

    [Fact]
    public void StandardBaselineIsEmbeddedAndOrdered()
    {
        var standard = ThresholdBaseline.Standard;
        for (int s = 0; s < 3; s++)
        for (int k = 1; k <= EquityTable.MaxOpponents; k++)
        {
            // 加注门槛必须高于弃牌门槛，否则“跟注”这一段不存在。
            Assert.True(standard.Raise[s, k] > standard.Fold[s, k]);
            // 对手越多，门槛越低（公平份额 1/(k+1) 越小）。
            if (k > 1)
            {
                Assert.True(standard.Fold[s, k] <= standard.Fold[s, k - 1] + 1e-9);
            }
        }
    }

    [Theory]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":1,"fold":[[0,0,0,0,0,0,0,0]],"raise":[],"raisePotFraction":0.5,"rerouteMargin":0}""")]
    [InlineData("""{"version":1,"fold":[[0,1.5,0,0,0,0,0,0],[0,0,0,0,0,0,0,0],[0,0,0,0,0,0,0,0]],"raise":[[0,0,0,0,0,0,0,0],[0,0,0,0,0,0,0,0],[0,0,0,0,0,0,0,0]],"raisePotFraction":0.5,"rerouteMargin":0}""")]
    public void BrokenFilesAreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => ThresholdBaseline.Load(json));
    }
}
