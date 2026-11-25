using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.2 i 11.6.10: kandydaci cennika Droid, dostawca i wybór najnowszego snapshotu.</summary>
public class DroidSourcePricingTests
{
    private static readonly CostCalculator Calculator = new(PricingCatalog.Embedded(), CostMode.Calculate);

    private static Models.UsageEntry ParseOne(string json)
    {
        using var directory = new TempDirectory();
        directory.Write("s.settings.json", json);
        return Assert.Single(TestData.ParseAll(new DroidSource(), TestData.ConfigWith(DroidSource.SourceId, directory.Path)));
    }

    [Fact]
    public void Candidates_put_provider_prefixed_logged_name_first_then_raw_then_normalized()
    {
        Assert.Equal(
        [
            "google/gemini-2.5-pro", "vertex_ai/gemini-2.5-pro", "gemini-2.5-pro",
            "google/gemini-2-5-pro", "vertex_ai/gemini-2-5-pro", "gemini-2-5-pro", "openrouter/google/gemini-2-5-pro"
        ], DroidSource.ModelCandidates("gemini-2.5-pro", "google"));
        Assert.Equal(["glm-4.7", "glm-4-7"], DroidSource.ModelCandidates("glm-4.7", null));
        Assert.Equal(["zai/glm-4.7", "glm-4.7", "zai/glm-4-7", "glm-4-7", "openrouter/zai/glm-4-7"],
            DroidSource.ModelCandidates("glm-4.7", "zai"));
    }

    // ccusage droid/src/parser.rs: adapter_pricing_identity (stawki z wbudowanego cennika, 1000 + 1000 tokenów).
    [Theory]
    [InlineData("gpt-5.1", 1.25, 10.0)]
    [InlineData("gpt-5.1-codex", 1.25, 10.0)]
    [InlineData("gemini-2.5-pro", 1.25, 10.0)]
    [InlineData("glm-4.7", 0.6, 2.2)]
    public void Dotted_logged_models_are_priced(string raw, double input, double output)
    {
        var entry = ParseOne($$$"""{"model":"{{{raw}}}","providerLockTimestamp":"2026-11-17T10:00:00Z","tokenUsage":{"inputTokens":1000,"outputTokens":1000}}""");

        var result = Calculator.Calculate(entry);

        Assert.Equal(raw, entry.Model);
        Assert.False(result.IsUnpriced);
        Assert.Equal((input + output) / 1000, result.CostUsd, 12);
    }

    [Fact]
    public void Provider_comes_from_lock_or_is_inferred_from_the_model()
    {
        Assert.Equal("google", ParseOne("""{"model":"gemini-2.5-pro","tokenUsage":{"inputTokens":1}}""").Provider);
        Assert.Equal("anthropic", ParseOne("""{"model":"Claude-Sonnet-4-[Anthropic]","tokenUsage":{"inputTokens":1}}""").Provider);
        Assert.Null(ParseOne("""{"model":"glm-4.7","tokenUsage":{"inputTokens":1}}""").Provider);
        Assert.Equal("zai", ParseOne("""{"model":"glm-4.7","providerLock":"zai","tokenUsage":{"inputTokens":1}}""").Provider);
        Assert.Equal("openrouter", ParseOne("""{"model":"deepseek-v4-flash","providerLock":"openrouter","tokenUsage":{"inputTokens":1}}""").Provider);

        // ccusage: a_vertex_provider_lock_keeps_the_vertex_host
        foreach (var logged in new[] { "vertex", "vertex_ai", "Vertex-AI", "google-vertex", "google_vertex" })
        {
            Assert.Equal("vertex_ai", DroidSource.NormalizeProvider(logged));
        }

        Assert.Equal("google", DroidSource.NormalizeProvider("gemini"));
    }

    [Fact]
    public void Model_keeps_version_dots_but_normalizes_to_the_same_ccusage_name()
    {
        Assert.Equal("claude-opus-4.5-thinking-0", DroidSource.CleanModelName("custom:Claude-Opus-4.5-Thinking-[Anthropic]-0"));
        Assert.Equal(DroidSource.NormalizeModelName("custom:Claude-Opus-4.5-Thinking-[Anthropic]-0"),
            DroidSource.NormalizeModelName(DroidSource.CleanModelName("custom:Claude-Opus-4.5-Thinking-[Anthropic]-0")));
    }

    [Fact]
    public void Duplicate_session_keeps_the_latest_snapshot_in_either_order()
    {
        // ccusage droid/src/loader.rs: keeps_latest_snapshot_for_duplicate_session_ids
        using var directory = new TempDirectory();
        directory.Write("archive/session-c.settings.json", """
            {"model":"gpt-5","providerLock":"openai","providerLockTimestamp":"2026-05-01T01:02:03.000Z","tokenUsage":{"inputTokens":10,"outputTokens":20}}
            """);
        directory.Write("session-c.settings.json", """
            {"model":"gpt-5","providerLock":"openai","providerLockTimestamp":"2026-05-02T01:02:03.000Z","tokenUsage":{"inputTokens":100,"outputTokens":200}}
            """);
        var parsed = TestData.ParseAll(new DroidSource(), TestData.ConfigWith(DroidSource.SourceId, directory.Path));
        Assert.Equal(2, parsed.Count);

        foreach (var order in new[] { parsed, Enumerable.Reverse(parsed).ToList() })
        {
            var entry = Assert.Single(Deduplicator.Apply(order));
            Assert.Equal("session-c", entry.SessionId);
            Assert.Equal(100, entry.InputTokens);
            Assert.Equal(200, entry.OutputTokens);
        }
    }
}
