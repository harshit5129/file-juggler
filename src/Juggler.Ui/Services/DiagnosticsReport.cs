using System.Text;
using Juggler.Core.Configuration;

namespace Juggler.Ui.Services;

/// <summary>
/// Output of <c>juggler.exe doctor</c>, rendered in the editor so a bug report can be
/// answered without asking the user to open a terminal.
/// <para>
/// Deliberately shareable: it reports the config <em>directory</em> and rule counts, not the
/// full rule bodies, because rules contain real filesystem paths. See SECURITY.md.
/// </para>
/// </summary>
public sealed record DiagnosticsReport(
    string ConfigPath,
    string? LoadError,
    IReadOnlyList<ConfigIssue> Issues,
    AppConfig Config)
{
    public string ConfigDirectory =>
        System.IO.Path.GetDirectoryName(ConfigPath) ?? ConfigPath;

    public string RuntimeDescription =>
        $"{(Environment.Is64BitProcess ? "x64" : "x86")} / "
        + $"{(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "Windows 11" : "Windows 10")}";

    /// <summary>Plain text suitable for pasting into an issue.</summary>
    public string ToReport()
    {
        StringBuilder sb = new();

        sb.AppendLine("File Juggler diagnostics");
        sb.AppendLine($"  platform        : {RuntimeDescription}");
        sb.AppendLine($"  process         : {(Environment.ProcessPath ?? "unknown")}");
        sb.AppendLine($"  working set     : {Environment.WorkingSet / 1024 / 1024} MB");
        sb.AppendLine($"  config file     : {ConfigPath}");
        sb.AppendLine($"  config exists   : {File.Exists(ConfigPath)}");
        sb.AppendLine($"  run mode        : {Config.General.Mode}");
        sb.AppendLine($"  priority        : {Config.General.Priority}");
        sb.AppendLine($"  dry run         : {(Config.General.DryRun ? "YES" : "no")}");
        sb.AppendLine($"  rules           : {Config.Rules.Count} ({Config.Rules.Count(r => r.Enabled)} enabled)");
        sb.AppendLine($"  files processed : {Config.Stats.FilesProcessed}");
        sb.AppendLine($"  bytes processed : {Config.Stats.BytesProcessed}");
        sb.AppendLine($"  autostart       : {IsAutostartRegistered()}");

        if (LoadError is not null)
        {
            sb.AppendLine();
            sb.AppendLine("LOAD ERROR:");
            sb.AppendLine($"  {LoadError}");
        }

        if (Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{Issues.Count} config issue(s):");
            foreach (ConfigIssue issue in Issues)
            {
                sb.AppendLine($"  [{issue.Severity}] {issue.Format()}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Registry read rather than a stored flag: the user may have removed the entry by hand,
    /// and a stale flag would be worse than a live check.
    /// </summary>
    public static bool IsAutostartRegistered()
    {
        // Registry is a Windows-only API. The analyzer (CA1416) is right that this is reachable
        // on every platform, and although the catch below would swallow the resulting exception,
        // depending on an exception for control flow would misreport "not registered" on a
        // platform where the question is meaningless.
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");

            return key?.GetValue("File Juggler") is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
