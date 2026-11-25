using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.5: dostawca Goose (<c>provider_name</c>) i kandydaci cennika.</summary>
public class GooseSourcePricingTests
{
    [Fact]
    public void Provider_name_is_recorded()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "sessions.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY, model_config_json TEXT, provider_name TEXT, created_at TEXT,
                    total_tokens INTEGER, input_tokens INTEGER, output_tokens INTEGER,
                    accumulated_total_tokens INTEGER, accumulated_input_tokens INTEGER, accumulated_output_tokens INTEGER);
                INSERT INTO sessions VALUES ('a', '{"model_name":"qwen-test"}', 'OpenRouter', '2026-11-17T10:00:00Z', 200, 100, 100, 0, 0, 0);
                INSERT INTO sessions VALUES ('b', '{"model_name":"qwen-test"}', ' ', '2026-11-17T10:00:00Z', 200, 100, 100, 0, 0, 0);
                INSERT INTO sessions VALUES ('c', '{"model_name":"gemini-2.5-pro"}', 'google-vertex', '2026-11-17T10:00:00Z', 200, 100, 100, 0, 0, 0);
                """;
            command.ExecuteNonQuery();
        }

        var entries = TestData.ParseAll(new GooseSource(), TestData.ConfigWith(GooseSource.SourceId, directory.Path))
            .OrderBy(entry => entry.SessionId).ToList();

        Assert.Equal(["openrouter", null, "vertex_ai"], entries.Select(entry => entry.Provider));
    }

    [Fact]
    public void Logged_provider_qualifies_the_model_ahead_of_the_bare_name()
    {
        // ccusage goose/src/parser.rs: inferred_openrouter_does_not_override_the_vendor_price, adapter_pricing_identity
        Assert.Equal(["openrouter/qwen-test", "qwen-test"], GooseSource.ModelCandidates("qwen-test", "openrouter"));
        Assert.Equal(["qwen-test", "openrouter/qwen-test"], GooseSource.ModelCandidates("qwen-test", null));
        Assert.Equal(["gpt-5.2", "openai/gpt-5.2"], GooseSource.ModelCandidates("gpt-5.2", null));
        Assert.Equal(["open_ai/gpt-5.2", "gpt-5.2"], GooseSource.ModelCandidates("gpt-5.2", "open-ai"));
        Assert.Equal(["vertex_ai/gemini-2.5-pro", "gemini-2.5-pro"], GooseSource.ModelCandidates("gemini-2.5-pro", "vertex_ai"));
        Assert.Equal(["custom-model"], GooseSource.ModelCandidates("custom-model", null));
        Assert.Equal(["custom-model"], GooseSource.ModelCandidates("custom-model", "goose"));
    }
}
