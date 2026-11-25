using System.Text;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class AntigravitySourceTests
{
    // Koder protobuf wzorowany na rust/adapters/antigravity/src/parser/test_support.rs.
    private sealed record Usage(
        ulong? ModelId = null,
        ulong Input = 0,
        ulong TotalOutput = 0,
        ulong CacheWrite = 0,
        ulong CacheRead = 0,
        ulong Reasoning = 0,
        ulong VisibleOutput = 0,
        string? MessageId = null,
        string? ResponseId = null,
        string? ProviderMessageId = null,
        ulong? Provider = null);

    private static void Varint(ulong value, List<byte> output)
    {
        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    private static void FieldVarint(ulong number, ulong value, List<byte> output)
    {
        Varint(number << 3, output);
        Varint(value, output);
    }

    private static void FieldBytes(ulong number, IReadOnlyCollection<byte> value, List<byte> output)
    {
        Varint((number << 3) | 2, output);
        Varint((ulong)value.Count, output);
        output.AddRange(value);
    }

    private static List<byte> UsageBlob(Usage usage)
    {
        var output = new List<byte>();
        if (usage.ModelId is { } modelId)
        {
            FieldVarint(1, modelId, output);
        }

        foreach (var (field, value) in new[] { (2UL, usage.Input), (3UL, usage.TotalOutput), (4UL, usage.CacheWrite), (5UL, usage.CacheRead), (9UL, usage.Reasoning), (10UL, usage.VisibleOutput) })
        {
            if (value > 0)
            {
                FieldVarint(field, value, output);
            }
        }

        if (usage.MessageId is not null)
        {
            FieldBytes(7, Encoding.UTF8.GetBytes(usage.MessageId), output);
        }

        if (usage.ResponseId is not null)
        {
            FieldBytes(11, Encoding.UTF8.GetBytes(usage.ResponseId), output);
        }

        if (usage.ProviderMessageId is not null)
        {
            FieldBytes(12, Encoding.UTF8.GetBytes(usage.ProviderMessageId), output);
        }

        if (usage.Provider is { } provider)
        {
            FieldVarint(6, provider, output);
        }

        return output;
    }

    private static List<byte> TimestampBlob(ulong seconds, ulong nanos)
    {
        var output = new List<byte>();
        FieldVarint(1, seconds, output);
        FieldVarint(2, nanos, output);
        return output;
    }

    private static byte[] GenerationBlob(string? model, Usage? usage, (ulong Seconds, ulong Nanos)? timestamp, Usage[]? retries = null, long[]? steps = null)
    {
        var chat = new List<byte>();
        if (usage is not null)
        {
            if (usage.ModelId is { } modelId)
            {
                FieldVarint(3, modelId, chat);
            }

            FieldBytes(4, UsageBlob(usage), chat);
        }

        if (timestamp is { } ts)
        {
            var info = new List<byte>();
            FieldBytes(4, TimestampBlob(ts.Seconds, ts.Nanos), info);
            FieldBytes(9, info, chat);
        }

        foreach (var retry in retries ?? [])
        {
            var retryInfo = new List<byte>();
            FieldBytes(2, UsageBlob(retry), retryInfo);
            FieldBytes(17, retryInfo, chat);
        }

        if (model is not null)
        {
            FieldBytes(19, Encoding.UTF8.GetBytes(model), chat);
        }

        var metadata = new List<byte>();
        FieldBytes(1, chat, metadata);
        foreach (var step in steps ?? [])
        {
            FieldVarint(2, (ulong)step, metadata);
        }

        return metadata.ToArray();
    }

    private static byte[] StepBlob(string? model, Usage? usage, (ulong Seconds, ulong Nanos)? timestamp, ulong? provider = null)
    {
        var metadata = new List<byte>();
        if (timestamp is { } ts)
        {
            FieldBytes(8, TimestampBlob(ts.Seconds, ts.Nanos), metadata);
        }

        if (usage is not null)
        {
            FieldBytes(9, UsageBlob(usage), metadata);
        }

        if (model is not null)
        {
            var info = new List<byte>();
            FieldBytes(12, Encoding.UTF8.GetBytes(model), info);
            if (provider is { } api)
            {
                FieldVarint(7, api, info);
            }

            FieldBytes(24, info, metadata);
        }

        return metadata.ToArray();
    }

    private static void CreateDatabase(string path, (long Idx, byte[] Data)[] generations, (long Idx, byte[] Data)[]? steps = null, byte[][]? trajectories = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB NOT NULL);
                CREATE TABLE steps (idx INTEGER PRIMARY KEY, metadata BLOB);
                CREATE TABLE trajectory_metadata_blob (data BLOB);
                """;
            create.ExecuteNonQuery();
        }

        void Insert(string sql, long? idx, byte[] data)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = sql;
            if (idx is not null)
            {
                insert.Parameters.AddWithValue("$i", idx.Value);
            }

            insert.Parameters.AddWithValue("$d", data);
            insert.ExecuteNonQuery();
        }

        foreach (var (idx, data) in generations)
        {
            Insert("INSERT INTO gen_metadata (idx, data) VALUES ($i, $d)", idx, data);
        }

        foreach (var (idx, data) in steps ?? [])
        {
            Insert("INSERT INTO steps (idx, metadata) VALUES ($i, $d)", idx, data);
        }

        foreach (var data in trajectories ?? [])
        {
            Insert("INSERT INTO trajectory_metadata_blob (data) VALUES ($d)", null, data);
        }
    }

    private static List<Models.UsageEntry> Load(string root, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new AntigravitySource(), TestData.ConfigWith(AntigravitySource.SourceId, root), diagnostics);

    /// <summary>Najlepszy identyfikator zdarzenia — pierwsza część klucza deduplikacji.</summary>
    private static string Primary(Models.UsageEntry entry) => entry.DedupeKey!.Split('\u001f')[0];

    [Fact]
    public void Events_sharing_an_identity_merge_across_databases_with_the_larger_value_of_each_field()
    {
        // ccusage antigravity/src/loader/tests.rs: deduplicates_response_ids_across_databases.
        using var directory = new TempDirectory();
        CreateDatabase(System.IO.Path.Combine(directory.Path, "first", "conversations", "first.db"),
        [
            (1, GenerationBlob("gemini-3-pro", new Usage(Input: 20, TotalOutput: 40, VisibleOutput: 35, Reasoning: 5, MessageId: "shared-message",
                ResponseId: "shared-response", ProviderMessageId: "shared-provider"), (1_778_000_000, 0)))
        ]);
        CreateDatabase(System.IO.Path.Combine(directory.Path, "second", "conversations", "second.db"),
        [
            (1, GenerationBlob("gemini-3-pro", new Usage(Input: 200, TotalOutput: 400, VisibleOutput: 350, Reasoning: 50, MessageId: "shared-message",
                ResponseId: "retry-response", ProviderMessageId: "shared-provider"), (1_778_000_001, 0)))
        ]);
        var config = new Config.OnionConfig { Timezone = "UTC" };
        config.Sources[AntigravitySource.SourceId] = new Config.SourceSettings
        {
            Paths = [System.IO.Path.Combine(directory.Path, "first"), System.IO.Path.Combine(directory.Path, "second")]
        };
        var source = new AntigravitySource();

        var parsed = TestData.ParseAll(source, config);
        var entries = Deduplicator.Apply(parsed, _ => source);

        Assert.Equal(2, parsed.Count);
        var entry = Assert.Single(entries);
        Assert.Equal("response:shared-response", Primary(entry));
        Assert.Equal(200, entry.InputTokens);
        Assert.Equal(400, entry.OutputTokens);
        Assert.Equal(50, entry.ReasoningTokens);
        Assert.Equal(1_778_000_000_000, entry.TimestampMs);
    }

    [Fact]
    public void Vertex_requests_record_the_provider_and_price_the_vertex_listing_first()
    {
        // ccusage parser/tests.rs: only_google_providers_receive_google_model_candidates; ModelInfo pole 7, ModelUsage pole 6.
        using var directory = new TempDirectory();
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "conv.db"),
            [(0, GenerationBlob(null, new Usage(Input: 1, MessageId: "gemini-api", Provider: 24), (1767312000, 0)))],
            [
                (7, StepBlob("gemini-unpriced", new Usage(Input: 1, MessageId: "vertex-step"), (1767312000, 0), provider: 3)),
                (8, StepBlob("gemini-unpriced", new Usage(Input: 1, MessageId: "other", Provider: 26), (1767312000, 0)))
            ]);
        var source = new AntigravitySource();

        var entries = Load(directory.Path).ToDictionary(Primary);

        Assert.Equal("vertex_ai", entries["message:vertex-step"].Provider);
        Assert.Equal(["vertex_ai/gemini-unpriced", "gemini-unpriced"], source.PricingCandidates(entries["message:vertex-step"]));
        Assert.Null(entries["message:other"].Provider);
        Assert.Equal(["gemini-unpriced"], source.PricingCandidates(entries["message:other"]));
        Assert.Null(entries["message:gemini-api"].Provider);
        Assert.Equal(
            ["gemini-unpriced", "google/gemini-unpriced", "gemini/gemini-unpriced", "vertex_ai/gemini-unpriced", "openrouter/google/gemini-unpriced"],
            source.PricingCandidates(entries["message:gemini-api"] with { Model = "gemini-unpriced" }));
    }

    [Fact]
    public void Logged_model_name_is_priced_before_its_normalized_form()
    {
        // ccusage loader/tests.rs: prices_the_logged_model_before_its_normalized_form.
        using var directory = new TempDirectory();
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "session.db"),
            [(1, GenerationBlob("GPT-Logged-Model", new Usage(Input: 100), (1_778_000_000, 0)))]);

        var entry = Assert.Single(Load(directory.Path));

        Assert.Equal("gpt-logged-model", entry.Model);
        Assert.Equal(["GPT-Logged-Model", "gpt-logged-model"], new AntigravitySource().PricingCandidates(entry));
    }

    [Fact]
    public void Flash_effort_variant_and_thinking_base_are_fallback_candidates()
    {
        Assert.Equal(["gemini-3.6-flash-high", "gemini-3.6-flash"], AntigravitySource.ModelCandidates("gemini-3.6-flash-high", null));
        var entry = new Models.UsageEntry { Source = AntigravitySource.SourceId, TimestampMs = 0, Model = "gemini-2.5-flash-thinking" };
        Assert.Equal(["gemini-2.5-flash-thinking", "gemini-2.5-flash"], new AntigravitySource().PricingCandidates(entry));
    }

    [Fact]
    public void Generation_usage_maps_buckets_and_output_includes_reasoning()
    {
        using var directory = new TempDirectory();
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "conv-1.db"),
        [
            (0, GenerationBlob("Gemini 3.8 Flash (High)", new Usage(Input: 1000, TotalOutput: 300, CacheWrite: 50, CacheRead: 4000, Reasoning: 200, VisibleOutput: 100, ResponseId: "resp-1"), (1767312000, 250_000_000)))
        ]);

        var entry = Assert.Single(Load(directory.Path));

        Assert.Equal("conv-1", entry.SessionId);
        Assert.Equal("gemini-3.8-flash-high", entry.Model);
        Assert.Equal(1767312000250, entry.TimestampMs);
        Assert.Equal(1000, entry.InputTokens);
        Assert.Equal(300, entry.OutputTokens);
        Assert.Equal(200, entry.ReasoningTokens);
        Assert.Equal(50, entry.CacheCreationTokens);
        Assert.Equal(4000, entry.CacheReadTokens);
        Assert.Equal("response:resp-1", Primary(entry));
    }

    [Fact]
    public void Continuation_rows_inherit_model_and_mirror_retry_is_dropped()
    {
        using var directory = new TempDirectory();
        var primary = new Usage(ModelId: 1318, Input: 10, VisibleOutput: 5, MessageId: "m-1");
        var distinctRetry = new Usage(ModelId: 1318, Input: 7, VisibleOutput: 1, MessageId: "m-retry");
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "conv.db"),
        [
            (0, GenerationBlob(null, primary, (1767312000, 0), [primary, distinctRetry])),
            (1, GenerationBlob(null, new Usage(Input: 3, VisibleOutput: 2, MessageId: "m-2"), (1767312060, 0)))
        ]);

        var entries = Load(directory.Path).OrderBy(Primary).ToList();

        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal("gemini-3.8-flash-high", entry.Model));
        Assert.Equal(["message:m-1", "message:m-2", "message:m-retry"], entries.Select(Primary).ToArray());
    }

    [Fact]
    public void Step_and_generation_copies_merge_and_use_trajectory_timestamp_fallback()
    {
        using var directory = new TempDirectory();
        var usage = new Usage(Input: 100, TotalOutput: 40, Reasoning: 10, VisibleOutput: 30);
        var trajectory = new List<byte>();
        FieldBytes(2, TimestampBlob(1767312000, 0), trajectory);
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "conv.db"),
            [(0, GenerationBlob("claude-sonnet-4-5", usage, null, steps: [7]))],
            [(7, StepBlob("Claude Sonnet 4.5", usage, null))],
            [trajectory.ToArray()]);

        var entry = Assert.Single(Load(directory.Path));

        Assert.Equal(1767312000000, entry.TimestampMs);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(40, entry.OutputTokens);
        Assert.Equal("step:conv:7", Primary(entry));
    }

    [Fact]
    public void Custom_root_without_conversations_dir_and_non_conversation_databases()
    {
        using var directory = new TempDirectory();
        CreateDatabase(System.IO.Path.Combine(directory.Path, "direct.db"),
            [(0, GenerationBlob("gemini-2.5-pro", new Usage(Input: 5, VisibleOutput: 1), (1767312000, 0)))]);
        var summaries = System.IO.Path.Combine(directory.Path, "conversation_summaries.db");
        using (var connection = new SqliteConnection($"Data Source={summaries};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE summaries (id TEXT)";
            create.ExecuteNonQuery();
        }

        var diagnostics = new ParseDiagnostics();
        var entry = Assert.Single(Load(directory.Path, diagnostics));

        Assert.Equal("gemini-2.5-pro", entry.Model);
        Assert.Equal(0, diagnostics.SkippedLines);
    }

    [Fact]
    public void Corrupt_protobuf_rejects_the_whole_database_and_duplicates_across_databases_collapse()
    {
        using var directory = new TempDirectory();
        var usage = new Usage(Input: 5, VisibleOutput: 1, ResponseId: "shared");
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "a.db"), [(0, GenerationBlob(null, usage, (1767312000, 0)))]);
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "b.db"), [(0, GenerationBlob(null, usage, (1767312000, 0)))]);
        CreateDatabase(System.IO.Path.Combine(directory.Path, "conversations", "broken.db"),
            [(0, GenerationBlob(null, usage, (1767312000, 0))), (1, [0x0A, 0x05, 0x01])]);
        var diagnostics = new ParseDiagnostics();

        var entries = Load(directory.Path, diagnostics);

        Assert.Equal(2, entries.Count);
        Assert.Single(Deduplicator.Apply(entries));
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Theory]
    [InlineData("Gemini 3.7 Pro (Thinking)", "gemini-3.7-pro")]
    [InlineData("MODEL_PLACEHOLDER_M402", "claude-opus-5-5")]
    [InlineData("Claude Sonnet 4.6", "claude-sonnet-4.6")]
    [InlineData("custom-model", "custom-model")]
    public void Model_names_are_normalized(string raw, string expected) =>
        Assert.Equal(expected, AntigravitySource.Normalize(raw));
}
