using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsage;

/// <summary>
/// Usage snapshot from Claude API
/// </summary>
public record UsageSnapshot(
    double SessionLeftPercent,
    double WeeklyLeftPercent,
    DateTime SessionResetsAt,
    DateTime WeeklyResetsAt,
    List<LimitInfo> Limits,
    string SubscriptionType,
    DateTime FetchedAt);

public record LimitInfo(
    string Kind,
    double PercentUsed,
    DateTime ResetsAt,
    string? ModelDisplayName = null);

/// <summary>
/// Fetches usage data from Claude API with caching and background refresh
/// </summary>
public class UsageClient
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _credentialsPath;
    private UsageSnapshot? _cachedSnapshot;
    private DateTime _lastFetch = DateTime.MinValue;
    private Timer? _refreshTimer;

    private const string ApiUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ApiVersion = "oauth-2025-04-20";
    private const string UserAgent = "claude-code/2.1.206";
    private const int CacheMaxAgeSeconds = 60;
    private const int BackgroundRefreshSeconds = 300; // 5 minutes

    public UsageClient()
    {
        _credentialsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json");
        StartBackgroundRefresh();
    }

    /// <summary>
    /// Get usage snapshot, using cache if fresher than CacheMaxAgeSeconds
    /// </summary>
    public async Task<UsageSnapshot?> GetAsync(bool force = false)
    {
        if (!force && _cachedSnapshot != null && (DateTime.UtcNow - _cachedSnapshot.FetchedAt).TotalSeconds < CacheMaxAgeSeconds)
        {
            return _cachedSnapshot;
        }

        return await FetchAsync();
    }

    private async Task<UsageSnapshot?> FetchAsync()
    {
        try
        {
            var token = ReadToken();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var headers = new Dictionary<string, string>
            {
                { "Authorization", $"Bearer {token}" },
                { "anthropic-beta", ApiVersion },
                { "User-Agent", UserAgent },
                { "Content-Type", "application/json" }
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            foreach (var (key, value) in headers)
            {
                request.Headers.Add(key, value);
            }

            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var sessionUtil = GetDouble(root, "five_hour.utilization") ?? 0;
            var weeklyUtil = GetDouble(root, "seven_day.utilization") ?? 0;
            var sessionResets = ParseDateTime(GetString(root, "five_hour.resets_at"));
            var weeklyResets = ParseDateTime(GetString(root, "seven_day.resets_at"));

            var limits = new List<LimitInfo>();
            if (root.TryGetProperty("limits", out var limitsArray) && limitsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limitsArray.EnumerateArray())
                {
                    var kind = GetString(limit, "kind") ?? "unknown";
                    var percent = GetDouble(limit, "percent") ?? 0;
                    var resetsAt = ParseDateTime(GetString(limit, "resets_at"));
                    var modelName = GetString(limit, "scope.model.display_name");

                    limits.Add(new LimitInfo(kind, percent, resetsAt, modelName));
                }
            }

            var subType = GetString(root, "subscription_type") ?? "unknown";

            _cachedSnapshot = new UsageSnapshot(
                SessionLeftPercent: 100 - sessionUtil,
                WeeklyLeftPercent: 100 - weeklyUtil,
                SessionResetsAt: sessionResets,
                WeeklyResetsAt: weeklyResets,
                Limits: limits,
                SubscriptionType: subType,
                FetchedAt: DateTime.UtcNow);

            _lastFetch = DateTime.UtcNow;
            return _cachedSnapshot;
        }
        catch
        {
            return null;
        }
    }

    private string? ReadToken()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return null;
            }

            var json = File.ReadAllText(_credentialsPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var token)
                ? token.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string? GetString(JsonElement element, string path)
    {
        var parts = path.Split('.');
        var current = element;
        foreach (var part in parts)
        {
            if (!current.TryGetProperty(part, out var next))
            {
                return null;
            }
            current = next;
        }
        return current.GetString();
    }

    private double? GetDouble(JsonElement element, string path)
    {
        var str = GetString(element, path);
        if (string.IsNullOrEmpty(str) || !double.TryParse(str, out var value))
        {
            return null;
        }
        return value;
    }

    private DateTime ParseDateTime(string? isoString)
    {
        if (string.IsNullOrEmpty(isoString))
        {
            return DateTime.UtcNow.AddHours(1);
        }
        if (DateTime.TryParse(isoString, out var dt))
        {
            return dt.ToUniversalTime();
        }
        return DateTime.UtcNow.AddHours(1);
    }

    private void StartBackgroundRefresh()
    {
        _refreshTimer = new Timer(async _ =>
        {
            await FetchAsync();
        }, null, TimeSpan.FromSeconds(BackgroundRefreshSeconds), TimeSpan.FromSeconds(BackgroundRefreshSeconds));
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
