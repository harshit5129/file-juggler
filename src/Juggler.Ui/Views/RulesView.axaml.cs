using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Juggler.Core.Configuration;
using Juggler.Core.Rules;
using Juggler.Ui.Services;
using Juggler.Ui.ViewModels;

namespace Juggler.Ui.Views;

public sealed partial class RulesView : UserControl
{
    private TextBox _searchBox = null!;
    private ItemsControl _ruleList = null!;
    private StackPanel _emptyState = null!;
    private StackPanel _noMatchState = null!;

    private List<RuleRow> _all = [];
    private bool _ready;

    public RulesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// One event for every row-level intent. A command record rather than several separate
    /// events, so the host cannot end up handling Edit without handling Duplicate, which is
    /// how half the rows quietly end up behaving differently.
    /// </summary>
    public event Action<RuleCommand>? Command;

    protected override void OnInitialized()
    {
        base.OnInitialized();

        if (_ready)
        {
            return;
        }

        _searchBox = this.Require<TextBox>("SearchBox");
        _ruleList = this.Require<ItemsControl>("RuleList");
        _emptyState = this.Require<StackPanel>("EmptyState");
        _noMatchState = this.Require<StackPanel>("NoMatchState");

        _ready = true;

        ApplyFilter();
    }

    public void Load(AppConfig config)
    {
        _all = [.. config.Rules.Select(r => new RuleRow(r))];

        if (_ready)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        if (!_ready)
        {
            return;
        }

        string query = _searchBox.Text?.Trim() ?? string.Empty;

        List<RuleRow> visible = query.Length == 0
            ? _all
            :
            [
                .. _all.Where(r =>
                    r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.PathSummary.Contains(query, StringComparison.OrdinalIgnoreCase))
            ];

        _ruleList.ItemsSource = visible;

        _emptyState.IsVisible = _all.Count == 0;
        _noMatchState.IsVisible = _all.Count > 0 && visible.Count == 0;
    }

    // ------------------------------------------------------------------ search

    private void OnSearchKeyUp(object? sender, KeyEventArgs e) => ApplyFilter();

    private void OnClearFilter(object? sender, RoutedEventArgs e)
    {
        _searchBox.Text = string.Empty;
        ApplyFilter();
    }

    // ------------------------------------------------------------------ actions

    private void OnNewRule(object? sender, RoutedEventArgs e) =>
        Command?.Invoke(new RuleCommand(Rule.New(), RuleCommandKind.New));

    private void OnEdit(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is RuleRow row)
        {
            Command?.Invoke(new RuleCommand(row.Rule, RuleCommandKind.Edit));
        }
    }

    private void OnToggleEnabled(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not RuleRow row)
        {
            return;
        }

        // The switch has already written IsChecked; mirror that value onto the model.
        bool enabled = toggle.IsChecked ?? true;
        row.Rule = row.Rule with { Enabled = enabled };

        Command?.Invoke(new RuleCommand(row.Rule, RuleCommandKind.PersistOnly));
    }

    private void OnMore(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not RuleRow row)
        {
            return;
        }

        MenuItem edit = new() { Header = "Edit\u2026" };
        edit.Click += (_, _) => Command?.Invoke(new RuleCommand(row.Rule, RuleCommandKind.Edit));

        MenuItem duplicate = new() { Header = "Duplicate" };
        duplicate.Click += (_, _) => Command?.Invoke(new RuleCommand(
            row.Rule with { Id = Rule.New().Id, Name = row.Rule.Name + " (copy)" },
            RuleCommandKind.New));

        MenuItem toggle = new() { Header = row.IsEnabled ? "Pause rule" : "Resume rule" };
        toggle.Click += (_, _) =>
        {
            row.ToggleEnabled();
            Command?.Invoke(new RuleCommand(row.Rule, RuleCommandKind.PersistOnly));
        };

        MenuItem delete = new()
        {
            Header = "Delete",
            Foreground = DangerBrush(),
        };
        delete.Click += (_, _) => Command?.Invoke(new RuleCommand(row.Rule, RuleCommandKind.Delete));

        ContextMenu menu = new()
        {
            ItemsSource = new object?[] { edit, duplicate, toggle, new Separator(), delete },
        };

        if (sender is Control control)
        {
            menu.Open(control);
        }
    }
    /// <summary>
    /// Resolves the danger brush for the current theme. A missing brush must not throw: the menu
    /// is a convenience, and losing the red would be a far better outcome than a crash.
    /// </summary>
    private IBrush? DangerBrush() =>
        Application.Current?.TryFindResource("DangerBrush", null, out object? value) == true
            ? value as IBrush
            : null;
}

/// <summary>What the user asked for, independent of which control they used.</summary>
public enum RuleCommandKind
{
    /// <summary>Create a brand-new rule from the supplied one.</summary>
    New,

    /// <summary>Open the supplied rule in the editor.</summary>
    Edit,

    /// <summary>Write the supplied rule to disk without opening the editor.</summary>
    PersistOnly,

    /// <summary>Remove the supplied rule.</summary>
    Delete,
}

public sealed record RuleCommand(Rule Rule, RuleCommandKind Kind);
