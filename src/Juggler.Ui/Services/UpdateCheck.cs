using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Juggler.Ui.Services;

/// <summary>Outcome of asking GitHub whether a newer release exists.</summary>
/// <param name="Status">None when the check did not complete. Never an error to the user:
/// a failed check must be indistinguishable from no update, or the app looks broken offline.</param>
public sealed record UpdateCheckResult(UpdateStatus Status, string LatestVersion, string ReleaseUrl)
{
    public bool UpdateAvailable => Status == UpdateStatus.Available;
}

public enum UpdateStatus
{
    /// <summary>Check did not complete, or found nothing newer.</summary>
    None,

    /// <summary>A newer release is published.</summary>
    Available,
}

/// <summary>
/// Checks whether a newer release has been published.
/// <para>
/// This does not download or install anything. Silently replacing the running executable is
/// the kind of thing that bricks a tool mid-session, and it cannot be verified from here. What it
/// does instead is notice and tell the user where the download is, which is the behaviour a
/// pre-1.0 tool should have anyway.
/// </para>
/// <para>
/// The whole check is one unauthenticated GET against the public releases API and is wrapped so
/// that no network condition can surface as a dialog or a crash. On a machine with no network it
/// resolves to <see cref="UpdateStatus.None"/> within the timeout.
/// </para>
/// </summary>
public sealed class UpdateCheck
{
    private const string Repo = "harshit5129/file-juggler";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    static UpdateCheck()
    {
        // No User-Agent: GitHub's API rejects requests without one, and the default
        // HttpClient sends none.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FileJuggler");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The version this build was stamped with by pack.ps1.</summary>
    public static string CurrentVersion
    {
        get
        {
            Version? version = Assembly.GetEntryAssembly()?.GetName().Version;

            return version is null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            string json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest");

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";

            string url = root.TryGetProperty("html_url", out JsonElement urlElement)
                ? urlElement.GetString() ?? ""
                : $"https://github.com/{Repo}/releases/latest";

            string latest = tag.TrimStart('v', 'V');

            return IsNewer(latest, CurrentVersion)
                ? new UpdateCheckResult(UpdateStatus.Available, latest, url)
                : new UpdateCheckResult(UpdateStatus.None, latest, url);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or JsonException
                                   or UriFormatException
                                   or NotSupportedException)
        {
            // Offline, rate-limited, DNS failure, malformed payload. All of them mean the same
            // thing to the user: we do not know, so say nothing.
            return new UpdateCheckResult(UpdateStatus.None, CurrentVersion, "");
        }
    }

    /// <summary>
    /// Compares dotted version strings numerically. A plain string compare would call "0.10.0"
    /// older than "0.9.0", which would strand every user on the release before it.
    /// </summary>
    /// <summary>
    /// Public so the test suite can pin the comparison. A string compare here would put 0.10.0
    /// behind 0.9.0 and strand every user on an old build, and that bug would not show up
    /// anywhere until the tenth release shipped.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        int[] a = Parts(candidate);
        int[] b = Parts(current);

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int left = i < a.Length ? a[i] : 0;
            int right = i < b.Length ? b[i] : 0;

            if (left != right)
            {
                return left > right;
            }
        }

        return false;
    }

    private static int[] Parts(string version)
    {
        string[] pieces = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int[] parts = new int[pieces.Length];

        for (int i = 0; i < pieces.Length; i++)
        {
            // Take the leading digits so a pre-release tag like "0.2.0-beta" parses as 0.2.0
            // rather than throwing on the dash.
            string numeric = string.Concat(pieces[i].TakeWhile(char.IsAsciiDigit));
            parts[i] = int.TryParse(numeric, out int value) ? value : 0;
        }

        return parts;
    }
}