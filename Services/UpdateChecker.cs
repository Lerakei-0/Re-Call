using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ReCall.Services;

/// <summary>Checks GitHub's "latest release" API for a newer tagged version
/// than <see cref="CurrentVersion"/>. Used by the Settings > About tab, both
/// for the "Check for updates" button and (via <see cref="LastResult"/>) an
/// optional silent check on startup. Ported from Kronos's
/// Services/UpdateChecker.cs.</summary>
public static class UpdateChecker
{
    // Owner is the GitHub account from the About tab's link
    // (https://github.com/Lerakei-0/Re-Call). This is the app's official repo.
    private const string Owner = "Lerakei-0";
    private const string Repo = "Re-Call";

    public const string CurrentVersion = "1.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static UpdateChecker()
    {
        // GitHub's REST API rejects requests with no User-Agent header.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ReCall-UpdateChecker");
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public sealed record Result(bool CheckSucceeded, bool UpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error);

    /// <summary>Set after any completed check (startup or manual) so the
    /// About tab can show a result immediately when it's opened, rather than
    /// silently re-running the check a second time.</summary>
    public static Result? LastResult { get; private set; }

    public static async Task<Result> CheckAsync()
    {
        var result = await CheckCoreAsync();
        LastResult = result;
        return result;
    }

    private static async Task<Result> CheckCoreAsync()
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            if (!response.IsSuccessStatusCode)
            {
                // GitHub's "latest release" endpoint returns 404 both when the
                // repo doesn't exist AND (more commonly, since this repo does
                // exist) when it exists but has no *published* releases yet -
                // draft/pre-release-only repos hit this too. Give a message
                // that points at the actual cause instead of a bare status code.
                var message = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "No published releases found yet."
                    : $"GitHub returned {(int)response.StatusCode}.";
                return new Result(false, false, null, null, message);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

            var latest = tag.TrimStart('v', 'V');
            var isNewer = Version.TryParse(NormalizeForVersion(latest), out var latestVer)
                          && Version.TryParse(NormalizeForVersion(CurrentVersion), out var currentVer)
                          && latestVer > currentVer;

            return new Result(true, isNewer, latest, url, null);
        }
        catch (Exception ex)
        {
            return new Result(false, false, null, null, ex.Message);
        }
    }

    // System.Version requires at least two components ("1" alone throws), so
    // pad a bare major-only tag like "2" out to "2.0" before parsing.
    private static string NormalizeForVersion(string v) => v.Contains('.') ? v : $"{v}.0";
}
