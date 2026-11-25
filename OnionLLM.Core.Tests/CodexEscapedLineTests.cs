using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Linie JSON z kluczami i wartościami zapisanymi przez ucieczki <c>\uXXXX</c> — czytane jak zapisane wprost
/// (ccusage <c>codex/src/parser/tests.rs</c> i <c>parser/diagnostic_tests.rs</c>).
/// </summary>
public class CodexEscapedLineTests
{
    private static List<UsageEntry> Parse(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path), diagnostics);

    private static string Join(params string[] lines) => string.Join('\n', lines);

    [Theory]
    [InlineData("""{"type":"turn\u005fcontext","payload":{}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"token\u005fcount"}}""")]
    [InlineData("""{"type":"compact\u0065d","payload":{}}""")]
    [InlineData("""{"type":"turn.completed","us\u0061ge":{"output_tokens":1}}""")]
    [InlineData("""{"\u0074ype":"session\u005Fmeta","payload":{}}""")]
    public void Recognizes_usage_lines_whose_markers_are_spelled_with_escapes(string line) =>
        Assert.True(CodexSource.SpellsWithEscapes(line, ["token_count", "turn_context", "session_meta", "compacted", "\"usage\""]));

    [Theory]
    [InlineData("""{"type":"response_item","payload":{"text":"a\nb \u001b[1m"}}""")]
    [InlineData("""{"type":"response_item","payload":{"text":"us\\u0061ge"}}""")]
    [InlineData("""{"type":"response_item","payload":{"text":"\"usage\""}}""")]
    public void Ignores_lines_whose_escapes_do_not_spell_a_marker(string line) =>
        Assert.False(CodexSource.SpellsWithEscapes(line, ["token_count", "turn_context", "\"usage\""]));

    [Fact]
    public void Loads_token_counts_whose_types_and_keys_are_spelled_with_escapes()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/session.jsonl", Join(
            """{"\u0074ype":"turn\u005fcontext","p\u0061yload":{"model":"gpt-5-mini"}}""",
            """{"timestamp":"2026-09-01T00:00:01Z","type":"event\u005fmsg","payload":{"type":"token\u005fcount","info":{"total_token_usage":{"input_tokens":100,"output_tokens":20}}}}""",
            """{"timest\u0061mp":"2026-09-01T00:00:02Z","\u0074ype":"event_msg","payload":{"\u0074ype":"token_count","info":{"last_token_usage":{"input_tokens":5,"output_tokens":1}}}}"""));

        var entries = Parse(directory);

        Assert.Equal([(100L, 20L), (5L, 1L)], entries.Select(entry => (entry.InputTokens, entry.OutputTokens)));
        Assert.All(entries, entry => Assert.Equal("gpt-5-mini", entry.Model));
    }

    [Fact]
    public void Loads_exec_usage_whose_usage_key_is_spelled_with_escapes()
    {
        using var directory = new TempDirectory();
        directory.Write("run.jsonl",
            """{"type":"turn.completed","timestamp":"2026-01-02T03:04:05Z","model":"gpt-5","us\u0061ge":{"output_tokens":7}}""");

        var entry = Assert.Single(Parse(directory));

        Assert.Equal(("gpt-5", 7L), (entry.Model, entry.OutputTokens));
    }

    [Fact]
    public void Malformed_usage_lines_skip_the_same_whether_or_not_a_marker_is_escaped()
    {
        foreach (var (malformed, reported) in new[]
                 {
                     ("""{"type":"event_msg","payload":{"type":"token_count","info":{"secret":""", 1),
                     ("""{"type":"event_msg","payload":{"type":"token_count","note":"compact\u0065d","info":{"secret":""", 1),
                     ("""{"type":"event_msg","payload":{"type":"token\u005fcount","info":{"secret":""", 0)
                 })
        {
            using var directory = new TempDirectory();
            directory.Write("session.jsonl", Join(malformed, """{"type":"event_msg","payload":{"type":"token_count","info":null}}"""));
            var diagnostics = new ParseDiagnostics();

            Assert.Empty(Parse(directory, diagnostics));
            Assert.Equal(reported, diagnostics.SkippedLines);
        }
    }

    [Fact]
    public void Finds_a_fork_parent_whose_session_meta_is_spelled_with_escapes()
    {
        static string TokenCount(string timestamp, long input) =>
            """{"timestamp":"@0@","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5.2","last_token_usage":{"input_tokens":@1@,"output_tokens":1,"total_tokens":@2@}}}}""".Fill(timestamp, input, input + 1);
        using var directory = new TempDirectory();
        directory.Write("sessions/parent.jsonl", Join(
            """{"timestamp":"2026-11-01T08:00:00Z","type":"session\u005fmeta","payload":{"\u0069d":"parent-xyz"}}""",
            TokenCount("2026-11-01T08:01:00Z", 1_000)));
        directory.Write("sessions/fork.jsonl", Join(
            """{"timestamp":"2026-11-01T08:30:00Z","type":"session\u005fmeta","payload":{"id":"fork-abc","forked\u005ffrom_id":"parent-xyz"}}""",
            TokenCount("2026-11-01T08:30:00Z", 1_000),
            TokenCount("2026-11-01T09:00:00Z", 100)));
        var source = new CodexSource();
        var diagnostics = new ParseDiagnostics();

        var entries = Deduplicator.Apply(TestData.ParseAll(source, TestData.ConfigWith("codex", directory.Path), diagnostics), _ => source);

        Assert.Equal(new Dictionary<string, long> { ["parent"] = 1_000, ["fork"] = 100 },
            entries.GroupBy(entry => entry.SessionId).ToDictionary(group => group.Key, group => group.Sum(entry => entry.InputTokens)));
        Assert.Equal(0, diagnostics.SkippedLines);
    }
}
