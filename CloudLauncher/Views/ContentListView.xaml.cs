using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>Which of the two kinds of row this is.</summary>
/// <remarks>Not shown to the user, but it decides what an operation on the row has to do. Derived
/// from <see cref="ContentRowState.Item"/> rather than set, so it can't disagree with the
/// row.</remarks>
public enum ContentRowClass
{
    /// <summary>A library item: the launcher keeps its own copy outside every instance. Only this
    /// kind can carry a rule or be placed into other instances.</summary>
    Library,

    /// <summary>One instance's own file. It can't be placed anywhere else until the launcher has
    /// its own copy.</summary>
    InstanceOwned
}

/// <summary>What, if anything, a row draws at its right edge for the page's on/off question.</summary>
/// <remarks>Set per page, not per row. <see cref="Toggle"/> is for independent bits (any number of
/// resource packs can be on); <see cref="Choice"/> is for a single slot (an instance loads one
/// shader pack). Both report through <see cref="ContentListHost.Toggled"/>.</remarks>
public enum ContentRowControl
{
    /// <summary>Nothing. For rows with no on/off of their own, such as world templates, which are
    /// copied once and then diverge.</summary>
    None,

    /// <summary>An on/off switch, for a page whose rows are independent bits.</summary>
    Toggle,

    /// <summary>A selection circle, for a page where the rows compete for one slot.</summary>
    Choice
}

public sealed record ContentRowState
{
    /// <summary>The row's identity for <see cref="ListDiff.Apply"/>: the library item's
    /// <see cref="LibraryItem.Key"/>, or for an instance's own file, a key that includes the
    /// instance id.</summary>
    public required string Key { get; init; }

    /// <summary>The name the user gave this, or the one the launcher derived.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The library item behind this row, or null for an instance's own file. Anything that
    /// needs an item (the "on by default" bit, the per-instance list, sharing) is only offered when
    /// this is set.</summary>
    public LibraryItem? Item { get; init; }

    /// <inheritdoc cref="ContentRowClass"/>
    public ContentRowClass RowClass =>
        Item is not null ? ContentRowClass.Library : ContentRowClass.InstanceOwned;

    /// <summary>The instance this row belongs to, for a row that is one instance's own file.</summary>
    public Guid? PackId { get; init; }

    /// <summary>Where the bytes are, for Reveal and for the identity checks.</summary>
    public string? Path { get; init; }

    /// <summary>The second line: file name, size, when it changed. Formatted by the page, with dates
    /// through <see cref="TimeFormat"/>.</summary>
    public string MetaLine { get; init; } = "";

    /// <summary>The rule in one line, e.g. "1.21.1 · NeoForge · 2 instances opted out". Empty, and
    /// hidden, for a row that is not a default.</summary>
    public string RulesLine { get; init; } = "";

    /// <summary>The coverage column, e.g. "on in 4 of 7". Empty hides it for this row.</summary>
    public string CoverageLine { get; init; } = "";

    /// <summary>The sentence behind the coverage column: how many instances hold it, how many have it
    /// switched on, how many the rules match.</summary>
    public string CoverageTip { get; init; } = "";

    /// <summary>Everything the row cannot fit, for the row tooltip.</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>One of the user's defaults: every compatible instance gets it.</summary>
    public bool IsDefault { get; init; }

    /// <summary>An instance has its own file of this name, which wins at launch, so the shared
    /// copy is inert there.</summary>
    public bool HasOwnCopy { get; init; }

    /// <inheritdoc cref="HasOwnCopy"/>
    public string OwnCopyTip { get; init; } = "";

    /// <summary>The user folder this is filed under, or empty. Shown as the pill's own text.</summary>
    public string FolderLabel { get; init; } = "";

    /// <inheritdoc cref="FolderLabel"/>
    public string FolderTip { get; init; } = "";

    /// <summary>"Keep on this machine": never uploaded when a shared instance syncs, or sitting in an
    /// instance's unsynced <c>local/</c> folder. Either way nobody else on that instance gets
    /// it.</summary>
    public bool IsLocalOnly { get; init; }

    /// <inheritdoc cref="IsLocalOnly"/>
    public string LocalTip { get; init; } = "";

    /// <summary>Present but switched off, or explicitly excluded, in the instance the page is scoped
    /// to.</summary>
    public bool OffHere { get; init; }

    /// <inheritdoc cref="OffHere"/>
    public string OffHereTip { get; init; } = "";

