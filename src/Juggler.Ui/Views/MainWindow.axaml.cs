using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Juggler.Core.Configuration;
using Juggler.Core.Rules;
using Juggler.Ui.Services;
using Juggler.Ui.ViewModels;

namespace Juggler.Ui.Views;

public sealed partial class MainWindow : Window
{
    // Controls resolved from the visual tree on first open.
    private TextBlock _themeGlyph = null!;
    private StackPanel _stateChip = null!;
    private Ellipse _stateDot = null!;
    private TextBlock _stateChipText = null!;
    private Ellipse _statusDot = null!;
    private TextBlock _statusRules = null!;
    private TextBlock _statusStats = null!;
    private TextBlock _statusBytes = null!;
    private TextBlock _statusPath = null!;
    private Border _dryRunChip = null!;
    private Border _updateBanner = null!;
    private TextBlock _updateText = null!;

    private UpdateCheckResult? _update;
    private bool _updateChecked;
    private bool _updateDismissed;

    private ConfigService? _config;
    private RulesView? _rulesView;
    private SettingsView? _settingsView;
    private LogView? _logView;

    private bool _dark = true;
    private bool _started;

    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Startup work runs here rather than in the constructor: the visual tree is fully built by
    /// the time the window opens, so every lookup below is guaranteed to succeed, and touching
    /// the filesystem or creating the tray-side services this late is the correct order anyway.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_started)
        {
            return;
        }

        _themeGlyph = this.Require<TextBlock>("ThemeGlyph");
        _stateChip = this.Require<StackPanel>("StateChip");
        _stateDot = this.Require<Ellipse>("StateDot");
        _stateChipText = this.Require<TextBlock>("StateChipText");
        _statusDot = this.Require<Ellipse>("StatusDot");
        _statusRules = this.Require<TextBlock>("StatusRules");
        _statusStats = this.Require<TextBlock>("StatusStats");
        _statusBytes = this.Require<TextBlock>("StatusBytes");
        _statusPath = this.Require<TextBlock>("StatusPath");
        _dryRunChip = this.Require<Border>("DryRunChip");
        _updateBanner = this.Require<Border>("UpdateBanner");
        _updateText = this.Require<TextBlock>("UpdateText");

        _rulesView = this.Find<RulesView>() ?? throw new InvalidOperationException("Rules view missing.");
        _logView = this.Find<LogView>() ?? throw new InvalidOperationException("Log view missing.");
        _settingsView = this.Find<SettingsView>() ?? throw new InvalidOperationException("Settings view missing.");

        _config = new ConfigService();

        // Subscribed before the first render so the status bar is never briefly wrong.
        _config.Changed += OnConfigChanged;
        _rulesView.Command += OnRuleCommand;
        _rulesView.ImportRequested += OnImportRequested;
        _rulesView.ExportRequested += OnExportRequested;
        _rulesView.GetStartedRequested += RunSetup;
        _rulesView.RunRequested += OnRunRule;

        // Subscribed once here, not in Refresh, which runs on every config change.
        // Re-subscribing there would stack a duplicate handler per reload.
        _settingsView.Changed += OnSettingsChanged;

        _started = true;

        ApplyTheme();
        ClampToWorkArea();
        Refresh();

