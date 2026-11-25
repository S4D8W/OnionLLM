using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class BlockTests
{
    private static readonly CostCalculator Calculator = new(PricingCatalog.Embedded(), CostMode.Calculate);

    [Fact]
    public void Blocks_split_on_duration_and_gaps()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T08:15:00Z", 100, 0),
            TestData.Entry("2026-11-24T10:00:00Z", 100, 0),
            TestData.Entry("2026-11-24T13:30:00Z", 100, 0), // > 5h od 08:00 → nowy blok
            TestData.Entry("2026-11-24T23:00:00Z", 100, 0)  // > 5h przerwy → przerwa + nowy blok
        };

        var report = BlockBuilder.Build(entries, Calculator, DateTimeOffset.Parse("2026-11-25T01:00:00Z"));

        var blocks = report.Blocks;
        Assert.Equal(4, blocks.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T08:00:00Z"), blocks[0].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T13:00:00Z"), blocks[1].Start);
        Assert.True(blocks[2].IsGap);
        Assert.True(blocks[3].IsActive);
        Assert.Equal(200, report.TokenLimit); // max z zakończonych bloków
    }

    [Fact]
    public void Active_block_has_burn_rate_and_projection()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 1000, 0),
            TestData.Entry("2026-11-24T10:10:00Z", 1000, 0)
        };

        var report = BlockBuilder.Build(entries, Calculator, DateTimeOffset.Parse("2026-11-24T11:00:00Z"));
        var active = Assert.IsType<SessionBlock>(report.Active);

        Assert.Equal(200, active.BurnRate!.TokensPerMinute, 6);
        Assert.Equal(240, active.Projection!.RemainingMinutes);
        Assert.Equal(2000 + 200 * 240, active.Projection.TotalTokens);
        Assert.Equal(TimeSpan.FromHours(4), active.Remaining(DateTimeOffset.Parse("2026-11-24T11:00:00Z")));
    }

    [Fact]
    public void Limit_status_thresholds()
    {
        Assert.Equal(LimitStatus.Ok, BlocksReport.Status(50, 100));
        Assert.Equal(LimitStatus.Warning, BlocksReport.Status(85, 100));
        Assert.Equal(LimitStatus.Exceeds, BlocksReport.Status(101, 100));
    }
}