    /// <summary>Something must be installed before this does anything in game (the shaders page's
    /// "NEEDS IRIS"). The page supplies the label.</summary>
    public bool NeedsLoader { get; init; }

    /// <inheritdoc cref="NeedsLoader"/>
    public string LoaderLabel { get; init; } = "";

    /// <inheritdoc cref="NeedsLoader"/>
    public string LoaderTip { get; init; } = "";

    /// <summary>"ALWAYS ON · PAXI": the pack sits in a folder a mod loads in every world, not in the
    /// stack the switch edits. Empty hides it.</summary>
    public string AlwaysOnLabel { get; init; } = "";

    /// <inheritdoc cref="AlwaysOnLabel"/>
    public string AlwaysOnTip { get; init; } = "";

    /// <summary>An advisory the row shows but never acts on, e.g. "pack_format 32". Empty hides
    /// it.</summary>
    public string FormatLabel { get; init; } = "";

    /// <inheritdoc cref="FormatLabel"/>
    public string FormatTip { get; init; } = "";

    /// <summary>Where the row's switch stands. What it means is up to the page, and the tooltip says
    /// it.</summary>
    public bool ToggleOn { get; init; }

    /// <summary>What the switch will do, naming the thing it changes. Never empty, since this control
    /// doesn't know whether the switch is a policy or an options.txt line.</summary>
    public string ToggleTip { get; init; } = "";

    /// <summary>False greys the switch out: the game is running in that instance, or the row has
    /// nothing to switch.</summary>
    public bool CanToggle { get; init; } = true;

    /// <summary>False leaves the name inert (no hand cursor, accent or underline), as the mods list
    /// does for a jar with no page.</summary>
    public bool CanOpen { get; init; } = true;

    /// <summary>The item's own art if the page already has it, frozen because it was read on a worker
    /// thread. Null means not read yet, so <see cref="ContentRow.CopyFrom"/> never assigns a null over
    /// an icon on screen.</summary>
    public ImageSource? Icon { get; init; }
}

/// <summary>One row in <see cref="ContentListView"/>: a library default, or one instance's own
/// file.</summary>
/// <remarks>
/// <para>The state arrives as a whole through <see cref="CopyFrom"/>, so a rescan updates the row
/// on screen in one call. The icon arrives later and has its own notification.</para>
/// <para>No brushes in the row or its state: the theme hands out new brushes when the accent
/// changes, so every colour is a DataTrigger on one of the state's bools.</para>
/// </remarks>
public sealed class ContentRow : INotifyPropertyChanged
{
    private ContentRowState _state;
    private ImageSource? _icon;

    public ContentRow(ContentRowState state)
    {
        _state = state;
        _icon = state.Icon;
    }

    /// <summary>The facts this row is drawing. Replace it with <see cref="CopyFrom"/>.</summary>
    public ContentRowState State => _state;

    // ── identity ─────────────────────────────────────────────────────────────

    public string Key => _state.Key;
    public LibraryItem? Item => _state.Item;
    public Guid? PackId => _state.PackId;
    public string? Path => _state.Path;

    /// <summary>True for a row the library holds, the only kind that can carry a rule.</summary>
    public bool HasItem => _state.Item is not null;

    /// <inheritdoc cref="ContentRowClass"/>
    public ContentRowClass RowClass => _state.RowClass;

    // ── text ─────────────────────────────────────────────────────────────────

    public string DisplayName => _state.DisplayName;
    public string MetaLine => _state.MetaLine;
    public string RulesLine => _state.RulesLine;
    public string CoverageLine => _state.CoverageLine;
    public string CoverageTip => _state.CoverageTip;
    public string Tooltip => _state.Tooltip;

    /// <summary>The letter tile under the icon slot.</summary>
    public string Initial
    {
        get
        {
            var name = (_state.DisplayName ?? "").Trim();
            return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();
        }
    }

    // ── pills and switches ───────────────────────────────────────────────────

    public bool IsDefault => _state.IsDefault;
    public bool HasOwnCopy => _state.HasOwnCopy;
    public string OwnCopyTip => _state.OwnCopyTip;
    public string FolderLabel => _state.FolderLabel;
    public string FolderTip => _state.FolderTip;
    public bool IsLocalOnly => _state.IsLocalOnly;
    public string LocalTip => _state.LocalTip;
    public bool OffHere => _state.OffHere;
    public string OffHereTip => _state.OffHereTip;
    public bool NeedsLoader => _state.NeedsLoader;
    public string LoaderLabel => _state.LoaderLabel;
    public string LoaderTip => _state.LoaderTip;
    public string AlwaysOnLabel => _state.AlwaysOnLabel;
    public string AlwaysOnTip => _state.AlwaysOnTip;
    public string FormatLabel => _state.FormatLabel;
    public string FormatTip => _state.FormatTip;
    public bool ToggleOn => _state.ToggleOn;
    public string ToggleTip => _state.ToggleTip;
    public bool CanToggle => _state.CanToggle;
    public bool CanOpen => _state.CanOpen;