        MaybeRunSetup();
        CheckForUpdate();
    }

    /// <summary>
    /// Asks once, after the window is up, whether a newer release exists.
    /// <para>
    /// Deliberately last and fire-and-forget: the app is usable the moment it opens, and a slow
    /// or unreachable network must not delay that. Nothing is downloaded - the banner only points
    /// at the release, because replacing a running executable unattended is not something this
    /// build can verify.
    /// </para>
    /// </summary>
    private async void CheckForUpdate()
    {
        if (_updateChecked)
        {
            return;
        }

        _updateChecked = true;

        UpdateCheckResult result = await UpdateCheck.CheckAsync();

        _update = result;

        if (result.UpdateAvailable && !_updateDismissed)
        {
            ShowUpdateBanner(result);
        }
    }

    private void ShowUpdateBanner(UpdateCheckResult result)
    {
        _updateText.Text =
            $"Version {result.LatestVersion} is out. You have {UpdateCheck.CurrentVersion}.";
        _updateBanner.IsVisible = true;
    }

    private void OnDismissUpdate(object? sender, RoutedEventArgs e)
    {
        _updateDismissed = true;
        _updateBanner.IsVisible = false;
    }

    private void OnOpenReleasePage(object? sender, RoutedEventArgs e)
    {
        if (_update is not { ReleaseUrl.Length: > 0 } update)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a browser is a convenience. The release URL is public and in the docs, so
            // failing silently costs nothing.
        }
    }

    /// <summary>
    /// Shows first-run setup if it has neither been completed nor skipped.
    /// <para>
    /// Deferred to the next dispatcher frame: the wizard is modal on this window, and opening it
    /// from inside OnOpened while the layout pass is still running showed it behind the main
    /// window, stealing input before the frame settled.
    /// </para>
    /// </summary>
    private void MaybeRunSetup()
    {
        if (_config is null || _config.Current.SetupCompleted)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(RunSetup);
    }

    /// <summary>
    /// Runs setup and persists whichever way the user left it.
    /// <para>
    /// Reachable from the empty state's "Get started" button as well as from first launch, so
    /// skipping is never a one-way door.
    /// </para>
    /// </summary>
    private void RunSetup()
    {
        if (_config is null)
        {
            return;
        }

        SetupWizardViewModel vm = new();
        SetupWizard wizard = new(vm);

        wizard.ShowDialog(this);

        if (!vm.Completed)
        {
            return;
        }

        // Skip writes only the flag, leaving any existing rules alone. Accepting writes the whole
        // chosen set in one save, so the watcher sees a single rename rather than one per rule.
        if (vm.Accepted)
        {
            _config.ReplaceRules(vm.SelectedRules);
        }
        else
        {
            _config.MarkSetupComplete();
        }

        Refresh();
    }

    /// <summary>
    /// Forces the window's client area to a size that actually fits the screen.
    /// <para>
    /// The window was being created with a frame RenderScaling times too small: on a 125%
    /// display the requested 1180 DIP produced a 1180 px frame instead of 1475 px, so roughly
    /// 236 DIP of content - including every per-row Edit and overflow-menu button - rendered
    /// outside the window and could not be clicked at all.
    /// </para>
    /// <para>
    /// Two mistakes are corrected here. First, the working area is in physical pixels while
    /// <see cref="ClientSize"/> is in DIP, so the comparison has to divide by the screen's
    /// scaling; comparing them directly made the guard a no-op. Second, the assignment is
    /// unconditional: the initial size is already inside the work area, so a "shrink only"
    /// guard would never fire, and it is the frame size itself that is wrong. Reassigning
    /// pushes the correctly scaled size through to the platform window.
    /// </para>
    /// </summary>
    private void ClampToWorkArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;

            if (screen is null)
            {
                return;
            }

            // WorkingArea is physical pixels; ClientSize is DIP. Mixing them silently
            // disabled the whole clamp.
            double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            double workWidthDip = screen.WorkingArea.Width / scale;
            double workHeightDip = screen.WorkingArea.Height / scale;

            // Leave slack so the window is never flush against the taskbar or screen edge.
            double targetWidth = Math.Min(1180, workWidthDip * 0.96);
            double targetHeight = Math.Min(820, workHeightDip * 0.92);

            // Width/Height rather than ClientSize: assigning ClientSize was measured to leave
            // the platform frame untouched, while Width goes through window sizing properly.
            Width = Math.Max(MinWidth, targetWidth);
            Height = Math.Max(MinHeight, targetHeight);
        }
        catch (Exception)
        {
            // Cosmetic only. A slightly oversized window is a far better outcome than an app
            // that refuses to start because screen enumeration failed.
        }
    }
