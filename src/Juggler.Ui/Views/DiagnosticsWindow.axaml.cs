using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Input.Platform;
using Juggler.Ui.Services;

namespace Juggler.Ui.Views;

public sealed partial class DiagnosticsWindow : Window
{
    private readonly string _report;

    private TextBox _reportBox = null!;
    private TextBlock _copiedText = null!;
    private bool _ready;

    private DiagnosticsWindow(string report)
    {
        InitializeComponent();

        _report = report;
        Title = "Diagnostics";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Resolves the named controls and fills the report.
    /// <para>
    /// Must run from OnOpened, not OnInitialized: for a Window, Avalonia raises
    /// OnInitialized from inside the base constructor, which runs before the derived
    /// constructor body, so InitializeComponent() has not yet run and the tree is empty.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_ready)
        {
            return;
        }

        _reportBox = this.Require<TextBox>("ReportBox");
        _copiedText = this.Require<TextBlock>("CopiedText");
        _reportBox.Text = _report;

        _ready = true;
    }

    public static void Show(Window owner, DiagnosticsReport report)
    {
        DiagnosticsWindow window = new(report.ToReport());
        window.Show(owner);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(_report);
            _copiedText.IsVisible = true;
        }
        catch (Exception)
        {
            // Clipboard access can be blocked by policy. The text stays selectable on screen, and
            // silently failing is correct here: this app never interrupts the user with a dialog.
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
