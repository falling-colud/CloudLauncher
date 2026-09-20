using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The in-window card that collects a server's RCON details, with a Test button that proves them
/// before they are saved.
/// </summary>
/// <remarks>
/// <para>The Test button is the point of this card. RCON has four ways to fail — the port is closed,
/// <c>enable-rcon</c> is false, the password is wrong, or something else entirely is listening — and
/// without a test they all look the same later, from inside a console that simply will not connect.
/// Testing here names the actual problem while the fields are still on screen.</para>
/// <para>The password is shown through a <see cref="PasswordBox"/> and is never written to
/// <see cref="AppLog"/>, put in a status message, or echoed into the console scrollback. It is still
/// stored in clear in settings.json, which the card says plainly rather than leaving the user to
/// assume otherwise.</para>
/// </remarks>
public partial class ServerConsoleSetupDialog : UserControl
{
    /// <summary>What the card came back with. <paramref name="Removed"/> means the user chose to
    /// forget this server's console entirely; the other fields are then meaningless.</summary>
    public sealed record SetupResult(bool Removed, string? Label, string? RconHost, int RconPort, string? Password);

    private readonly TaskCompletionSource<SetupResult?> _tcs = new();
    private readonly string _gameHost;
    private CancellationTokenSource? _testCts;

    private ServerConsoleSetupDialog(string serverName, string gameAddress, ServerAdminEntry? existing)
    {
        InitializeComponent();
        _gameHost = MinecraftServerPing.ParseAddress(gameAddress).Host;

        TitleLabel.Text = existing?.HasConsole == true ? "Console settings" : "Set up console";
        SubLabel.Text = $"Connects to {serverName} over RCON so you can run commands from the launcher.";
        LabelBox.Text = existing?.Label ?? "";
        HostBox.Text = existing?.RconHost ?? "";
        PortBox.Text = (existing?.RconPort is > 0 and <= 65535 ? existing.RconPort : RconClient.DefaultPort)
            .ToString();
        PasswordInput.Password = existing?.RconPassword ?? "";
        RemoveButton.Visibility = existing is not null ? Visibility.Visible : Visibility.Collapsed;

        Focusable = true;
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            HostBox.ToolTip = $"Blank means {_gameHost} — the same host as the game address.";
            (PasswordInput.Password.Length == 0 ? PasswordInput : (Control)LabelBox).Focus();
        };
        Unloaded += (_, _) => _testCts?.Cancel();
    }

    public Task<SetupResult?> Result => _tcs.Task;
    public void Cancel() { _testCts?.Cancel(); _tcs.TrySetResult(null); }

    public static async Task<SetupResult?> ShowAsync(MainWindow host, string serverName, string gameAddress,
                                                     ServerAdminEntry? existing)
    {
        var card = new ServerConsoleSetupDialog(serverName, gameAddress, existing);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    /// <summary>The host the console will actually dial: the override when one is set, the game host
    /// otherwise.</summary>
    private string EffectiveHost =>
        string.IsNullOrWhiteSpace(HostBox.Text) ? _gameHost : HostBox.Text.Trim();

    private bool TryReadPort(out int port)
    {
        var text = PortBox.Text?.Trim() ?? "";
        if (text.Length == 0) { port = RconClient.DefaultPort; return true; }
        return int.TryParse(text, out port) && port is > 0 and <= 65535;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryReadPort(out var port)) { Say("That port is not a number between 1 and 65535.", danger: true); return; }
            if (EffectiveHost.Length == 0) { Say("There is no host to connect to.", danger: true); return; }
            if (PasswordInput.Password.Length == 0) { Say("Enter the RCON password first.", danger: true); return; }

            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            TestButton.IsEnabled = false;
            Say($"Connecting to {EffectiveHost}:{port}…");

            using var client = await RconClient.ConnectAsync(EffectiveHost, port, PasswordInput.Password,
                                                             ct: _testCts.Token);
            Say("Connected — the password works.", success: true);
        }
        catch (OperationCanceledException) { }
        catch (RconAuthenticationException ex) { Say(ex.Message, danger: true); }
        catch (TimeoutException ex) { Say(ex.Message + " Check the port is open and enable-rcon=true.", danger: true); }
        catch (Exception ex) { Say("Could not connect: " + ex.Message, danger: true); }
        finally { TestButton.IsEnabled = true; }
    }

    private void Say(string message, bool danger = false, bool success = false)
    {
        StatusLabel.Text = message;
        // SetResourceReference rather than a fetched brush: the colour has to follow a live theme
        // change, and a Brush pulled out of the resources here would be a snapshot of one theme.
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,
            danger ? "DangerBrush" : success ? "SuccessBrush" : "TextSecondaryBrush");
    }

    private void OnRemove(object sender, RoutedEventArgs e) =>
        _tcs.TrySetResult(new SetupResult(Removed: true, null, null, RconClient.DefaultPort, null));

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void OnSave(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (!TryReadPort(out var port)) { Say("That port is not a number between 1 and 65535.", danger: true); return; }
        if (PasswordInput.Password.Length == 0)
        {
            Say("Without a password there is nothing to connect with — use “Remove console” to forget this server instead.",
                danger: true);
            return;
        }
        _testCts?.Cancel();
        _tcs.TrySetResult(new SetupResult(
            Removed: false,
            string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
            string.IsNullOrWhiteSpace(HostBox.Text) ? null : HostBox.Text.Trim(),
            port,
            PasswordInput.Password));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