// ------------------------------------------------------------------ theme

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        ApplyTheme();
        Refresh();
    }

    private void ApplyTheme()
    {
        App.ApplyTheme(_dark ? ThemeVariant.Dark : ThemeVariant.Light);

        // Sun while dark (click for light), half-moon while light (click for dark).
        _themeGlyph.Text = _dark ? "\u2600" : "\u25D0";
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Header "New rule" button. Routes through the same command path as the list.</summary>
    private void OnNewRule(object? sender, RoutedEventArgs e) =>
        OnRuleCommand(new RuleCommand(Rule.New(), RuleCommandKind.New));

    /// <summary>Single entry point for every row-level intent in the rules list.</summary>
    private void OnRuleCommand(RuleCommand command)
    {
        if (_config is null)
        {
            return;
        }

        switch (command.Kind)
        {
            case RuleCommandKind.New:
                OpenEditor(new RuleEditorViewModel(command.Rule, _config), isNew: true);
                break;

            case RuleCommandKind.Edit:
                OpenEditor(new RuleEditorViewModel(command.Rule, _config), isNew: false);
                break;

            case RuleCommandKind.PersistOnly:
                _config.Upsert(command.Rule);
                break;

            case RuleCommandKind.Delete:
                _config.DeleteRule(command.Rule.Id);
                break;
        }

        Refresh();
    }

    private void OpenEditor(RuleEditorViewModel vm, bool isNew)
    {
        RuleEditorWindow window = new(vm, isNew);

        window.Closed += (_, _) =>
        {
            if (vm.SaveRequested && _config is not null)
            {
                _config.Upsert(vm.Rule);
                Refresh();
            }
        };

        window.Show(this);
    }

    /// <summary>
    /// Imports rules from a user-picked JSON file, merged into the current config.
    /// Unreadable files are ignored: there is no dialog infrastructure in this app,
    /// and the file picker already limits selection to JSON.
    /// </summary>
    private async void OnImportRequested()
    {
        if (_config is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked = await provider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Import rules",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } local)
        {
            return;
        }

        ConfigStore.LoadResult loaded;
        try
        {
            loaded = new ConfigStore(local).Load();
        }
        catch (Exception)
        {
            return;
        }

        if (loaded.Error is not null || loaded.Config.Rules.Count == 0)
        {
            return;
        }

        _config.ImportRules(loaded.Config.Rules);
        Refresh();
    }

    private async void OnExportRequested()
    {
        if (_config is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return;
        }

        IStorageFile? picked = await provider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export rules",
                SuggestedFileName = "rules.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });

        if (picked?.TryGetLocalPath() is not { } local)
        {
            return;
        }

        try
        {
            if (File.Exists(_config.ConfigPath))
            {
                File.Copy(_config.ConfigPath, local, overwrite: true);
            }
            else
            {
                // Nothing saved yet: write the live config so the export is never empty.
                IReadOnlyList<ConfigIssue> issues = new ConfigStore(local).Save(_config.Current);
                if (issues.Any(i => i.Severity == IssueSeverity.Error))
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Export is a convenience. The live config is untouched on failure.
        }
    }

    /// <summary>Opens the manual run window for one rule.</summary>
    private void OnRunRule(Rule rule)
    {
        if (_config is null)
        {
            return;
        }

        RunRuleWindow window = new(rule, _config);
        window.Closed += (_, _) => Refresh();
        window.Show(this);
    }

    private void OnDoctor(object? sender, RoutedEventArgs e)
    {
        if (_config is not null)
        {
            DiagnosticsWindow.Show(this, _config.Diagnose());
        }
    }

    // ------------------------------------------------------------------ state

    private void OnSettingsChanged()
    {
        if (_config is not null)
        {
            _config.SaveGeneral(_config.Current.General);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) => Refresh();

    /// <summary>Pushes the current config into every view and recomputes the status bar.</summary>
    public void Refresh()
    {
        if (_config is null || _rulesView is null || _logView is null || _settingsView is null)
        {
            return;
        }

        AppConfig config = _config.Current;

        _rulesView.Load(config);
        _logView.Load(_config);
        _settingsView.Load(config);

        int enabled = config.Rules.Count(r => r.Enabled);
        _statusRules.Text = $"{enabled} of {config.Rules.Count} rules enabled";

        _statusStats.Text = config.Stats.FilesProcessed == 1
            ? "1 file processed"
            : $"{config.Stats.FilesProcessed:N0} files processed";

        _statusBytes.Text = Formatting.Bytes(config.Stats.BytesProcessed);
        _statusPath.Text = _config.ConfigPath;
        ToolTip.SetTip(_statusPath, _config.ConfigPath);

        _dryRunChip.IsVisible = config.General.DryRun;

        // The chip is a summary, not a status light: it appears only when something needs
        // attention. A config error outranks the routine "watching" state.
        int errors = _config.Issues.Count(i => i.Severity == IssueSeverity.Error);

        if (errors > 0)
        {
            ShowChip("DangerBrush",
                $"{errors} config error{(errors == 1 ? "" : "s")}");
        }
        else if (config.Rules.Count > 0)
        {
            ShowChip("SuccessBrush", "Watching");
        }
        else
        {
            _stateChip.IsVisible = false;
            _statusDot.Fill = Brush("TextTertiaryBrush");
        }
    }

    /// <summary>
    /// Tints the dot and the word to tell watching apart from an error. The chip used to
    /// be a filled pill, which put a second colour on a screen meant to carry exactly one
    /// accent; the background fill was then removed, so there is deliberately no
    /// background parameter.
    /// </summary>
    private void ShowChip(string foreground, string text)
    {
        _stateChip.IsVisible = true;
        _stateChipText.Text = text;
        _stateChipText.Foreground = Brush(foreground);
        _stateDot.Fill = Brush(foreground);
        _statusDot.Fill = Brush(foreground);
    }

    /// <summary>
    /// Resolves a palette brush, falling back to a neutral colour.
    /// <para>
    /// The palette lives in theme dictionaries, which are resolved per <see cref="ThemeVariant"/>.
    /// During startup the window's variant may still be unset, so a lookup can legitimately miss.
    /// A neutral fallback keeps the status bar legible; it must never throw, because a missing
    /// cosmetic brush is not worth taking the window down for.
    /// </para>
    /// </summary>
    private static IBrush Brush(string key, string fallback = "#FF7A8699")
    {
        if (Application.Current?.TryFindResource(key, null, out object? value) == true && value is IBrush brush)
        {
            return brush;
        }

        return Avalonia.Media.Brush.Parse(fallback);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_config is not null)
        {
            _config.Changed -= OnConfigChanged;
            _config.Dispose();
        }

        base.OnClosed(e);
    }
}
