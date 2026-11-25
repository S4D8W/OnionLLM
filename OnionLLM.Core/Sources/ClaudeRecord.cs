using System.Globalization;
using System.Text.Json;
using OnionLLM.Core.Models;

namespace OnionLLM.Core.Sources;

/// <summary>Wynik odczytu rekordu zużycia Claude Code (ccusage <c>daily/read.rs</c> → <c>Refusal</c>).</summary>
internal enum ClaudeOutcome
{
    Admitted,

    /// <summary>Null w czytanym polu — Claude Code tak zapisuje rekordy błędów, odmów i przerwań (<c>usage_wire.rs:92-96</c>).</summary>
    NullContract,

    /// <summary>Rekord niezgodny z formatem (typy pól, nieznana prędkość, wersja nie-semver, puste identyfikatory).</summary>
    Malformed,

    InvalidTimestamp,

    /// <summary>Liczba tokenów poza zakresem.</summary>
    Unrepresentable
}

/// <summary>Zużycie z obiektu <c>usage</c> (lub iteracji advisora).</summary>
internal sealed record ClaudeUsage(
    long Input,
    long Output,
    long CacheCreation,
    long CacheCreation1h,
    long CacheRead,
    bool HasCacheSplit,
    SpeedTier Speed,
    long Reasoning)
{
    public long Total => Input + Output + CacheCreation + CacheRead;
}

internal sealed record ClaudeAdvisor(string Model, ClaudeUsage Usage);

/// <summary>Zdekodowany rekord asystenta: linia główna albo opakowanie AgentProgress (<c>data.message.message</c>).</summary>
internal sealed record ClaudeRecord
{
    public required long TimestampMs { get; init; }
    public required ClaudeUsage Usage { get; init; }
    public string? Model { get; init; }
    public string? MessageId { get; init; }
    public string? RequestId { get; init; }
    public string? SessionId { get; init; }
    public double? CostUsd { get; init; }
    public bool IsSidechain { get; init; }
    public bool IsAgentProgress { get; init; }
    public IReadOnlyList<ClaudeAdvisor> Advisors { get; init; } = [];

    /// <summary>Iteracje advisora, których nie dało się odczytać (diagnostyka; rekord nadrzędny zostaje).</summary>
    public int MalformedAdvisors { get; init; }
}

/// <summary>
/// Odczyt rekordu zgodny z ccusage: <c>daily/record.rs</c> (koperty, kontrakt null, walidacja),
/// <c>usage_wire.rs</c> + <c>usage_wire/iterations.rs</c> (usage, podział cache) i <c>advisor.rs</c> (iteracje advisora).
/// </summary>
internal static class ClaudeRecordReader
{
    private enum Field
    {
        Missing,
        Null,
        Value,
        Invalid
    }

    private sealed class Envelope
    {
        public required string TimestampText;
        public required ClaudeUsage Usage;
        public required JsonElement UsageElement;
        public string? Model;
        public string? MessageId;
        public string? Version;
        public string? SessionId;
        public double? CostUsd;
        public string? RequestId;
        public bool IsSidechain;
        public bool Overflow;
        public string? NullField;
    }

    /// <summary>Czy linia ma obiekt usage w jednym z miejsc czytanych przez ccusage (<c>diagnostics.rs:rejected</c>).</summary>
    public static bool HasUsageObject(JsonElement root) =>
        Get(Get(root, "message"), "usage") is { ValueKind: JsonValueKind.Object } ||
        Get(Get(Get(Get(root, "data"), "message"), "message"), "usage") is { ValueKind: JsonValueKind.Object };

