namespace OnionLLM.Core.Sources;

/// <summary>Wszystkie obsługiwane źródła danych.</summary>
public static class SourceRegistry
{
    private static readonly Lazy<IReadOnlyList<IUsageSource>> Shared = new(CreateAll);

    /// <summary>Wspólne instancje źródeł (bezstanowe) — do reguł cennika i deduplikacji.</summary>
    public static IReadOnlyList<IUsageSource> Default => Shared.Value;

    public static IUsageSource? Find(string id) =>
        Default.FirstOrDefault(source => source.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<IUsageSource> CreateAll() =>
    [
        new ClaudeSource(),
        new CodexSource(),
        new CopilotSource(),
        new GeminiSource(),
        new QwenSource(),
        new OpenCodeSource(),
        new KiloSource(),
        new ClineSource(),
        new AmpSource(),
        new DroidSource(),
        new CodebuffSource(),
        new GooseSource(),
        new KimiSource(),
        new PiSource(),
        new OpenClawSource(),
        new HermesSource(),
        new AntigravitySource(),
        new GrokSource(),
        new ZCodeSource(),
        new DshSource()
    ];
}
