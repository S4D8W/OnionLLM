using System.Text.Json;
using OnionLLM.Cli;
using OnionLLM.Core.Config;

namespace OnionLLM.Core.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

public class CliArgumentsTests
{
    [Fact]
    public void Parses_positionals_aliases_inline_values_and_flags()
    {
        var arguments = CliArguments.Parse(["claude", "daily", "-s", "20260101", "--until=2026-01-31", "-j", "-b", "--last", "3"]);

        Assert.Equal(["claude", "daily"], arguments.Positionals);
        Assert.Equal("20260101", arguments.Get("--since"));
        Assert.Equal("2026-01-31", arguments.Get("--until"));
        Assert.True(arguments.Has("--json"));
        Assert.True(arguments.Has("--breakdown"));
        Assert.Equal(3, arguments.GetInt("--last"));
    }

    [Fact]
    public void Short_i_means_id_for_session_and_instances_otherwise()
    {
        Assert.Equal("abc", CliArguments.Parse(["session", "-i", "abc"]).Get("--id"));
        Assert.True(CliArguments.Parse(["daily", "-i"]).Has("--instances"));
    }

    [Fact]
    public void Missing_value_and_bad_numbers_are_errors()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["daily", "--since"]));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["daily", "--last", "x"]).GetInt("--last"));
    }
}