    public static ClaudeOutcome Read(JsonElement root, out ClaudeRecord? record, out string reason)
    {
        record = null;
        reason = "";

        // Najpierw rekord bezpośredni, potem AgentProgress (record.rs:parse_usage_line; #977).
        var envelope = Decode(root, root, wrapped: false, out var directReason);
        var wrapped = false;
        if (envelope is null)
        {
            if (Get(Get(root, "data"), "message") is { ValueKind: JsonValueKind.Object } progress)
            {
                envelope = Decode(root, progress, wrapped: true, out var wrappedReason);
                wrapped = true;
                if (envelope is null)
                {
                    reason = Get(Get(root, "message"), "usage") is { ValueKind: JsonValueKind.Object } ? directReason : wrappedReason;
                    return ClaudeOutcome.Malformed;
                }
            }
            else
            {
                reason = directReason;
                return ClaudeOutcome.Malformed;
            }
        }

        if (envelope.NullField is { } nullField)
        {
            reason = $"null w polu {nullField} (rekord błędu/przerwania Claude Code)";
            return ClaudeOutcome.NullContract;
        }

        if (!DateTimeOffset.TryParse(envelope.TimestampText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            reason = "brak lub zły timestamp";
            return ClaudeOutcome.InvalidTimestamp;
        }

        // record.rs:is_valid_usage_record.
        var invalid = envelope switch
        {
            { Version: { } version } when !IsSemverPrefix(version) => "wersja Claude Code nie jest semver",
            { SessionId: "" } => "pusty sessionId",
            { RequestId: "" } => "pusty requestId",
            { MessageId: "" } => "pusty message.id",
            { Model: "" } => "pusty message.model",
            _ => null
        };
        if (invalid is not null)
        {
            reason = invalid;
            return ClaudeOutcome.Malformed;
        }

        if (envelope.Overflow || !FitsTotal(envelope.Usage))
        {
            reason = "liczba tokenów poza zakresem";
            return ClaudeOutcome.Unrepresentable;
        }

        var advisors = ReadAdvisors(envelope.UsageElement, out var malformedAdvisors);
        record = new ClaudeRecord
        {
            TimestampMs = timestamp.ToUnixTimeMilliseconds(),
            Usage = envelope.Usage,
            Model = envelope.Model,
            MessageId = envelope.MessageId,
            RequestId = envelope.RequestId,
            SessionId = envelope.SessionId,
            CostUsd = envelope.CostUsd,
            IsSidechain = envelope.IsSidechain,
            IsAgentProgress = wrapped,
            Advisors = advisors,
            MalformedAdvisors = malformedAdvisors
        };
        return ClaudeOutcome.Admitted;
    }

    /// <summary>
    /// Pola koperty. <paramref name="source"/> to korzeń (rekord bezpośredni) albo <c>data.message</c> (AgentProgress,
    /// bez <c>version</c>; <c>sessionId</c> zawsze z korzenia).
    /// </summary>
    private static Envelope? Decode(JsonElement root, JsonElement source, bool wrapped, out string reason)
    {
        reason = "";
        if (Get(source, "timestamp") is not { ValueKind: JsonValueKind.String } timestamp)
        {
            reason = "brak lub zły timestamp";
            return null;
        }

        if (Get(source, "message") is not { ValueKind: JsonValueKind.Object } message ||
            Get(message, "usage") is not { ValueKind: JsonValueKind.Object } usageElement)
        {
            reason = "brak obiektu message.usage";
            return null;
        }

        var usage = ReadUsage(usageElement, out var usageReason, out var usageNull, out var overflow);
        if (usage is null)
        {
            reason = usageReason;
            return null;
        }

        string? nullField = usageNull is null ? null : "usage." + usageNull;
        var why = "";

        bool Text(JsonElement owner, string name, string label, out string? value)
        {
            switch (ReadString(owner, name, out value))
            {
                case Field.Invalid:
                    why = $"niepoprawny typ pola {label}";
                    return false;
                case Field.Null:
                    nullField ??= label;
                    break;
            }

            return true;
        }

        bool Flag(JsonElement owner, string name, string label, bool nullable, out bool value)
        {
            value = false;
            switch (Get(owner, name))
            {
                case null:
                    return true;
                case { ValueKind: JsonValueKind.True }:
                    value = true;
                    return true;
                case { ValueKind: JsonValueKind.False }:
                    return true;
                case { ValueKind: JsonValueKind.Null }:
                    // isSidechain: Option<bool> — null jak brak; isApiErrorMessage: Nullable — null odrzuca rekord.
                    if (!nullable)
                    {
                        nullField ??= label;
                    }

                    return true;
                default:
                    why = $"niepoprawny typ pola {label}";
                    return false;
            }
        }

        if (!Text(message, "model", "message.model", out var model) ||
            !Text(message, "id", "message.id", out var messageId) ||
            !Text(root, "sessionId", "sessionId", out var sessionId) ||
            !Text(source, "requestId", "requestId", out var requestId) ||
            !Flag(source, "isSidechain", "isSidechain", nullable: true, out var isSidechain) ||
            !Flag(source, "isApiErrorMessage", "isApiErrorMessage", nullable: false, out _))
        {
            reason = why;
            return null;
        }

        string? version = null;
        if (!wrapped && !Text(source, "version", "version", out version))
        {
            reason = why;
            return null;
        }

        double? cost = null;
        switch (Get(source, "costUSD"))
        {
            case null:
                break;
            case { ValueKind: JsonValueKind.Null }:
                nullField ??= "costUSD";
                break;
            case { ValueKind: JsonValueKind.Number } number when number.TryGetDouble(out var value):
                cost = value;
                break;
            default:
                reason = "niepoprawny typ pola costUSD";
                return null;
        }

        return new Envelope
        {
            TimestampText = timestamp.GetString()!,
            Usage = usage,
            UsageElement = usageElement,
            Model = model,
            MessageId = messageId,
            Version = version,
            SessionId = sessionId,
            CostUsd = cost,
            RequestId = requestId,
            IsSidechain = isSidechain,
            Overflow = overflow,
            NullField = nullField
        };
    }

    /// <summary>
    /// Obiekt usage (<c>usage_wire.rs:TokenUsageRawJson</c>). Null w <c>speed</c>, <c>cache_creation_input_tokens</c>
    /// lub <c>cache_read_input_tokens</c> ustawia <paramref name="nullField"/> (kontrakt null), inne błędy dają null.
    /// </summary>
    internal static ClaudeUsage? ReadUsage(JsonElement usage, out string reason, out string? nullField, out bool overflow)
    {
        reason = "";
        nullField = null;
        overflow = false;

        if (ReadCount(Get(usage, "input_tokens"), ref overflow) is not { } input ||
            ReadCount(Get(usage, "output_tokens"), ref overflow) is not { } output)
        {
            reason = "usage bez poprawnych input_tokens/output_tokens";
            return null;
        }

        long Guarded(string name, ref string? nullName, ref bool overflowed, ref string why)
        {
            switch (Get(usage, name))
            {
                case null:
                    return 0;
                case { ValueKind: JsonValueKind.Null }:
                    nullName ??= name;
                    return 0;
                case var value:
                    if (ReadCount(value, ref overflowed) is { } count)
                    {
                        return count;
                    }

                    why = $"niepoprawny typ pola usage.{name}";
                    return -1;
            }
        }

        var cacheCreation = Guarded("cache_creation_input_tokens", ref nullField, ref overflow, ref reason);
        var cacheRead = Guarded("cache_read_input_tokens", ref nullField, ref overflow, ref reason);
        if (cacheCreation < 0 || cacheRead < 0)
        {
            return null;
        }

        var speed = SpeedTier.Unknown;
        switch (Get(usage, "speed"))
        {
            case null:
                break;
            case { ValueKind: JsonValueKind.Null }:
                nullField ??= "speed";
                break;
            case { ValueKind: JsonValueKind.String } text when text.ValueEquals("standard"):
                speed = SpeedTier.Standard;
                break;
            case { ValueKind: JsonValueKind.String } text when text.ValueEquals("fast"):
                speed = SpeedTier.Fast;
                break;
            default:
                reason = "nieznana prędkość (usage.speed)";
                return null;
        }

        // Podział górnego poziomu jest ścisły; podział z iteracji „message” — pobłażliwy (iterations.rs).
        (long FiveMinutes, long OneHour)? split = null;
        switch (Get(usage, "cache_creation"))
        {
            case null or { ValueKind: JsonValueKind.Null }:
                break;
            case { ValueKind: JsonValueKind.Object } details:
                long five = 0, one = 0;
                foreach (var property in details.EnumerateObject())
                {
                    if (property.NameEquals("ephemeral_5m_input_tokens") || property.NameEquals("ephemeral_1h_input_tokens"))
                    {
                        if (ReadCount(property.Value, ref overflow) is not { } count)
                        {
                            reason = "niepoprawny podział usage.cache_creation";
                            return null;
                        }

                        if (property.NameEquals("ephemeral_5m_input_tokens"))
                        {
                            five = count;
                        }
                        else
                        {
                            one = count;
                        }
                    }
                }

                split = (five, one);
                break;
            default:
                reason = "niepoprawny podział usage.cache_creation";
                return null;
        }

        // NOTE(ccusage #2085): w turach z advisorem podział górnego poziomu obejmuje tylko pierwszą iterację „message”,
        // a suma — wszystkie. Gdy każda iteracja „message” ma podział, liczy się ich suma (usage_wire.rs:74-85).
        split = MessageIterationSplit(Get(usage, "iterations"), ref overflow) ?? split;

        var cacheTotal = cacheCreation;
        long cache1h = 0;
        if (split is { } parts)
        {
            cacheTotal = Add(parts.FiveMinutes, parts.OneHour, ref overflow);
            cache1h = parts.OneHour;
        }

        var reasoning = Get(Get(usage, "output_tokens_details"), "thinking_tokens") is { ValueKind: JsonValueKind.Number } thinking &&
                        thinking.TryGetInt64(out var thinkingTokens) && thinkingTokens > 0
            ? thinkingTokens
            : 0;

        return new ClaudeUsage(input, output, cacheTotal, Math.Min(cache1h, cacheTotal), cacheRead, split is not null, speed, reasoning);
    }

    /// <summary>Suma podziałów cache z iteracji „message”, gdy każda go ma (iterations.rs:Iterations::from_seq).</summary>
    private static (long, long)? MessageIterationSplit(JsonElement? iterations, ref bool overflow)
    {
        if (iterations is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        long five = 0, one = 0;
        var hasMessage = false;
        var everyMessageHasSplit = true;
        foreach (var iteration in array.EnumerateArray())
        {
            if (iteration.ValueKind != JsonValueKind.Object ||
                Get(iteration, "type") is not { ValueKind: JsonValueKind.String } type || !type.ValueEquals("message"))
            {
                continue;
            }

            hasMessage = true;
            if (LenientSplit(Get(iteration, "cache_creation")) is { } split)
            {
                five = Add(five, split.Item1, ref overflow);
                one = Add(one, split.Item2, ref overflow);
            }
            else
            {
                everyMessageHasSplit = false;
            }
        }

        return hasMessage && everyMessageHasSplit ? (five, one) : null;
    }

    private static (long, long)? LenientSplit(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } details)
        {
            return null;
        }

        long? five = 0, one = 0;
        foreach (var property in details.EnumerateObject())
        {
            var ignored = false;
            if (property.NameEquals("ephemeral_5m_input_tokens"))
            {
                five = ReadCount(property.Value, ref ignored);
            }
            else if (property.NameEquals("ephemeral_1h_input_tokens"))
            {
                one = ReadCount(property.Value, ref ignored);
            }
        }

        return five is { } fiveMinutes && one is { } oneHour ? (fiveMinutes, oneHour) : null;
    }

