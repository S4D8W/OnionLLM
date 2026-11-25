using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.5: dostawca Hermes (<c>billing_provider</c>) i kandydaci cennika.</summary>
public class HermesSourcePricingTests
{
    [Fact]
    public void Billing_provider_is_normalized_or_inferred_from_the_model()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "state.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT, model TEXT, started_at REAL, input_tokens INTEGER,
                    output_tokens INTEGER, billing_provider TEXT);
                INSERT INTO sessions VALUES ('a', 'cli', 'gpt-5', 1767312000, 10, 5, 'openai-codex');
                INSERT INTO sessions VALUES ('b', 'cli', 'gpt-5', 1767312000, 10, 5, NULL);
                INSERT INTO sessions VALUES ('c', 'cli', 'gemini-4-argon', 1767312000, 10, 5, 'Vertex-AI');
                INSERT INTO sessions VALUES ('d', 'cli', 'hermes-4', 1767312000, 10, 5, NULL);
                INSERT INTO sessions VALUES ('e', 'cli', 'glm-4.7', 1767312000, 10, 5, 'zai');
                """;
            command.ExecuteNonQuery();
        }

        var entries = TestData.ParseAll(new HermesSource(), TestData.ConfigWith(HermesSource.SourceId, directory.Path))
            .OrderBy(entry => entry.SessionId).ToList();

        Assert.Equal(["openai", "openai", "vertex_ai", null, "zai"], entries.Select(entry => entry.Provider));
    }

    [Fact]
    public void Provider_qualified_candidate_comes_first()
    {
        // ccusage hermes/src/parser.rs: tries_provider_qualified_model_candidate_first
        Assert.Equal(["openai/gpt-5.5", "gpt-5.5"], HermesSource.ModelCandidates("gpt-5.5", "openai"));
        Assert.Equal(["hermes-4"], HermesSource.ModelCandidates("hermes-4", null));
        Assert.Equal("groq", HermesSource.NormalizeProvider("Groq", "llama"));
        Assert.Equal("google", HermesSource.NormalizeProvider("gemini", "gemini-4-argon"));
    }

    [Fact]
    public void Zai_qualified_glm_is_priced()
    {
        var entry = new Models.UsageEntry
        {
            Source = HermesSource.SourceId,
            TimestampMs = 1767312000000,
            Model = "glm-4.7",
            Provider = "zai",
            InputTokens = 1000,
            OutputTokens = 1000,
            IsSessionSummary = true
        };

        var result = new CostCalculator(PricingCatalog.Embedded(), CostMode.Calculate).Calculate(entry);

        // zai/glm-4.7: $0,6 / $2,2 za 1M.
        Assert.Equal(0.0028, result.CostUsd, 12);
    }
}