[Collection("Console")]
public sealed class CliEndToEndTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string? _previousDataDir = Environment.GetEnvironmentVariable("ONIONLLM_DATA_DIR");
    private readonly string _config;

    public CliEndToEndTests()
    {
        Environment.SetEnvironmentVariable("ONIONLLM_DATA_DIR", Path.Combine(_directory.Path, "data"));
        _directory.Write("claude/projects/demo/session-a.jsonl", string.Join('\n',
            TestData.ClaudeLine("2026-11-20T10:00:00Z", "m1", "r1", 1000, 2000, 0, 0, model: "claude-sonnet-4-5", session: "session-a"),
            TestData.ClaudeLine("2026-11-20T10:00:00Z", "m1", "r1", 1000, 2000, 0, 0, model: "claude-sonnet-4-5", session: "session-a"),
            TestData.ClaudeLine("2026-11-21T10:00:00Z", "m2", "r2", 500, 100, 0, 0, model: "claude-sonnet-4-5", session: "session-a")));
        var claudeRoot = Path.Combine(_directory.Path, "claude").Replace("\\", "\\\\");
        var disabled = string.Concat(Core.Sources.SourceRegistry.CreateAll()
            .Where(source => source.Id != "claude")
            .Select(source => $",\"{source.Id}\":{{\"enabled\":false}}"));
        _config = _directory.Write("config.json",
            $"{{\"timezone\":\"UTC\",\"offline\":true,\"costMode\":\"calculate\",\"sources\":{{\"claude\":{{\"paths\":[\"{claudeRoot}\"]}}{disabled}}}}}");
    }

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var code = await CliApp.RunAsync([.. args, "--config", _config]);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task Daily_json_contains_deduplicated_rows_and_totals()
    {
        var (code, output) = await Run("claude", "daily", "--json", "--since", "20261101");

        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(output);
        var daily = json.RootElement.GetProperty("daily");
        Assert.Equal(2, daily.GetArrayLength());
        Assert.Equal("2026-11-20", daily[0].GetProperty("date").GetString());
        Assert.Equal(1000, daily[0].GetProperty("inputTokens").GetInt64()); // duplikat policzony raz
        Assert.Equal(3600, json.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        // claude-sonnet-4-5: 1500*3 + 2100*15 µ$ = 0.036
        Assert.Equal(0.036, json.RootElement.GetProperty("totals").GetProperty("totalCost").GetDouble(), 6);
    }

    [Fact]
    public async Task Csv_and_session_and_monthly_outputs()
    {
        var csv = await Run("monthly", "--csv");
        Assert.StartsWith("period,project,inputTokens", csv.Output);
        Assert.Contains("2026-11", csv.Output);

        var sessions = await Run("session", "--json");
        using var json = JsonDocument.Parse(sessions.Output);
        // Raport zbiorczy ccusage: tablica "session", period = ID sesji, agent = źródło.
        var session = json.RootElement.GetProperty("session")[0];
        Assert.Equal("session-a", session.GetProperty("period").GetString());
        Assert.Equal("claude", session.GetProperty("agent").GetString());
        Assert.EndsWith("Z", session.GetProperty("metadata").GetProperty("lastActivity").GetString());

        var focused = await Run("claude", "session", "--json");
        using var focusedJson = JsonDocument.Parse(focused.Output);
        Assert.Equal("session-a", focusedJson.RootElement.GetProperty("sessions")[0].GetProperty("sessionId").GetString());

        var missing = await Run("session", "--id", "nope");
        Assert.Equal(1, missing.Code);
    }

    [Fact]
    public async Task Table_output_and_unknown_source_error()
    {
        var table = await Run("daily", "--no-cost");
        Assert.Contains("2026-11-21", table.Output);
        Assert.DoesNotContain("Koszt", table.Output);

        await Assert.ThrowsAsync<ArgumentException>(() => Run("nosuchsource", "daily"));
        await Assert.ThrowsAsync<ArgumentException>(() => Run("daily", "--since", "2026-02-30"));
    }

    [Fact]
    public async Task Statusline_prints_model_costs_and_context()
    {
        Statusline.InputOverride = new StringReader("""
            {"session_id":"session-a","model":{"id":"claude-sonnet-4-5","display_name":"Sonnet 4.5"},
             "cost":{"total_cost_usd":1.5},"effort":{"level":"high"},
             "context_window":{"total_input_tokens":50000,"context_window_size":200000}}
            """);
        try
        {
            var (code, output) = await Run("statusline", "--cost-source", "both");

            Assert.Equal(0, code);
            Assert.Contains("🤖 Sonnet 4.5 (high)", output);
            Assert.Contains("$1.50 cc / $0.04 onion", output);
            Assert.Contains("🧠 50,000 (25%)", output);
        }
        finally
        {
            Statusline.InputOverride = null;
        }
    }

    private const string HookWithoutCost = """
        {"session_id":"session-a","model":{"id":"claude-sonnet-4-5","display_name":"Sonnet 4.5"},
         "context_window":{"total_input_tokens":50000,"context_window_size":200000}}
        """;

    private async Task<(int Code, string Output)> RunStatusline(string input, params string[] args)
    {
        Statusline.InputOverride = new StringReader(input);
        try
        {
            return await Run(["statusline", .. args]);
        }
        finally
        {
            Statusline.InputOverride = null;
        }
    }

    [Fact]
    public async Task Statusline_shows_NA_without_claude_code_cost_and_applies_label_aliases()
    {
        var (code, output) = await RunStatusline(HookWithoutCost, "--cost-source", "cc", "--no-cache", "--model-label-aliases", "Sonnet 4.5=S45");

        Assert.Equal(0, code);
        Assert.Contains("💰 N/A sesja", output);
        Assert.Contains("🤖 S45", output);

        var both = await RunStatusline(HookWithoutCost, "--cost-source", "both", "--no-cache");
        Assert.Contains("(N/A cc / $0.04 onion)", both.Output);
    }

    [Fact]
    public async Task Statusline_uses_per_session_cache_and_falls_back_to_last_output_on_error()
    {
        var first = await RunStatusline(HookWithoutCost);
        Assert.Equal(0, first.Code);
        var cachePath = StatuslineCache.PathFor("session-a");
        Assert.True(File.Exists(cachePath));
        Assert.False(StatuslineCache.Read(cachePath)!.IsUpdating);

        // Wynik z pamięci (świeży) nie wymaga przeliczenia — nawet gdy przeliczenie by się nie udało.
        Statusline.FailureOverride = () => new InvalidOperationException("test");
        try
        {
            var cached = await RunStatusline(HookWithoutCost, "--refresh-interval", "3600");
            Assert.Equal(0, cached.Code);
            Assert.Equal(first.Output, cached.Output);

            // Wynik przeterminowany + błąd → ostatni wynik, blokada zwolniona.
            var failed = await RunStatusline(HookWithoutCost, "--refresh-interval", "0");
            Assert.Equal(1, failed.Code);
            Assert.Equal(first.Output, failed.Output);
            Assert.False(StatuslineCache.Read(cachePath)!.IsUpdating);

            // Bez pamięci podręcznej po błędzie zostaje komunikat błędu.
            var noCache = await RunStatusline(HookWithoutCost, "--no-cache");
            Assert.Equal(1, noCache.Code);
            Assert.Contains(Statusline.ErrorText, noCache.Output);
        }
        finally
        {
            Statusline.FailureOverride = null;
        }
    }

    [Fact]
    public async Task Debug_prints_pricing_mismatch_report_and_diagnostics_flag_is_separate()
    {
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var (code, _) = await Run("daily", "--json", "--debug", "--debug-samples", "2");
            Assert.Equal(0, code);
            Assert.Contains("Rozbieżności kosztu", error.ToString());
            Assert.DoesNotContain("pominiętych linii", error.ToString());

            await Run("daily", "--json", "--diagnostics");
            Assert.Contains("pominiętych linii", error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task Sections_and_by_agent_json()
    {
        var (code, output) = await Run("daily", "--json", "--sections", "session,monthly", "--by-agent");

        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(output);
        var keys = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["daily", "monthly", "session", "totals"], keys);
        var row = json.RootElement.GetProperty("daily")[0];
        Assert.Equal("all", row.GetProperty("agent").GetString());
        Assert.Equal("claude", row.GetProperty("agents")[0].GetProperty("agent").GetString());
        Assert.Equal(["claude"], row.GetProperty("metadata").GetProperty("agents").EnumerateArray().Select(item => item.GetString()));

        await Assert.ThrowsAsync<ArgumentException>(() => Run("daily", "--sections", "yearly"));
        await Assert.ThrowsAsync<ArgumentException>(() => Run("daily", "--sections", "weekly", "--last", "2"));
    }

    [Fact]
    public async Task Source_statusline_summarizes_today_range_and_session()
    {
        var config = ConfigLoader.Load(_config);
        using var engine = new UsageEngine(config);
        await engine.SyncAsync(force: true);
        var now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");

        var today = SourceStatusline.Summarize(engine, CliArguments.Parse(["claude", "statusline"]), "claude", now)!;
        Assert.Equal("today", today.Scope);
        Assert.Equal(600, today.Totals.TotalTokens);

        var range = SourceStatusline.Summarize(engine, CliArguments.Parse(["statusline", "--since", "20261101"]), "claude", now)!;
        Assert.Equal("range", range.Scope);
        Assert.Equal(3600, range.Totals.TotalTokens);

        var session = SourceStatusline.Summarize(engine, CliArguments.Parse(["statusline", "--id", "session-a", "--since", "20261121"]), "claude", now)!;
        Assert.Equal("session", session.Scope);
        Assert.Equal(3600, session.Totals.TotalTokens); // --id ignoruje filtry dat

        var json = SourceStatusline.ToJson(session, includeCost: true);
        Assert.Equal("session-a", json["sessionId"]!.GetValue<string>());
        Assert.True(json.ContainsKey("costUSD"));
        Assert.False(SourceStatusline.ToJson(session, includeCost: false).ContainsKey("costUSD"));
        Assert.StartsWith("Claude", SourceStatusline.FormatLine(today, "Claude Code", includeCost: true));

        Assert.Null(SourceStatusline.Summarize(engine, CliArguments.Parse(["statusline", "--id", "nope"]), "claude", now));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ONIONLLM_DATA_DIR", _previousDataDir);
        _directory.Dispose();
    }
}
