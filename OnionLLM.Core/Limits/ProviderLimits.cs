namespace OnionLLM.Core.Limits;

public sealed record ProviderLimits
{
    public bool IsAvailable { get; init; }

    public string Status { get; init; } = "limity niedostępne";

    public double FiveHourUtilization { get; init; }

    public DateTimeOffset? FiveHourReset { get; init; }

    public double SevenDayUtilization { get; init; }

    public DateTimeOffset? SevenDayReset { get; init; }
}
