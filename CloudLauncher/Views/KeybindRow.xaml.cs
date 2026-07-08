using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// A label + clickable key button. Click to enter "press any key" capture mode;
/// the next KeyDown becomes the bound key (Minecraft key.* format).
/// </summary>
public partial class KeybindRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(KeybindRow),
        new PropertyMetadata("", (d, e) => ((KeybindRow)d).LabelText.Text = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private string _bound = "key.keyboard.unknown";
    private bool _capturing;

    public event EventHandler? BoundChanged;

    public string Bound
    {
        get => _bound;
        set
        {
            if (_bound == value)
            {
                KeyDisplay.Text = LauncherKeybinds.PrettyName(value);
                return;
            }

            _bound = value;
            KeyDisplay.Text = LauncherKeybinds.PrettyName(value);
            BoundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public KeybindRow()
    {
        InitializeComponent();
    }

    private void OnKeyButtonClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        KeyDisplay.Text = "Press a key…";
        KeyButton.Background = (Brush)FindResource("AccentSoftBrush");
        Keyboard.Focus(KeyButton);
        KeyButton.PreviewKeyDown += OnPreviewKeyDown;
        KeyButton.PreviewMouseDown += OnPreviewMouseDown;
        // Lose focus → cancel
        KeyButton.LostKeyboardFocus += OnLostFocus;
    }

    private void EndCapture()
    {
        _capturing = false;
        KeyButton.Background = null;
        KeyButton.PreviewKeyDown -= OnPreviewKeyDown;
        KeyButton.PreviewMouseDown -= OnPreviewMouseDown;
        KeyButton.LostKeyboardFocus -= OnLostFocus;
        KeyDisplay.Text = LauncherKeybinds.PrettyName(_bound);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_capturing) EndCapture();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        if (e.Key == Key.Escape) { EndCapture(); e.Handled = true; return; }

        var mc = LauncherKeybinds.WpfKeyToMinecraft(e.Key);
        if (mc is not null)
        {
            Bound = mc;
            EndCapture();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing) return;
        var mc = e.ChangedButton switch
        {
            MouseButton.Left   => "key.mouse.left",
            MouseButton.Right  => "key.mouse.right",
            MouseButton.Middle => "key.mouse.middle",
            MouseButton.XButton1 => "key.mouse.4",
            MouseButton.XButton2 => "key.mouse.5",
            _ => null
        };
        if (mc is not null)
        {
            Bound = mc;
            EndCapture();
            e.Handled = true;
        }
    }

}
