using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Juggler.Core.Configuration;
using Juggler.Ui.Services;

namespace Juggler.Ui.Views;

/// <summary>One line in the log list.</summary>
public sealed class LogEntry
{
    public required string Time { get; init; }

    public required string Message { get; init; }

    public required string Severity { get; init; }

    public required IBrush SeverityBrush { get; init; }

    /// <summary>Factory that resolves the severity brush from the active theme.</summary>
    public static LogEntry Make(string time, string severity, string message, string brushKey) => new()
    {
        Time = time,
        Severity = severity,
        Message = message,
        SeverityBrush = Resolve(brushKey),
    };

    private static IBrush Resolve(string key) =>
        Application.Current?.TryFindResource(key, null, out object? value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
}

public sealed partial class LogView : UserControl
{
    private ComboBox _severityFilter = null!;
    private ItemsControl _logList = null!;
    private StackPanel _emptyState = null!;
    private TextBlock _footerText = null!;

    private ConfigService? _config;
    private List<LogEntry> _all = [];
    private bool _ready;

    public LogView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnInitialized()
    {
        base.OnInitialized();

        if (_ready)
        {
            return;
        }

        _severityFilter = this.Require<ComboBox>("SeverityFilter");
        _logList = this.Require<ItemsControl>("LogList");
        _emptyState = this.Require<StackPanel>("EmptyState");
        _footerText = this.Require<TextBlock>("FooterText");

        _ready = true;
        ApplyFilter();
    }

    public void Load(ConfigService config)
    {
        _config = config;

        List<LogEntry> entries = [];

        // Config problems always show: they are the one thing worth knowing even before the
        // daemon exists.
        foreach (ConfigIssue issue in config.Issues)
        {
            entries.Add(LogEntry.Make(
                "--:--:--",
                issue.Severity.ToString().ToUpperInvariant(),
                issue.Format(),
                issue.Severity == IssueSeverity.Error ? "DangerBrush" : "WarningBrush"));
        }

        foreach (string line in ReadLogTail())
        {
            entries.Add(LogEntry.Make(ParseTime(line), "INFO", StripPrefix(line), "TextSecondaryBrush"));
        }

        _all = entries;

        if (_ready)
        {
            ApplyFilter();
        }
    }

    /// <summary>
    /// Reads the tail of the daemon's current log file, if there is one.
    /// <para>
    /// Deliberately bounded: the log rotates at 5 MB, and an unbounded read would be precisely
    /// the memory mistake this project exists to avoid.
    /// </para>
    /// </summary>
    private static IEnumerable<string> ReadLogTail(int maxLines = 500)
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileJuggler");

        string? active = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "juggler*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;

        if (active is null)
        {
            return [];
        }

        try
        {
            using FileStream stream = File.Open(active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            long start = Math.Max(0, stream.Length - 128 * 1024);
            stream.Seek(start, SeekOrigin.Begin);

            using StreamReader reader = new(stream);

            return
            [
                .. reader.ReadToEnd()
                       .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.TrimEnd('\r'))
                       .TakeLast(maxLines)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ParseTime(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : "--:--:--";
    }

    private static string StripPrefix(string line)
    {
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 ? parts[2] : line;
    }

    private void ApplyFilter()
    {
        if (!_ready)
        {
            return;
        }

        int filter = _severityFilter.SelectedIndex;

        List<LogEntry> filtered = filter switch
        {
            1 => [.. _all.Where(e => e.Severity.Contains("Error", StringComparison.OrdinalIgnoreCase))],
            2 => [.. _all.Where(e => e.Severity.Contains("Warn", StringComparison.OrdinalIgnoreCase))],
            _ => [.. _all],
        };

        _logList.ItemsSource = filtered;
        _emptyState.IsVisible = _all.Count == 0;

        _footerText.Text = _all.Count == 0
            ? "No log file yet - it is created when the daemon first runs."
            : $"{filtered.Count} of {_all.Count} shown";
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (_config is not null)
        {
            Load(_config);
        }
    }

    private void OnRevealLogs(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileJuggler");

            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience. Failing silently is acceptable because the
            // path is also shown in Diagnostics, and this app never interrupts the user.
        }
    }
}
