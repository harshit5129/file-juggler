using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Juggler.Core.Configuration;
using Juggler.Core.Execution;
using Juggler.Core.Rules;
using Juggler.Ui.Services;

namespace Juggler.Ui.Views;

/// <summary>
/// Runs one rule on one folder, by hand.
/// <para>
/// Nothing moves until the user has read the list. Opening on a preview is the whole point: a
/// person running a rule by hand is present to decide, and a rule with a broad condition can
/// match more than its author expected.
/// </para>
/// <para>
/// The dry-run switch is global and this window does not override it. A tool that moves real
/// files gets one brake, not one per screen.
/// </para>
/// </summary>
public sealed partial class RunRuleWindow : Window
{
    private readonly Rule _rule;
    private readonly ConfigService _config;

    private string? _root;
    private RuleRunResult? _result;
    private bool _running;

    private TextBlock _summary = null!;
    private TextBlock _titleText = null!;
    private TextBlock _modeText = null!;
    private TextBlock _folderHint = null!;
    private TextBlock _targetText = null!;
    private ItemsControl _list = null!;
    private StackPanel _emptyState = null!;
    private TextBox _pathBox = null!;

    private bool _ready;

    public RunRuleWindow(Rule rule, ConfigService config)
    {
        InitializeComponent();

        _rule = rule;
        _config = config;

        Title = $"Run - {rule.Name}";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string RuleName => _rule.Name;

    public bool DryRun => _config.Current.General.DryRun;

    /// <summary>Plain-English statement of whether this will change anything.</summary>
    public string ModeNotice => DryRun
        ? "Preview mode. Nothing will be moved, copied or deleted."
        : "This moves real files. Read the list, then press Go.";

    /// <summary>
    /// Tells the user what happens once they have picked a folder. Set into the view directly
    /// rather than bound: AvaloniaObject already implements INotifyPropertyChanged, so a binding
    /// here would mean either shadowing that event or giving the window a view model for two
    /// strings.
    /// </summary>
    private string FolderHint
    {
        get
        {
            if (_root is null)
            {
                return "Pick the folder to look in. Leave it blank if you are not sure.";
            }

            string scope = _rule.Monitor.IncludeSubfolders
                ? "this folder and everything inside it"
                : "this folder only";

            return $"Looking in: {scope}.";
        }
    }

    /// <summary>
    /// The summary line is set in code and bound through the named TextBlock, so it has no
    /// bindable property here. A property named <c>Summary</c> would collide with the field the
    /// XAML name generator emits for <c>x:Name="Summary"</c>.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_ready)
        {
            return;
        }

        _ready = true;

        _summary = this.Require<TextBlock>("Summary");
        _titleText = this.Require<TextBlock>("TitleText");
        _modeText = this.Require<TextBlock>("ModeText");
        _folderHint = this.Require<TextBlock>("FolderHintText");
        _targetText = this.Require<TextBlock>("TargetText");
        _list = this.Require<ItemsControl>("ResultList");
        _emptyState = this.Require<StackPanel>("EmptyState");
        _pathBox = this.Require<TextBox>("PathBox");

        _titleText.Text = RuleName;
        _modeText.Text = ModeNotice;

        // A rule watching one folder has an obvious answer, so pre-fill it rather than making the
        // user browse for the folder they just configured.
        if (_rule.Monitor.Paths.Count == 1)
        {
            SetRoot(_rule.Monitor.Paths[0].Path);
        }
        else if (_rule.Monitor.Paths.Count > 1)
        {
            _summary.Text = $"This rule watches {_rule.Monitor.Paths.Count} folders. Pick the one to run on.";
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await provider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Pick a folder", AllowMultiple = false });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
        {
            SetRoot(local);
        }
    }

    /// <summary>
    /// Typing a folder updates the list. No binding guard is needed: the box is only written by
    /// SetRoot during startup and by Browse, both of which set it before the preview runs, so a
    /// handler cannot feed back into itself.
    /// </summary>
    private void OnPreview(object? sender, TextChangedEventArgs e) => SetRoot(_pathBox.Text);

    private void SetRoot(string? root)
    {
        _root = string.IsNullOrWhiteSpace(root) ? null : root;
        _folderHint.Text = FolderHint;

        if (_root is null)
        {
            Show(null, "Nothing chosen yet.");
            return;
        }

        try
        {
            // Built with dry-run on, so this path cannot act even if the global switch changed.
            Show(new RuleRunner(dryRun: true).Preview(_rule, _root), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Show(null, Plain(ex.Message));
        }
    }

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_root is null || _running)
        {
            return;
        }

        _running = true;

        try
        {
            RuleRunResult result = new RuleRunner(DryRun).Run(_rule, _root);

            Show(result, Describe(result));

            if (result.Changed > 0)
            {
                Record(result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Show(null, Plain(ex.Message));
        }
        finally
        {
            _running = false;
        }
    }

    private void Show(RuleRunResult? result, string? summary)
    {
        _result = result;
        _summary.Text = summary ?? string.Empty;

        List<ResultRow> rows = result is null
            ? []
            : [.. result.Entries
                   .Where(e => e.Outcome != ActionOutcome.Skipped)
                   .Select(ResultRow.From)];

        _list.ItemsSource = rows;
        _emptyState.IsVisible = rows.Count == 0;
    }

    /// <summary>Counts, in the fewest words that are still accurate.</summary>
    private static string Describe(RuleRunResult result)
    {
        if (result.Changed > 0)
        {
            return $"{Plural(result.Changed, "file")} moved. {Plural(result.Failed, "problem")}.";
        }

        if (result.WouldHaveChanged > 0)
        {
            return $"Preview: {Plural(result.WouldHaveChanged, "file")} would change.";
        }

        return "Nothing matched.";
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// Exception text often contains a full path or a raw HRESULT. Neither helps someone who
    /// just wants to know why their file did not move.
    /// </summary>
    private static string Plain(string message) =>
        string.IsNullOrWhiteSpace(message) ? "Could not do that." : message.Trim();

    /// <summary>Writes counters back so the status bar reflects a manual run.</summary>
    private void Record(RuleRunResult result)
    {
        RunStats stats = _config.Current.Stats;

        _config.SaveRunStats(stats with
        {
            FilesProcessed = stats.FilesProcessed + result.Changed,
            FilesMatched = stats.FilesMatched + result.Matched,
            RulesFailed = stats.RulesFailed + result.Failed,
            LastRunAt = DateTimeOffset.Now,
        });
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>One line in the list. Flattened so the template can bind to it.</summary>
    public sealed record ResultRow
    {
        public static ResultRow From(RuleRunEntry entry) => new()
        {
            Source = entry.Source,
            Target = entry.Target ?? string.Empty,
            HasTarget = !string.IsNullOrEmpty(entry.Target),
            Note = NoteFor(entry),
            HasNote = entry.Note is not null,
            Outcome = Word(entry.Outcome),
        };

        public required string Source { get; init; }
        public required string Target { get; init; }
        public required bool HasTarget { get; init; }
        public required string Note { get; init; }
        public required bool HasNote { get; init; }
        public required string Outcome { get; init; }

        /// <summary>The one-line explanation, with the technical prefix stripped off.</summary>
        private static string NoteFor(RuleRunEntry entry) => entry.Note switch
        {
            "Dry run: nothing changed." => "Preview only. Nothing was changed.",
            "No match." => string.Empty,
            null => string.Empty,
            var note => note,
        };

        /// <summary>Past tense for what already happened, future for what would.</summary>
        private static string Word(ActionOutcome outcome) => outcome switch
        {
            ActionOutcome.WouldAct => "would change",
            ActionOutcome.Moved => "moved",
            ActionOutcome.Copied => "copied",
            ActionOutcome.Recycled => "deleted",
            _ => "skipped",
        };
    }
}