    /// <summary>The item's own art, once something has opened the file for it.</summary>
    public ImageSource? Icon => _icon;

    /// <summary>Folds a freshly scanned row into this one, so the row object (and with it the scroll
    /// anchor, selection and open breakdown) survives while the numbers change.</summary>
    public void CopyFrom(ContentRow fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        Apply(fresh._state);
        // Rescanned rows can arrive without an icon; overwriting the one on screen would flash the
        // letter tiles on every refresh.
        if (fresh._icon is not null) SetIcon(fresh._icon);
    }

    /// <inheritdoc cref="CopyFrom"/>
    public void Apply(ContentRowState fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (_state == fresh) return;
        _state = fresh;
        // One notification per bound property. Raising with an empty name would re-evaluate every
        // binding on every row, and this list can hold thousands of rows.
        Raise(nameof(DisplayName));
        Raise(nameof(Initial));
        Raise(nameof(MetaLine));
        Raise(nameof(RulesLine));
        Raise(nameof(CoverageLine));
        Raise(nameof(CoverageTip));
        Raise(nameof(Tooltip));
        Raise(nameof(IsDefault));
        Raise(nameof(HasOwnCopy));
        Raise(nameof(OwnCopyTip));
        Raise(nameof(FolderLabel));
        Raise(nameof(FolderTip));
        Raise(nameof(IsLocalOnly));
        Raise(nameof(LocalTip));
        Raise(nameof(OffHere));
        Raise(nameof(OffHereTip));
        Raise(nameof(NeedsLoader));
        Raise(nameof(LoaderLabel));
        Raise(nameof(LoaderTip));
        Raise(nameof(AlwaysOnLabel));
        Raise(nameof(AlwaysOnTip));
        Raise(nameof(FormatLabel));
        Raise(nameof(FormatTip));
        Raise(nameof(ToggleOn));
        Raise(nameof(ToggleTip));
        Raise(nameof(CanToggle));
        Raise(nameof(CanOpen));
        Raise(nameof(HasItem));
        Raise(nameof(RowClass));
    }

