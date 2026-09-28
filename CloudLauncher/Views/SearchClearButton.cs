using System.Windows;
using System.Windows.Controls;

namespace CloudLauncher.Views;

/// <summary>The small X at the right of a search box that empties it. Shown only while
/// <see cref="Target"/> has text.</summary>
/// <remarks>
/// A click clears the box, puts the caret back in it and raises <see cref="Cleared"/>. A page whose
/// search waits out a debounce, or only runs on Enter, handles that to run the empty search (its
/// default listing) straight away, the same way its Escape key does. <see cref="CompactSearchBox"/>
/// carries one, so most pages get it without doing anything; place this by hand only inside a search
/// box built from a plain TextBox.
/// <para>Not focusable: taking focus from the box would collapse a CompactSearchBox, and the caret
/// belongs in the box anyway.</para>
/// </remarks>
public sealed class SearchClearButton : Button
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(TextBox), typeof(SearchClearButton),
        new PropertyMetadata(null, OnTargetChanged));

    /// <summary>The search box this button empties.</summary>
    public TextBox? Target
    {
        get => (TextBox?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>The box was emptied by this button. The caret is already back in it.</summary>
    public event EventHandler? Cleared;

    public SearchClearButton()
    {
        SetResourceReference(StyleProperty, "SearchClearButton");
        Focusable = false;
        IsTabStop = false;
        ToolTip = "Clear search (Esc)";
        Visibility = Visibility.Collapsed;
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (Target is not { } box) return;
        box.Clear();
        box.Focus();
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (SearchClearButton)d;
        if (e.OldValue is TextBox old)
        {
            old.TextChanged -= button.OnTargetTextChanged;
            old.IsVisibleChanged -= button.OnTargetVisibleChanged;
        }
        if (e.NewValue is TextBox box)
        {
            box.TextChanged += button.OnTargetTextChanged;
            box.IsVisibleChanged += button.OnTargetVisibleChanged;
        }
        button.UpdateVisibility();
    }

    private void OnTargetTextChanged(object sender, TextChangedEventArgs e) => UpdateVisibility();

    private void OnTargetVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateVisibility();

    /// <summary>Visible while the box shows text. A collapsed CompactSearchBox hides its box, and the X
    /// goes with it.</summary>
    private void UpdateVisibility() =>
        Visibility = Target is { Text.Length: > 0, Visibility: Visibility.Visible }
            ? Visibility.Visible
            : Visibility.Collapsed;
}