    /// <summary>
    /// Iteracje <c>advisor_message</c> z własnym modelem (advisor.rs:advisor_usages_from_envelope, #1423, #1719).
    /// Iteracja bez modelu lub innego typu jest pomijana; null w czytanym polu — po cichu; inny błąd — diagnostyka.
    /// </summary>
    private static IReadOnlyList<ClaudeAdvisor> ReadAdvisors(JsonElement usage, out int malformed)
    {
        malformed = 0;
        if (Get(usage, "iterations") is not { ValueKind: JsonValueKind.Array } iterations)
        {
            return [];
        }

        List<ClaudeAdvisor>? advisors = null;
        foreach (var iteration in iterations.EnumerateArray())
        {
            if (iteration.ValueKind != JsonValueKind.Object ||
                Get(iteration, "model") is not { ValueKind: JsonValueKind.String } modelElement ||
                modelElement.GetString() is not { Length: > 0 } model ||
                Get(iteration, "type") is not { ValueKind: JsonValueKind.String } type || !type.ValueEquals("advisor_message"))
            {
                continue;
            }

            var usageValue = ReadUsage(iteration, out _, out var nullField, out var overflow);
            if (usageValue is null || overflow || !FitsTotal(usageValue))
            {
                malformed++;
                continue;
            }

            if (nullField is not null)
            {
                continue;
            }

            (advisors ??= []).Add(new ClaudeAdvisor(model, usageValue));
        }

        return advisors ?? (IReadOnlyList<ClaudeAdvisor>)[];
    }