    /// <summary>The item's art, once a background read has it.</summary>
    public void SetIcon(ImageSource? icon)
    {
        if (ReferenceEquals(_icon, icon)) return;
        _icon = icon;
        Raise(nameof(Icon));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Everything <see cref="ContentListView"/> has to ask its page, in one object.</summary>
/// <remarks>Every callback is optional; a null one means the list doesn't offer that
/// gesture.</remarks>
public sealed class ContentListHost
{
    /// <summary>Clicking the row's name. Null leaves the name inert.</summary>
    public Action<ContentRow>? Open { get; init; }

    /// <summary>The row's switch was clicked; the bool is the requested state. The page does the
    /// write and re-reads. The switch is bound one-way, so it doesn't move until then.</summary>
    public Func<ContentRow, bool, Task>? Toggled { get; init; }

    /// <inheritdoc cref="ContentRowControl"/>
    public ContentRowControl RowControl { get; init; } = ContentRowControl.None;

    /// <summary>Builds the row's menu on demand. <c>full</c> is false for the short left-click menu
    /// and true for the right-click one, as in the mods list.</summary>
    public Func<ContentRow, bool, ContextMenu?>? Menu { get; init; }

    /// <summary>One line for the page's status bar. Optional.</summary>
    public Action<string>? Status { get; init; }
}

/// <summary>The library of defaults as a list of rows, in the style of the mods list.</summary>
/// <remarks>Shared by the Resource packs, Worlds and Shader packs pages; nothing here is specific to
/// a content kind. The page owns <see cref="Rows"/> and updates it in place with
/// <see cref="ListDiff.Apply"/> (or <see cref="PageRefresh"/>) so scroll position and selection
/// survive a refresh. Row menus are built on demand, since a menu in the item template would be
/// built for every row.</remarks>
public partial class ContentListView : UserControl
{
    public ContentListView()
    {
        InitializeComponent();
        RowList.ItemsSource = Rows;
    }

    /// <summary>The rows on screen. Update in place (see the class remarks).</summary>
    public ObservableCollection<ContentRow> Rows { get; } = new();

    /// <summary>The list the user is looking at, for <see cref="PageRefresh"/>'s scroll anchor.</summary>
    public ItemsControl ListControl => RowList;

    /// <summary>The scroller, for a page that hands its content element to
    /// <see cref="PageState"/>.</summary>
    public ScrollViewer ScrollHost => RowScroll;

    /// <summary>The row the user last clicked or acted on. Keyboard shortcuts use it, since rows
    /// don't take keyboard focus.</summary>
    public ContentRow? Focused { get; private set; }

    /// <summary>What the page answers for this list. Set once, before the first fill.</summary>
    public ContentListHost? Host
    {
        get => _host;
        set
        {
            _host = value;
            SetValue(ShowsRowToggleKey, value?.RowControl == ContentRowControl.Toggle);
            SetValue(ShowsRowChoiceKey, value?.RowControl == ContentRowControl.Choice);
        }
    }

    private ContentListHost? _host;

    private static readonly DependencyPropertyKey ShowsRowToggleKey =
        DependencyProperty.RegisterReadOnly(nameof(ShowsRowToggle), typeof(bool), typeof(ContentListView),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey ShowsRowChoiceKey =
        DependencyProperty.RegisterReadOnly(nameof(ShowsRowChoice), typeof(bool), typeof(ContentListView),
            new PropertyMetadata(false));

    /// <summary>Whether the row's right edge draws an on/off switch (see
    /// <see cref="ContentRowControl"/>).</summary>
    /// <remarks>Two bools instead of one enum because the row template uses triggers, and a trigger
    /// comparing an enum to a string fails silently. Both are set from <see cref="Host"/>.</remarks>
    public static readonly DependencyProperty ShowsRowToggleProperty = ShowsRowToggleKey.DependencyProperty;

    /// <inheritdoc cref="ShowsRowToggleProperty"/>
    public static readonly DependencyProperty ShowsRowChoiceProperty = ShowsRowChoiceKey.DependencyProperty;

    /// <inheritdoc cref="ShowsRowToggleProperty"/>
    public bool ShowsRowToggle => (bool)GetValue(ShowsRowToggleProperty);

    /// <inheritdoc cref="ShowsRowToggleProperty"/>
    public bool ShowsRowChoice => (bool)GetValue(ShowsRowChoiceProperty);

    /// <inheritdoc cref="Host"/>
    public void Configure(ContentListHost host) => Host = host;

    // ── row gestures ─────────────────────────────────────────────────────────

    /// <summary>A click anywhere on the row opens the item's own page.</summary>
    private void OnRowLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        // Otherwise clicking the switch or the "..." button would also open the page.
        if (InsideButton(e.OriginalSource)) return;
        Focused = row;
        if (!row.CanOpen || Host?.Open is null) return;
        Host.Open(row);
    }

    private void OnNameClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row || !row.CanOpen || Host?.Open is null) return;
        Focused = row;
        // Handled, so the row's own click does not open the same page a second time.
        e.Handled = true;
        Host.Open(row);
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        Focused = row;
        // The switch is bound one-way: reset it to the row's state and let the page's write move it.
        // SetCurrentValue rather than the IsChecked setter, which would replace the binding with a
        // local value and stop the switch following its row.
        if (sender is ToggleButton toggle)
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, row.ToggleOn);
        if (Host?.Toggled is { } toggled) await toggled(row, !row.ToggleOn);
    }

    private void OnOptionsClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        Focused = row;
        ShowMenu(row, full: false, sender as UIElement);
    }

    private void OnOptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        Focused = row;
        // Otherwise the event bubbles to the row's right-click handler and a second menu opens.
        e.Handled = true;
        ShowMenu(row, full: true, sender as UIElement);
    }

    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        Focused = row;
        e.Handled = true;
        ShowMenu(row, full: true, sender as UIElement);
    }

    private void ShowMenu(ContentRow row, bool full, UIElement? target)
    {
        if (Host?.Menu is not { } build) return;
        var menu = build(row, full);
        if (menu is null || menu.Items.Count == 0) return;
        menu.PlacementTarget = target ?? this;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static ContentRow? RowOf(object? sender) =>
        sender is FrameworkElement { DataContext: ContentRow row } ? row : null;

    private static bool InsideButton(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

}

/// <summary>The one answer to "will this item be put into that instance?".</summary>
/// <remarks>Matches <see cref="ContentDefaultsService.ReconcileAllAsync"/> (same
/// <see cref="ContentCompatibility.Matches"/> call and overrides), so counts, pages and the engine
/// agree. Precedence: a per-instance Forced or Excluded, then the item's "on by default" bit, then
/// its rules. <see cref="ContentDefaultPolicy.IncludePackIds"/> is still read for older items; the
/// first edit on the item's page replaces it with an exclude list.</remarks>
public static class ContentPlacement
{
    /// <inheritdoc cref="GoesInto(LibraryItem,PackSummary,InstanceContentOverrides?,ShaderLoader,out string)"/>
    public static bool GoesInto(LibraryItem item, PackSummary instance,
                                InstanceContentOverrides? overrides,
                                ShaderLoader loader = ShaderLoader.Iris) =>
        GoesInto(item, instance, overrides, loader, out _);

    /// <summary>True when the launcher will place <paramref name="item"/> in
    /// <paramref name="instance"/>.</summary>
    /// <param name="overrides">That instance's own answers, or null when they could not be read
    /// (treated as no answers).</param>
    /// <param name="loader">What the instance can load shaders with. Only consulted for shader packs,
    /// so pass <see cref="ShaderLoader.Iris"/> for other kinds rather than reading it from
    /// disk.</param>
    /// <param name="why">A sentence for the UI, filled in either way. Never empty.</param>
    public static bool GoesInto(LibraryItem item, PackSummary instance,
                                InstanceContentOverrides? overrides, ShaderLoader loader,
                                out string why)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(instance);

        if (overrides is not null)
        {
            if (overrides.IsForced(item.Key))
            {
                why = $"Always in {instance.Name} - you said so on this item's page.";
                return true;
            }
            if (overrides.IsExcluded(item.Key))
            {
                why = $"Never in {instance.Name} - you said so on this item's page.";
                return false;
            }
            if (overrides.OptOutOfAllDefaults)
            {
                why = $"{instance.Name} takes none of your defaults.";
                return false;
            }
        }

        var policy = item.Defaults;
        if (policy is null || !policy.Enabled)
        {
            why = "Switched off, so nothing places it anywhere.";
            return false;
        }

        return ContentCompatibility.Matches(policy, instance, item, loader, out why,
                                            ContentFolderRules.EffectiveFor(item));
    }

    /// <summary>How many of these instances it will be put into.</summary>
    /// <remarks>Uses the same call as the row tooltip and the item page, so the counts always
    /// agree.</remarks>
    public static int Reach(LibraryItem item, IReadOnlyList<PackSummary> instances,
                            IReadOnlyDictionary<Guid, InstanceContentOverrides> overrides,
                            Func<PackSummary, ShaderLoader>? loaderOf = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(overrides);
        return instances.Count(p => GoesInto(
            item, p, overrides.GetValueOrDefault(p.Id),
            loaderOf?.Invoke(p) ?? ShaderLoader.Iris));
    }

    /// <summary>Whether the item's own page should draw this instance's box ticked.</summary>
    /// <remarks>Unlike <see cref="GoesInto"/>, this ignores whether the item is switched on, so
    /// switching it off doesn't clear the user's ticks.</remarks>
    public static bool IsTicked(LibraryItem item, PackSummary instance,
                                InstanceContentOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(instance);

        if (overrides is not null)
        {
            if (overrides.IsForced(item.Key)) return true;
            if (overrides.IsExcluded(item.Key)) return false;
        }
        var policy = item.Defaults;
        if (policy is null) return true;                            // no rule: everywhere
        if (policy.ExcludePackIds.Contains(instance.Id)) return false;
        if (policy.IncludePackIds.Count > 0) return policy.IncludePackIds.Contains(instance.Id);
        return true;
    }

    /// <summary>Writes the item page's per-instance list back as one whole answer.</summary>
    /// <remarks>Normalises older items that mix include lists, exclude lists and per-instance
    /// overrides: afterwards there is only an exclude list of the unticked instances (so new instances
    /// are covered), and this item's overrides are back to Follow. The ticks shown are preserved.</remarks>
    /// <param name="ticked">The instances that stay in. Everything else in
    /// <paramref name="instances"/> is written out.</param>
    public static void SetTicked(LibraryItem item, IReadOnlyList<PackSummary> instances,
                                 IReadOnlyCollection<Guid> ticked)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(ticked);

        var policy = App.State.Library.GetDefaults(item.Key);
        policy.IncludePackIds.Clear();
        policy.ExcludePackIds.Clear();
        foreach (var instance in instances)
            if (!ticked.Contains(instance.Id)) policy.ExcludePackIds.Add(instance.Id);
        App.State.Library.SetDefaults(item.Key, policy);

        // Reset this item's per-instance overrides to Follow so they can't outrank what was just
        // written. Other items' overrides are left alone.
        foreach (var instance in instances)
        {
            try
            {
                var overrides = App.State.ContentDefaults.OverridesFor(instance.Id);
                if (overrides.ChoiceFor(item.Key) != ContentDefaultChoice.Follow)
                    App.State.ContentDefaults.SetChoice(instance.Id, item.Key, ContentDefaultChoice.Follow);
            }
            catch (Exception ex) { AppLog.LogError("content-placement.set-ticked", ex); }
        }
    }
}

