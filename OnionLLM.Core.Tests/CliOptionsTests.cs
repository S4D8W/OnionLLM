using OnionLLM.Cli;

namespace OnionLLM.Core.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Ccusage_short_options_are_recognized()
    {
        var arguments = CliArguments.Parse(["blocks", "-n", "4", "-w", "sunday", "-B", "emoji", "-q", ".totals", "-v", "--by-agent", "--diagnostics", "--no-cache"]);

        Assert.Equal("4", arguments.Get("--session-length"));
        Assert.Equal("sunday", arguments.Get("--start-of-week"));
        Assert.Equal("emoji", arguments.Get("--visual-burn-rate"));
        Assert.Equal(".totals", arguments.Get("--jq"));
        Assert.True(arguments.Has("--version"));
        Assert.True(arguments.Has("--by-agent"));
        Assert.True(arguments.Has("--diagnostics"));
        Assert.True(arguments.Has("--no-cache"));
        Assert.Equal("3", CliArguments.Parse(["blocks", "-l", "3"]).Get("--session-length")); // dawny skrót
    }

    [Fact]
    public void Sections_are_validated_ordered_and_exclude_invoked_command()
    {
        Assert.Equal(["daily", "monthly"], CliApp.ParseSections("monthly, weekly,daily", "weekly"));
        Assert.Empty(CliApp.ParseSections(null, "daily"));
        Assert.Throws<ArgumentException>(() => CliApp.ParseSections("daily,yearly", "daily"));
        Assert.Throws<ArgumentException>(() => CliApp.ParseSections("daily", "blocks"));
    }

    [Fact]
    public void Model_label_aliases_match_exact_names_including_arns()
    {
        var aliases = Statusline.ParseModelLabelAliases(
            "arn:aws:bedrock:ap-northeast-1:012345678910:application-inference-profile/abcde12345=claude-opus-4-6, Sonnet 4.5 = S45");

        Assert.Equal("claude-opus-4-6", aliases["arn:aws:bedrock:ap-northeast-1:012345678910:application-inference-profile/abcde12345"]);
        Assert.Equal("S45", aliases["Sonnet 4.5"]);
        Assert.Empty(Statusline.ParseModelLabelAliases(null));
    }

    [Fact]
    public void Statusline_cache_is_fresh_until_expired_or_transcript_changes()
    {
        var cache = new StatuslineCache { LastOutput = "line", LastUpdateTime = 1_000, TranscriptMtime = 5, OptionsKey = "k" };

        Assert.Equal("line", cache.CachedOutput(5, "k", 1_500, refreshSeconds: 1));
        Assert.Null(cache.CachedOutput(5, "k", 2_000, refreshSeconds: 1));   // przeterminowany
        Assert.Null(cache.CachedOutput(6, "k", 1_500, refreshSeconds: 1));   // zmiana transkryptu
        Assert.Null(cache.CachedOutput(5, "x", 1_500, refreshSeconds: 1));   // inne opcje
        Assert.Null(new StatuslineCache().CachedOutput(0, "", 0, 1));         // brak wyniku
    }

    [Fact]
    public void Statusline_cache_returns_last_output_while_live_process_updates()
    {
        var cache = new StatuslineCache
        {
            LastOutput = "line", LastUpdateTime = 1_000, TranscriptMtime = 5, OptionsKey = "k",
            IsUpdating = true, Pid = 42, UpdatingSince = 9_000
        };

        Assert.Equal("line", cache.CachedOutput(6, "k", 10_000, 1, isAlive: pid => pid == 42));
        Assert.Null(cache.CachedOutput(6, "k", 10_000, 1, isAlive: _ => false));
        // Blokada starsza niż 30 s jest ignorowana.
        Assert.Null(cache.CachedOutput(6, "k", 9_000 + 31_000, 1, isAlive: _ => true));
    }

    [Fact]
    public void Statusline_cache_path_is_per_session_and_safe()
    {
        var a = StatuslineCache.PathFor("abc-123");
        var b = StatuslineCache.PathFor("other");
        var weird = StatuslineCache.PathFor("../..\\x:y");

        Assert.NotEqual(a, b);
        Assert.EndsWith("abc-123.json", a);
        Assert.DoesNotContain("..", Path.GetFileName(weird));
        Assert.Equal(Path.GetDirectoryName(a), Path.GetDirectoryName(weird));
        Assert.EndsWith("_default.json", StatuslineCache.PathFor(""));
    }

    [Theory]
    [InlineData("2026/11/24/rollout-2026-11-24T10-00-00-0199a213-81c0-7800-8aa1-bbab2a035a53.jsonl", 40, "0199a213-81c0-7800-8aa1-bbab2a035a53")]
    [InlineData("session-a", 13, "session-a")]
    [InlineData("0123456789abcdefXYZ", 12, "0123456789ab…")]
    public void Session_ids_are_shortened_for_display(string id, int max, string expected) =>
        Assert.Equal(expected, Format.SessionId(id, max));

    [Fact]
    public void Unknown_model_has_readable_name() => Assert.Equal("nieznany model", Format.Model("unknown"));
}
