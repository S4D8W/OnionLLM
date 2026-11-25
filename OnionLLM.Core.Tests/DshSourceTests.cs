using System.IO.Compression;
using System.Text;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class DshSourceTests
{
    // Fikstury wzorowane na rust/adapters/dsh/tests/fixtures (parent / fork).
    private const string Parent = """
        {"type":"session","version":4,"id":"dsh-parent","createdAt":1791968400000,"cwd":"/home/user/project","isSeeded":false}
        {"type":"request/header","seq":1,"time":1791968400100,"data":{"header":{"config":{"provider":"deepseek-official","model":"deepseek-v4-pro"}},"reason":"initial"}}
        {"type":"request\/context","seq":2,"time":1791968400101,"data":{"provider":"deepseek-official","model":"deepseek-v4-pro","contextWindow":1048576}}
        {"type":"assistant/message","seq":3,"time":1791968405000,"data":{"stream":[{"type":"chunk","chunk":{"type":"usage","usage":{"inputTokens":1200,"outputTokens":300}}}],"usage":{"inputTokens":1200,"outputTokens":300,"cacheReadTokens":8000,"totalTokens":9500}}}
        {"type":"assistant/attempt","seq":4,"time":1791972000000,"data":{"stream":[{"type":"chunk","chunk":{"type":"usage","usage":{"inputTokens":50,"outputTokens":0}}}]}}
        {"type":"assistant/message","seq":5,"time":1791972005000,"data":{"stream":[],"usage":{"inputTokens":400,"outputTokens":120,"cacheReadTokens":9000,"cacheWriteTokens":600,"totalTokens":10120}}}
        {"type":"compaction/summary","seq":6,"time":1792107000000,"data":{"provider":"deepseek-account","model":"deepseek-flash","usage":{"inputTokens":19000,"outputTokens":800,"totalTokens":19800}}}
        """;

    private const string Fork = """
        {"type":"session","version":4,"id":"dsh-fork","createdAt":1792483200000,"cwd":"/home/user/project","parentSession":"dsh-parent","isSeeded":true}
        {"type":"request/header","seq":1,"time":1791968400100,"data":{"header":{"config":{"provider":"deepseek-official","model":"deepseek-v4-pro"}}}}
        {"type":"assistant/message","seq":3,"time":1791968405000,"data":{"usage":{"inputTokens":1200,"outputTokens":300,"cacheReadTokens":8000,"totalTokens":9500}}}
        {"type":"session/end-seed","seq":4,"time":1792483200000,"data":{"inherited":true}}
        {"type":"request/header","seq":5,"time":1792483200100,"data":{"header":{"config":{"provider":"anthropic","model":"claude-sonnet-4-5"}}}}
        {"type":"assistant/message","seq":6,"time":1792483205000,"data":{"usage":{"inputTokens":3000,"outputTokens":500,"cacheWriteTokens":2000,"totalTokens":5500}}}
        {"type":"assistant/message","seq":7,"time":1793102400000,"data":{"usage":{"inputTokens":200,"outputTokens":50,"cacheReadTokens":5000,"totalTokens":5250}}}
        """;

    private static List<Models.UsageEntry> Load(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new DshSource(), TestData.ConfigWith(DshSource.SourceId, directory.Path), diagnostics);

    [Fact]
    public void Parent_session_maps_routes_stream_fallback_and_compaction()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/project/dsh-parent/session.v4.jsonl", Parent);

        var entries = Load(directory);

        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry => Assert.Equal("dsh-parent", entry.SessionId));
        Assert.All(entries, entry => Assert.Equal("/home/user/project", entry.ProjectPath));
        Assert.Equal(1200, entries[0].InputTokens);
        Assert.Equal(8000, entries[0].CacheReadTokens);
        Assert.Equal("deepseek-v4-pro", entries[0].Model);
        Assert.Equal(50, entries[1].InputTokens); // assistant/attempt: ostatni fragment usage ze stream
        Assert.Equal(600, entries[2].CacheCreationTokens);
        Assert.Equal("deepseek-flash", entries[3].Model);
        Assert.Equal(800, entries[3].OutputTokens);
    }

    [Fact]
    public void Fork_counts_only_usage_after_inherited_marker()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/project/dsh-parent/session.v4.jsonl", Parent);
        directory.Write("sessions/project/dsh-fork/session.v4.jsonl", Fork);

        var fork = Deduplicator.Apply(Load(directory)).Where(entry => entry.SessionId == "dsh-fork").ToList();

        Assert.Equal(2, fork.Count);
        Assert.All(fork, entry => Assert.Equal("claude-sonnet-4-5", entry.Model));
        Assert.Equal(2000, fork[0].CacheCreationTokens);
    }

    [Fact]
    public void Seeded_log_without_marker_is_parent_usage_only()
    {
        using var directory = new TempDirectory();
        var lines = Fork.Split('\n');
        directory.Write("sessions/project/dsh-fork/session.jsonl", string.Join('\n', lines.Take(3)));

        Assert.Empty(Load(directory));
    }

    [Fact]
    public void Only_newest_generation_is_read()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/project/dsh-parent/session.jsonl", Parent);
        directory.Write("sessions/project/dsh-parent/session.v2.jsonl", Parent);
        directory.Write("sessions/project/dsh-parent/session.v10.jsonl", Parent);
        directory.Write("sessions/project/dsh-parent/session.vX.jsonl", Parent);

        var files = new DshSource().EnumerateFiles(TestData.ConfigWith(DshSource.SourceId, directory.Path)).ToList();

        Assert.EndsWith("session.v10.jsonl", Assert.Single(files).Path);
    }

    [Fact]
    public void Missing_output_is_recovered_from_total_and_seq_dedupes()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/p/s/session.jsonl", string.Join('\n',
            """{"type":"session","id":"s","createdAt":1791968400000}""",
            """{"type":"assistant/message","seq":3,"data":{"usage":{"inputTokens":100,"cacheReadTokens":50,"totalTokens":180}}}""",
            """{"type":"assistant/message","seq":3,"data":{"usage":{"inputTokens":100,"cacheReadTokens":50,"totalTokens":180}}}"""));

        var entries = Load(directory);
        var entry = Assert.Single(Deduplicator.Apply(entries));

        Assert.Equal(30, entry.OutputTokens);
        Assert.Equal(1791968400000, entry.TimestampMs); // brak time → createdAt nagłówka
        Assert.Equal(DshSource.UnknownModel, entry.Model);
        Assert.True(entry.IsFallbackModel);
    }

    [Fact]
    public void Zstd_log_with_torn_tail_keeps_earlier_frames()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "sessions", "project", "dsh-parent", "session.v4.jsonl.zstd");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var lines = Parent.Split('\n');
        using (var output = File.Create(path))
        {
            // Jedna ramka zstd na partię zapisu, jak w DeepSeek Harness.
            foreach (var batch in new[] { lines[..4], lines[4..] })
            {
                var buffer = new MemoryStream();
                using (var zstd = new ZstandardStream(buffer, CompressionMode.Compress, leaveOpen: true))
                {
                    var bytes = Encoding.UTF8.GetBytes(string.Join('\n', batch) + "\n");
                    zstd.Write(bytes);
                }

                output.Write(buffer.ToArray());
            }

            // Ucięta trzecia ramka.
            var torn = new MemoryStream();
            using (var zstd = new ZstandardStream(torn, CompressionMode.Compress, leaveOpen: true))
            {
                zstd.Write(Encoding.UTF8.GetBytes(lines[3] + "\n"));
            }

            output.Write(torn.ToArray().AsSpan(0, (int)torn.Length / 2));
        }

        var diagnostics = new ParseDiagnostics();
        var entries = Load(directory, diagnostics);

        Assert.Equal(4, entries.Count);
        Assert.Equal(1, diagnostics.SkippedLines);
    }
}