/// <summary>The menu helpers the three content pages share.</summary>
/// <remarks>The same helpers <see cref="ModOptionsMenu"/> keeps private (glyph gutter, check mark
/// column, 230px minimum width, a header naming the subject), made public so the pages don't drift
/// apart. Each page decides what goes in its menus.</remarks>
public static class ContentMenu
{
    /// <summary>An ordinary command.</summary>
    /// <param name="gesture">The right-hand column: a shortcut, a check mark, or a short reason the
    /// item is greyed out, so a disabled item explains itself before it is clicked.</param>
    public static MenuItem Item(string header, Action onClick, object? icon = null,
                                string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (icon is not null) mi.Icon = icon;
        if (gesture is not null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>A submenu's parent.</summary>
    public static MenuItem Parent(string header, object? icon = null, string? gesture = null)
    {
        var mi = new MenuItem { Header = header };
        if (icon is not null) mi.Icon = icon;
        if (gesture is not null) mi.InputGestureText = gesture;
        return mi;
    }

    /// <summary>A checkable item that stays open, so several can be flipped in a row. Its check mark
    /// follows what <paramref name="toggle"/> returns.</summary>
    public static MenuItem Toggle(string header, bool active, Func<bool> toggle, object? icon = null)
    {
        var mi = new MenuItem { Header = header, StaysOpenOnClick = true, InputGestureText = active ? "✓" : "" };
        if (icon is not null) mi.Icon = icon;
        mi.Click += (_, _) => mi.InputGestureText = toggle() ? "✓" : "";
        return mi;
    }

    /// <summary>One of a set of alternatives.</summary>
    public static MenuItem Radio(string header, bool active, Action onClick, string? tip = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = active ? "✓" : "" };
        if (tip is not null) mi.ToolTip = tip;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>The inert two-line header that names what the menu is about.</summary>
    public static MenuItem Header(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(2) };
        var name = new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, MaxWidth = 250,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(name);

        if (subtitle.Length > 0)
        {
            var under = new TextBlock { Text = subtitle, FontSize = 10, MaxWidth = 250, TextWrapping = TextWrapping.Wrap };
            under.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(under);
        }
        return new MenuItem { Header = panel, IsHitTestVisible = false, Focusable = false };
    }

    /// <summary>A fixed-width icon cell from an MDL2 glyph, or an aligned blank for 0.</summary>
    /// <remarks>A resource reference rather than a brush from the theme dictionary, so a menu built
    /// before a theme change still repaints.</remarks>
    public static TextBlock Glyph(int code)
    {
        var glyph = new TextBlock
        {
            Text = code <= 0 ? "" : char.ConvertFromUtf32(code),
            Width = 16,
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return glyph;
    }

    /// <summary>A menu with the standard minimum width, already carrying its header and
    /// separator.</summary>
    public static ContextMenu New(string title, string subtitle)
    {
        var menu = new ContextMenu { MinWidth = 230 };
        menu.Items.Add(Header(title, subtitle));
        menu.Items.Add(new Separator());
        return menu;
    }

    /// <summary>Opens a second menu where the first one stood, once the first has gone away.</summary>
    /// <remarks>Deferred because opening the second popup inline puts it behind the closing (topmost)
    /// menu, which also keeps keyboard focus.</remarks>
    public static void ShowInstead(ContextMenu closing, Func<ContextMenu> build)
    {
        var target = closing.PlacementTarget;
        var placement = closing.Placement;
        Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                var menu = build();
                menu.PlacementTarget = target;
                menu.Placement = placement;
                menu.IsOpen = true;
            }));
    }
}




