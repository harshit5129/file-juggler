using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

        _rulesView = this.Find<RulesView>() ?? throw new InvalidOperationException("Rules view missing.");
        _logView = this.Find<LogView>() ?? throw new InvalidOperationException("Log view missing.");
        _settingsView = this.Find<SettingsView>() ?? throw new InvalidOperationException("Settings view missing.");

        _config = new ConfigService();

        // Subscribed before the first render so the status bar is never briefly wrong.
        _config.Changed += OnConfigChanged;
        _rulesView.Command += OnRuleCommand;

        // Subscribed once here, not in Refresh, which runs on every config change.
        // Re-subscribing there would stack a duplicate handler per reload.
        _settingsView.Changed += OnSettingsChanged;

        _started = true;

        ApplyTheme();
        Refresh();
    }

    // ------------------------------------------------------------------ theme

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        ApplyTheme();
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
            ShowChip("DangerBrush", "DangerSoftBrush",
                $"{errors} config error{(errors == 1 ? "" : "s")}");
        }
        else if (config.Rules.Count > 0)
        {
            ShowChip("SuccessBrush", "SuccessSoftBrush", "Watching");
        }
        else
        {
            _stateChip.IsVisible = false;
            _statusDot.Fill = Brush("TextTertiaryBrush");
        }
    }

    /// <summary>
    /// The <c>background</c> parameter is intentionally unused: the chip used to be a filled
    /// pill, which put a second colour on a screen meant to carry exactly one accent. Tinting
    /// the dot and the word is enough to tell watching apart from an error.
    /// </summary>
    private void ShowChip(string foreground, string background, string text)
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
