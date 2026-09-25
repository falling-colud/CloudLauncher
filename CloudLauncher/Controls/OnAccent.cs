using System.Windows;

namespace CloudLauncher.Controls;

/// <summary>Marks a subtree as sitting on an accent fill, so the text inside it takes
/// <c>TextOnAccentBrush</c> (see the app-wide TextBlock style in Controls.xaml).</summary>
/// <remarks>An inherited attached property is the only thing that reaches every piece of text in a
/// button. The app-wide TextBlock style outranks the colour a button hands down, and a string label
/// is a TextBlock inside the ContentPresenter's AccessText, whose implicit style lookup skips the
/// button. The TextBlock style triggers off this value. Needed for light accents, where
/// TextPrimary isn't readable.</remarks>
public static class OnAccent
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(OnAccent),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsOn(DependencyObject o) => (bool)o.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject o, bool value) => o.SetValue(IsOnProperty, value);
}
