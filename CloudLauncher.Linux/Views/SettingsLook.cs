using Avalonia;
using Avalonia.Controls;

namespace CloudLauncher.Views;

/// <summary>
/// Disabled controls in the Slate look, for the Account and Settings pages and their cards. The Fluent
/// theme's own disabled brushes are a light grey that stands out brighter than the enabled controls on
/// the Slate surfaces; these point them at the Slate surfaces and the disabled text colour instead.
/// </summary>
/// <remarks>Set on the page (or card) itself, so it reaches only what is inside it. The same keys in
/// Themes/Slate.axaml would give every page this look.</remarks>
public static class SettingsLook
{
    private static readonly (string Fluent, string Slate)[] DisabledBrushes =
    [
        ("ButtonBackgroundDisabled", "Surface2Brush"),
        ("ButtonBorderBrushDisabled", "BorderSubtleBrush"),
        ("ButtonForegroundDisabled", "TextDisabledBrush"),
        ("TextControlBackgroundDisabled", "Surface1Brush"),
        ("TextControlBorderBrushDisabled", "BorderSubtleBrush"),
        ("TextControlForegroundDisabled", "TextDisabledBrush"),
        ("TextControlPlaceholderForegroundDisabled", "TextDisabledBrush"),
        ("ComboBoxBackgroundDisabled", "Surface1Brush"),
        ("ComboBoxBorderBrushDisabled", "BorderSubtleBrush"),
        ("ComboBoxForegroundDisabled", "TextDisabledBrush"),
        ("ComboBoxDropDownGlyphForegroundDisabled", "TextDisabledBrush"),
        ("CheckBoxForegroundCheckedDisabled", "TextDisabledBrush"),
        ("CheckBoxForegroundUncheckedDisabled", "TextDisabledBrush"),
        ("CheckBoxCheckBackgroundFillCheckedDisabled", "Surface4Brush"),
        ("CheckBoxCheckBackgroundStrokeCheckedDisabled", "BorderBrush"),
        ("CheckBoxCheckGlyphForegroundCheckedDisabled", "TextDisabledBrush"),
        ("CheckBoxCheckBackgroundStrokeUncheckedDisabled", "BorderBrush"),
    ];

    /// <summary>Gives the controls inside <paramref name="root"/> the Slate disabled look.</summary>
    public static void UseSlateDisabledLook(Control root)
    {
        if (Application.Current is not { } app) return;
        foreach (var (fluent, slate) in DisabledBrushes)
            if (app.TryFindResource(slate, out var brush) && brush is not null)
                root.Resources[fluent] = brush;
    }
}
