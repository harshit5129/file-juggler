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

    protected override void OnInitialized()
    {
        base.OnInitialized();

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
