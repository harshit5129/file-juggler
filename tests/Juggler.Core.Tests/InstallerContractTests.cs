using System.Text.RegularExpressions;

namespace Juggler.Core.Tests;

/// <summary>
/// Contract tests between the installer script and the application.
/// <para>
/// These exist because the installer's autostart value name and the name the diagnostics report
/// looks up had drifted apart: the installer wrote <c>"File Juggler"</c> while the app queried
/// <c>"FileJuggler"</c>. Nothing failed loudly. Autostart simply always reported as "not
/// registered", so a user could not tell whether it was working.
/// </para>
/// <para>
/// The two live in different files and different languages, which is exactly why a test is the
/// only thing that will catch a drift between them.
/// </para>
/// </summary>
public sealed class InstallerContractTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Juggler.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine([dir.FullName, .. parts]);
    }

    /// <summary>
    /// The Run key value the installer writes must be the one the app queries.
    /// </summary>
    [Fact]
    public void AutostartRegistryValueName_MatchesBetweenInstallerAndApp()
    {
        string iss = File.ReadAllText(RepoFile("installer", "FileJuggler.iss"));
        string app = File.ReadAllText(RepoFile("src", "Juggler.Ui", "Services", "DiagnosticsReport.cs"));

        // ValueName in the [Registry] section, after the AppName define is expanded.
        Match define = Regex.Match(iss, @"#define\s+AppName\s+""([^""]+)""");
        Assert.True(define.Success, "installer no longer defines AppName");

        string expected = define.Groups[1].Value;

        Match lookup = Regex.Match(app, @"GetValue\(""([^""]+)""\)");
        Assert.True(lookup.Success, "DiagnosticsReport no longer reads a Run value");

        Assert.Equal(expected, lookup.Groups[1].Value);
    }

    /// <summary>
    /// The installer must never require elevation. The tool's config lives outside Program Files,
    /// so a UAC prompt during install would be pure friction and would contradict the documented
    /// behaviour contract.
    /// </summary>
    [Fact]
    public void Installer_DoesNotRequireElevation()
    {
        string iss = File.ReadAllText(RepoFile("installer", "FileJuggler.iss"));

        Assert.Contains("PrivilegesRequired=lowest", iss, StringComparison.Ordinal);
    }

    /// <summary>
    /// Autostart must stay opt-in. Autostarting the on-demand editor would open a window at every
    /// sign-in, which is the behaviour this product exists to avoid.
    /// </summary>
    [Fact]
    public void Installer_LeavesAutostartUncheckedByDefault()
    {
        string iss = File.ReadAllText(RepoFile("installer", "FileJuggler.iss"));

        Match task = Regex.Match(iss, @"Name:\s*""autostart"";.*", RegexOptions.IgnoreCase);
        Assert.True(task.Success, "installer no longer defines an autostart task");
        Assert.Contains("unchecked", task.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A silent uninstall must not be able to block on a message box.
    /// </summary>
    [Fact]
    public void Installer_GuardsUninstallMessageBox()
    {
        string iss = File.ReadAllText(RepoFile("installer", "FileJuggler.iss"));

        Assert.Contains("UninstallSilent", iss, StringComparison.Ordinal);
    }
}