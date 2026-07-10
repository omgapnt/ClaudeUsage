using System.Globalization;
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
/// Failure reason for usage fetch
/// </summary>
public enum UsageFailureReason
{
    Ok,
    NoCredentials,
    HttpError,
    NetworkError,
    ParseError
}

/// <summary>
/// Result of a usage fetch operation
/// </summary>
public record UsageResult(
    UsageFailureReason Reason,
    UsageSnapshot? Snapshot = null,
    int? HttpStatusCode = null);

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
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfileEnv = Environment.GetEnvironmentVariable("USERPROFILE") ?? "(not set)";
        var baseDir = AppContext.BaseDirectory;

        DebugLog.WriteLine($"UsageClient construct: UserProfile={userProfile}, USERPROFILE env={userProfileEnv}, BaseDirectory={baseDir}");

        _credentialsPath = Path.Combine(userProfile, ".claude", ".credentials.json");
        StartBackgroundRefresh();
    }

    /// <summary>
    /// Get usage snapshot, using cache if fresher than CacheMaxAgeSeconds.
    /// Returns result with failure reason if unsuccessful.
    /// </summary>
    public async Task<UsageResult> GetAsync(bool force = false)
    {
        if (!force && _cachedSnapshot != null && (DateTime.UtcNow - _cachedSnapshot.FetchedAt).TotalSeconds < CacheMaxAgeSeconds)
        {
            return new UsageResult(UsageFailureReason.Ok, _cachedSnapshot);
        }

        return await FetchAsync();
    }

    private async Task<UsageResult> FetchAsync()
    {
        try
        {
            var credentialsExist = File.Exists(_credentialsPath);
            DebugLog.WriteLine($"FetchAsync: credentialsPath={_credentialsPath}, exists={credentialsExist}");

            var token = ReadToken();
            if (string.IsNullOrEmpty(token))
            {
                DebugLog.WriteLine($"FetchAsync: token is null/empty, returning NoCredentials");
                return new UsageResult(UsageFailureReason.NoCredentials);
            }

            DebugLog.WriteLine($"FetchAsync: token length={token.Length}, expiresAt=(N/A)");
            DebugLog.WriteLine($"FetchAsync: sending GET {ApiUrl} with Authorization Bearer, anthropic-beta={ApiVersion}, User-Agent={UserAgent}");

            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("anthropic-beta", ApiVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await HttpClient.SendAsync(request);
            var statusCode = (int)response.StatusCode;
            DebugLog.WriteLine($"FetchAsync: received status {statusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                var errorPreview = errorBody.Length > 200 ? errorBody.Substring(0, 200) : errorBody;
                DebugLog.WriteLine($"FetchAsync: error response body (first 200 chars): {errorPreview}");
                return new UsageResult(UsageFailureReason.HttpError, HttpStatusCode: statusCode);
            }

            var content = await response.Content.ReadAsStringAsync();
            DebugLog.WriteLine($"FetchAsync: response body length={content.Length}, first 500 chars: {(content.Length > 500 ? content.Substring(0, 500) : content)}");

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
            DebugLog.WriteLine($"FetchAsync: OK session={_cachedSnapshot.SessionLeftPercent:F1}% weekly={_cachedSnapshot.WeeklyLeftPercent:F1}%");
            return new UsageResult(UsageFailureReason.Ok, _cachedSnapshot);
        }
        catch (HttpRequestException ex)
        {
            DebugLog.WriteLine($"FetchAsync: HttpRequestException: {ex}");
            return new UsageResult(UsageFailureReason.NetworkError);
        }
        catch (OperationCanceledException ex)
        {
            DebugLog.WriteLine($"FetchAsync: OperationCanceledException: {ex}");
            return new UsageResult(UsageFailureReason.NetworkError);
        }
        catch (JsonException ex)
        {
            DebugLog.WriteLine($"FetchAsync: JsonException: {ex}");
            return new UsageResult(UsageFailureReason.ParseError);
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"FetchAsync: Unexpected exception: {ex}");
            return new UsageResult(UsageFailureReason.ParseError);
        }
    }

    private string? ReadToken()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                DebugLog.WriteLine($"ReadToken: credentials file does not exist at {_credentialsPath}");
                return null;
            }

            var json = File.ReadAllText(_credentialsPath);
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement
                .TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var token)
                ? token.GetString()
                : null;

            if (result != null)
            {
                DebugLog.WriteLine($"ReadToken: successfully read token, length={result.Length}");
            }
            else
            {
                DebugLog.WriteLine($"ReadToken: credentials file exists but claudeAiOauth.accessToken not found");
            }

            return result;
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"ReadToken: exception reading credentials: {ex}");
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
            // Handle null values in the path
            if (current.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        // Only return string if the element is actually a string
        if (current.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return current.GetString();
    }

    private double? GetDouble(JsonElement element, string path)
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
            // Handle null values in the path
            if (current.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        // If it's a number, get it directly
        if (current.ValueKind == JsonValueKind.Number)
        {
            if (current.TryGetDouble(out var doubleValue))
            {
                return doubleValue;
            }
        }
        // If it's a string, try parsing it
        else if (current.ValueKind == JsonValueKind.String)
        {
            var str = current.GetString();
            if (str != null && double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedValue))
            {
                return parsedValue;
            }
        }
        return null;
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
