using System.Globalization;
using OnionLLM.Core.Models;

namespace OnionLLM.Core.Sources;

/// <summary>Rodzaj wpisu Copilota rozpoznawany po <see cref="UsageEntry.DedupeKey"/>.</summary>
internal enum CopilotEntryKind
{
    /// <summary>Wiersz <c>session-store.db</c> lub wpis spoza parsera — bez uzgadniania.</summary>
    Other,
    Otel,
    UsageRecord,
    ShutdownInterval,
    CreditGap
}

/// <summary>
/// Dane wpisu Copilota: tożsamość w <see cref="UsageEntry.DedupeKey"/> (z prefiksem rodzaju) oraz pola pomocnicze potrzebne
/// przy uzgadnianiu między plikami (identyfikatory wywołania, liczba żądań, surowa nazwa modelu) w <see cref="UsageEntry.SourceData"/>.
/// Kopie o tej samej tożsamości, ale różnych polach, uzgadnia <see cref="CopilotSource.Reconcile"/>.
/// </summary>
internal static class CopilotKey
{
    private const char Separator = '\u001f';

    /// <summary>Surowa nazwa modelu z logu, gdy różni się od znormalizowanej.</summary>
    public const string RawModel = "raw";

    /// <summary><c>gen_ai.response.id</c> (OTel) / <c>apiCallId</c> (usage_record).</summary>
    public const string ResponseId = "resp";

    /// <summary><c>github.copilot.service_request_id</c> (OTel) / <c>serviceRequestId</c> (usage_record).</summary>
    public const string ServiceRequestId = "sr";

    /// <summary><c>accounting.sourceSessionId</c> rekordu usage_record.</summary>
    public const string SourceSession = "src";

    /// <summary>Liczba żądań w przedziale shutdown.</summary>
    public const string Requests = "req";

    /// <summary>Flaga: któraś migawka shutdown pary (sesja, model) nie podaje liczby żądań.</summary>
    public const string NoRequestCounts = "norq";

    /// <summary>Rodzaj rekordu OTel (<c>OtelKind</c> jako liczba).</summary>
    public const string OtelKind = "kind";

    /// <summary>Trace id rekordu OTel.</summary>
    public const string TraceId = "tr";

    /// <summary>Flaga: rekord OTel nie podaje własnego modelu (model z kontekstu trace).</summary>
    public const string NoOwnModel = "nom";

    /// <summary>Flaga: rekord OTel nie podaje własnej sesji (sesja z kontekstu trace).</summary>
    public const string NoOwnSession = "nos";

    /// <summary>Model z kontekstu trace w pliku rekordu (pierwszy niepusty, surowa nazwa).</summary>
    public const string TraceModel = "tm";

    /// <summary>Sesja z kontekstu trace w pliku rekordu (najwyższy priorytet atrybutu).</summary>
    public const string TraceSession = "ts";

    /// <summary>Priorytet <see cref="TraceSession"/> (1–3).</summary>
    public const string TraceSessionPriority = "tp";

    /// <summary>Reszta z <c>total_tokens</c> rekordu bez <c>output_tokens</c> — wyjście „zastępcze” (ccusage <c>fallback_output_tokens</c>).</summary>
    public const string FallbackOutput = "fb";

    /// <summary>Reszta z <c>total_tokens</c> rekordu z <c>output_tokens</c> (ccusage <c>extra_total_tokens</c>).</summary>
    public const string ExtraTotal = "x";

    public const string OtelPrefix = "otel:";
    public const string UsageRecordPrefix = "usage-record:";
    public const string ShutdownPrefix = "shutdown:";
    public const string CreditsPrefix = "credits:";

    /// <summary>Wartość <see cref="UsageEntry.SourceData"/> z pól niebędących null; null, gdy żadnego nie ma.</summary>
    public static string? Data(params (string Name, string? Value)[] fields)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var (name, value) in fields)
        {
            if (value is not null)
            {
                builder.Append(Separator).Append(name).Append('=').Append(value);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    public static string? Field(UsageEntry entry, string name)
    {
        if (entry.SourceData is not { } key)
        {
            return null;
        }

        var prefix = Separator + name + "=";
        var start = key.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = key.IndexOf(Separator, start);
        return end < 0 ? key[start..] : key[start..end];
    }

    public static long? LongField(UsageEntry entry, string name) =>
        long.TryParse(Field(entry, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Rodzaj wpisu; wiersze bazy (poziom 0) i obce klucze nie podlegają uzgadnianiu.</summary>
    public static CopilotEntryKind KindOf(UsageEntry entry)
    {
        if (entry.SessionTier == 0 || entry.DedupeKey is not { } key)
        {
            return CopilotEntryKind.Other;
        }

        return key.StartsWith(OtelPrefix, StringComparison.Ordinal) ? CopilotEntryKind.Otel
            : key.StartsWith(UsageRecordPrefix, StringComparison.Ordinal) ? CopilotEntryKind.UsageRecord
            : key.StartsWith(ShutdownPrefix, StringComparison.Ordinal) ? CopilotEntryKind.ShutdownInterval
            : key.StartsWith(CreditsPrefix, StringComparison.Ordinal) ? CopilotEntryKind.CreditGap
            : CopilotEntryKind.Other;
    }
}
