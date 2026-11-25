using System.Net.Http.Headers;
using System.Text.Json;


namespace OnionLLM.Core.Limits;

public sealed class ProviderLimitService : IDisposable
{
    private readonly HttpClient _httpClient = new();

    public async Task<IReadOnlyDictionary<string, ProviderLimits>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ProviderLimits>(StringComparer.OrdinalIgnoreCase);
        var credentialsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json");

        if (!File.Exists(credentialsPath))
        {
            return result;
        }

        await using var credentialsStream = new FileStream(
            credentialsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var credentials = await JsonDocument.ParseAsync(credentialsStream, cancellationToken: cancellationToken);

        if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
            !oauth.TryGetProperty("accessToken", out var accessTokenElement) ||
            accessTokenElement.GetString() is not { Length: > 0 } accessToken)
        {
            return result;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("OnionLLM/1.0");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            result["claude"] = new ProviderLimits { Status = "limit online chwilowo niedostępny" };
            return result;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                result["claude"] = new ProviderLimits { Status = "odśwież logowanie Claude" };
                return result;
            }

            try
            {
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var usage = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
                var limits = new ProviderLimits
                {
                    IsAvailable = true,
                    Status = "live",
                    FiveHourUtilization = ReadUtilization(usage.RootElement, "five_hour"),
                    FiveHourReset = ReadReset(usage.RootElement, "five_hour"),
                    SevenDayUtilization = ReadUtilization(usage.RootElement, "seven_day"),
                    SevenDayReset = ReadReset(usage.RootElement, "seven_day")
                };
                result["claude"] = limits;
                return result;
            }
            catch (JsonException)
            {
                result["claude"] = new ProviderLimits { Status = "nieznany format limitów" };
                return result;
            }
        }
    }

    private static double ReadUtilization(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            !window.TryGetProperty("utilization", out var utilization) ||
            !utilization.TryGetDouble(out var value))
        {
            return 0;
        }

        return Math.Clamp(value > 1 ? value / 100 : value, 0, 1);
    }

    private static DateTimeOffset? ReadReset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            !window.TryGetProperty("resets_at", out var reset) ||
            !reset.TryGetDateTimeOffset(out var value))
        {
            return null;
        }

        return value;
    }

    public void Dispose() => _httpClient.Dispose();
}
