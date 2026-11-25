using System.Globalization;
using System.Text.Json.Nodes;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>Zdarzenia Copilota do testów — odpowiedniki pomocników z ccusage <c>copilot/src/loader/tests.rs</c>.</summary>
internal static class CopilotFixture
{
    public const long NanoAiuPerCredit = 1_000_000_000;

    /// <summary>Parsuje katalog i uzgadnia wpisy tak jak <see cref="UsageEngine"/> (bez filtra zakresu dat).</summary>
    public static List<UsageEntry> Load(TempDirectory directory, Func<string?>? exporterPath = null)
    {
        var source = new CopilotSource(exporterPath ?? (() => null));
        var parsed = TestData.ParseAll(source, TestData.ConfigWith("copilot", directory.Path));
        return Deduplicator.Apply(parsed, id => id == CopilotSource.SourceId ? source : null)
            .OrderBy(entry => entry.TimestampMs)
            .ThenBy(entry => entry.Model == "unknown")
            .ToList();
    }

    public static string Checkpoint(string id, string timestamp, long credits, JsonObject? watermarks = null)
    {
        var data = new JsonObject { ["totalNanoAiu"] = credits * NanoAiuPerCredit };
        if (watermarks is not null)
        {
            data["usageAccountingWatermarks"] = watermarks;
        }

        return Event("session.usage_checkpoint", id, timestamp, data);
    }

    public static string Shutdown(string id, string timestamp, long? credits, long? modelCredits, long input = 100, long output = 50, long? requests = null)
    {
        var model = new JsonObject { ["usage"] = new JsonObject { ["inputTokens"] = input, ["outputTokens"] = output } };
        if (requests is not null)
        {
            model["requests"] = new JsonObject { ["count"] = requests };
        }

        if (modelCredits is not null)
        {
            model["totalNanoAiu"] = modelCredits * NanoAiuPerCredit;
        }

        var data = new JsonObject { ["modelMetrics"] = new JsonObject { ["test-model"] = model } };
        if (credits is not null)
        {
            data["totalNanoAiu"] = credits * NanoAiuPerCredit;
        }

        return Event("session.shutdown", id, timestamp, data);
    }

    /// <summary>Shutdown z <paramref name="requests"/> żądaniami po 100 tokenów wejścia i 10 wyjścia (ccusage <c>shutdown_with_requests</c>).</summary>
    public static string ShutdownWithRequests(string id, string timestamp, long requests) =>
        Shutdown(id, timestamp, null, null, requests * 100, requests * 10, requests);

    public static string UsageRecord(string id, string timestamp, JsonObject? usage = null)
    {
        var fields = new JsonObject { ["model"] = "test-model", ["inputTokens"] = 100 };
        foreach (var (name, value) in usage ?? [])
        {
            fields[name] = value?.DeepClone();
        }

        return Event("session.usage_record", id, timestamp, new JsonObject { ["usage"] = fields });
    }

    public static JsonObject Accounting(string source, long sequence) =>
        new() { ["accounting"] = new JsonObject { ["sourceSessionId"] = source, ["sequence"] = sequence } };

    public static string OtelCall(string spanId, string timestamp, string session = "session-1", long input = 1,
        params (string Name, string Value)[] attributes)
    {
        var seconds = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
        var attributeObject = new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "test-model",
            ["gen_ai.conversation.id"] = session,
            ["gen_ai.usage.input_tokens"] = input,
            ["gen_ai.usage.output_tokens"] = 1
        };
        foreach (var (name, value) in attributes)
        {
            attributeObject[name] = value;
        }

        return new JsonObject
        {
            ["type"] = "span",
            ["traceId"] = "trace-1",
            ["spanId"] = spanId,
            ["name"] = "chat test-model",
            ["endTime"] = new JsonArray(seconds, 0),
            ["attributes"] = attributeObject
        }.ToJsonString();
    }

    public static string Lines(params string[] lines) => string.Join('\n', lines);

    public static (string Timestamp, long Input) Row(UsageEntry entry) =>
        (entry.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), entry.InputTokens);

    private static string Event(string type, string id, string timestamp, JsonObject data) =>
        new JsonObject { ["type"] = type, ["id"] = id, ["timestamp"] = timestamp, ["data"] = data }.ToJsonString();
}