    /// <summary>Liczba tokenów: nieujemna liczba całkowita JSON (serde <c>u64</c>); null = niepoprawna.</summary>
    private static long? ReadCount(JsonElement? element, ref bool overflow)
    {
        if (element is not { ValueKind: JsonValueKind.Number } number)
        {
            return null;
        }

        if (number.TryGetInt64(out var value))
        {
            return value >= 0 ? value : null;
        }

        if (number.TryGetUInt64(out _))
        {
            overflow = true;
            return 0;
        }

        return null;
    }

    private static long Add(long left, long right, ref bool overflow)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            overflow = true;
            return long.MaxValue;
        }
    }

    private static bool FitsTotal(ClaudeUsage usage)
    {
        try
        {
            _ = checked(usage.Input + usage.Output + usage.CacheCreation + usage.CacheRead);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static Field ReadString(JsonElement owner, string name, out string? value)
    {
        value = null;
        switch (Get(owner, name))
        {
            case null:
                return Field.Missing;
            case { ValueKind: JsonValueKind.Null }:
                return Field.Null;
            case { ValueKind: JsonValueKind.String } text:
                value = text.GetString();
                return Field.Value;
            default:
                return Field.Invalid;
        }
    }

    /// <summary>Prefiks semver <c>X.Y.Z</c> (record.rs:is_semver_prefix).</summary>
    internal static bool IsSemverPrefix(string value)
    {
        var index = 0;
        if (!Digits(value, ref index) || index >= value.Length || value[index] != '.')
        {
            return false;
        }

        index++;
        if (!Digits(value, ref index) || index >= value.Length || value[index] != '.')
        {
            return false;
        }

        index++;
        return index < value.Length && char.IsAsciiDigit(value[index]);
    }

    private static bool Digits(string value, ref int index)
    {
        var start = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            index++;
        }

        return index > start;
    }

    internal static JsonElement? Get(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;
}
