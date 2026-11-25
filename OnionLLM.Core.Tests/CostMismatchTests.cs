using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;

namespace OnionLLM.Core.Tests;

/// <summary>Rozbieżności kosztu zapisanego i wyliczonego (Ustawienia → Diagnostyka, CLI <c>--debug</c>).</summary>
public class CostMismatchTests
{
    private static CostCalculator Calculator(CostMode mode = CostMode.Auto) => new(PricingCatalog.Embedded(), mode);

    [Fact]
    public void Compares_stored_and_calculated_costs_with_tolerance_and_samples()
    {
        // claude-sonnet-4-5: 1000 tokenów wejścia = $0.003.
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 1000, 0, recorded: 0.003),       // zgodny
            TestData.Entry("2026-11-24T10:01:00Z", 1000, 0, recorded: 0.0030001),   // zgodny (< 0,1%)
            TestData.Entry("2026-11-24T10:02:00Z", 1000, 0, recorded: 0.006),       // rozbieżny o $0.003
            TestData.Entry("2026-11-24T10:03:00Z", 1000, 0, recorded: 0.1),         // rozbieżny o ~$0.097
            TestData.Entry("2026-11-24T10:04:00Z", 1000, 0),                        // bez kosztu zapisanego
            TestData.Entry("2026-11-24T10:05:00Z", 1000, 0, model: "mystery-model-9000", recorded: 0.01)
        };

        var report = PricingMismatchAnalyzer.Analyze(entries, Calculator(), samples: 1);

        Assert.Equal(6, report.TotalEntries);
        Assert.Equal(5, report.EntriesWithStoredCost);
        Assert.Equal(4, report.Compared);
        Assert.Equal(2, report.Matches);
        Assert.Equal(2, report.Mismatches);
        Assert.Equal(1, report.Unpriced);
        Assert.Single(report.Samples);
        Assert.Equal(0.1, report.Samples[0].StoredCostUsd, 9);
        Assert.Equal(0.003, report.Samples[0].CalculatedCostUsd, 9);

        var sonnet = report.Models.Single(model => model.Model == "claude-sonnet-4-5");
        Assert.Equal(4, sonnet.Compared);
        Assert.Equal(0.5, sonnet.MatchRate, 9);
        Assert.Equal(1, report.Models.Single(model => model.Model == "mystery-model-9000").Unpriced);

        var text = report.Format(TimeZoneInfo.Utc);
        Assert.Contains("Zgodne", text);
        Assert.Contains("2026-11-24 10:03:00", text);
    }

    [Fact]
    public void Calculates_from_pricing_even_in_display_mode()
    {
        var entries = new[] { TestData.Entry("2026-11-24T10:00:00Z", 1000, 0, recorded: 0.006) };

        var report = PricingMismatchAnalyzer.Analyze(entries, Calculator(CostMode.Display));

        Assert.Equal(0.003, report.CalculatedCostUsd, 9);
        Assert.Equal(1, report.Mismatches);
    }

    [Fact]
    public void Empty_input_produces_readable_message()
    {
        var report = PricingMismatchAnalyzer.Analyze([TestData.Entry("2026-11-24T10:00:00Z", 1, 1)], Calculator());

        Assert.Equal(0, report.Compared);
        Assert.Contains("Brak wpisów z kosztem zapisanym", report.Format());
    }
}
