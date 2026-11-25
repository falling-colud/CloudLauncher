using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// One instance on the Instances page: its cover, name, version and owner, and Play. While a transfer
/// runs for it (a modpack import, an update) the bottom row shows its progress with Pause and Stop;
/// while the game runs, Play turns into Stop. The Windows launcher's instance card (PackListView's
/// PackCardTemplate).
/// </summary>
/// <remarks>Built in code and kept for the page's lifetime, one per instance, so progress and state
/// land on the card on screen (the page looks cards up by instance id). The page's styles paint it
/// (class <c>instanceCard</c>).</remarks>
public sealed class InstanceCard : Border
{
    public const double CardHeight = 252;

    private readonly Action<InstanceCard> _open;
    private readonly Action<InstanceCard> _play;
    private readonly Action<InstanceCard> _stop;
    private readonly Func<InstanceCard, MenuFlyout> _menu;

    private readonly Panel _cover;
    private readonly Image _image;
    private readonly TextBlock _letter;
    private readonly TextBlock _pin;
    private readonly Border _tagChip;
    private readonly TextBlock _tagText;
    private readonly TextBlock _loaderText;
    private readonly TextBlock _name;
    private readonly TextBlock _version;
    private readonly TextBlock _owner;
    private readonly Button _more;
    private readonly Button _playButton;
    private readonly Button _stopButton;
    private readonly Grid _normalRow;
    private readonly Grid _progressRow;
    private readonly TextBlock _progressLabel;
    private readonly TextBlock _progressPercent;
    private readonly ProgressBar _progressBar;
    private readonly Button _pauseJob;
    private readonly Button _stopJob;

    private ProgressInfo? _progress;
    private MinecraftInstanceStatus _status = MinecraftInstanceStatus.Idle;
    private bool _pressed;

    public InstanceCard(PackSummary pack, Action<InstanceCard> open, Action<InstanceCard> play,
        Action<InstanceCard> stop, Func<InstanceCard, MenuFlyout> menu)
    {
        Pack = pack;
        _open = open;
        _play = play;
        _stop = stop;
        _menu = menu;

        Classes.Add("instanceCard");
        Height = CardHeight;
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        // ── cover ──
        _cover = InstanceArt.Cover(pack.Id, pack.Name, 60, out _image, out _letter);
        _pin = new TextBlock
        {
            Text = "\uE718",
            FontSize = 13,
            Margin = new Thickness(12, 11, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = Brushes.White,
            IsVisible = false
        };
        _pin.Classes.Add("icon");
        ToolTip.SetTip(_pin, "Pinned");
        _tagChip = InstanceArt.Chip("", Brushes.White);
        _tagText = (TextBlock)_tagChip.Child!;
        _tagChip.Margin = new Thickness(0, 10, 10, 0);
        _tagChip.HorizontalAlignment = HorizontalAlignment.Right;
        _tagChip.VerticalAlignment = VerticalAlignment.Top;
        var loaderChip = InstanceArt.Chip("", Brushes.White, new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)));
        _loaderText = (TextBlock)loaderChip.Child!;
        loaderChip.Margin = new Thickness(10, 0, 0, 10);
        loaderChip.HorizontalAlignment = HorizontalAlignment.Left;
        loaderChip.VerticalAlignment = VerticalAlignment.Bottom;
        _cover.Children.Add(_pin);
        _cover.Children.Add(_tagChip);
        _cover.Children.Add(loaderChip);
        var coverBox = new Border
        {
            Height = 140,
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            ClipToBounds = true,
            Child = _cover
        };

        // ── name, version ──
        _name = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _version = new TextBlock { Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _version.Classes.Add("caption");

        // ── owner, menu, Play / Stop ──
        _owner = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _owner.Classes.Add("caption");
        _more = new Button { Content = "\uE712", Width = 30, Height = 30, Margin = new Thickness(4, 0, 4, 0) };
        _more.Classes.Add("icon");
        ToolTip.SetTip(_more, "More");
        _more.Click += (_, _) => ShowMenu(_more, atPointer: false);

        _playButton = ActionButton("\uE768", "Play", "AccentTextBrush");
        _playButton.Click += (_, _) => _play(this);
        _stopButton = ActionButton("\uE711", "Stop", "DangerBrush");
        _stopButton.Click += (_, _) => _stop(this);
        ToolTip.SetTip(_stopButton, "Stop Minecraft");
        var actionSlot = new Panel { Children = { _playButton, _stopButton }, VerticalAlignment = VerticalAlignment.Center };

        _normalRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _normalRow.Children.Add(_owner);
        Grid.SetColumn(_more, 1);
        _normalRow.Children.Add(_more);
        Grid.SetColumn(actionSlot, 2);
        _normalRow.Children.Add(actionSlot);

        // ── transfer progress, in place of the row above ──
        _progressLabel = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _progressLabel.Classes.Add("caption");
        _progressPercent = new TextBlock { Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        _progressPercent.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AccentTextBrush"));
        _pauseJob = SmallIconButton("\uE769");
        _pauseJob.Click += (_, _) => TogglePause();
        _stopJob = SmallIconButton("\uE711");
        _stopJob.Click += (_, _) => StopJob();
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            MinWidth = 0,   // the theme's 200 would overhang a card's narrow column
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // The label and its percentage on top; the bar with Pause and Stop beside it under them.
        _progressRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            IsVisible = false
        };
        _progressRow.Children.Add(_progressLabel);
        _progressPercent.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_progressPercent, 1);
        Grid.SetColumnSpan(_progressPercent, 2);
        _progressRow.Children.Add(_progressPercent);
        Grid.SetRow(_progressBar, 1);
        _progressRow.Children.Add(_progressBar);
        Grid.SetRow(_pauseJob, 1);
        Grid.SetColumn(_pauseJob, 1);
        _progressRow.Children.Add(_pauseJob);
        Grid.SetRow(_stopJob, 1);
        Grid.SetColumn(_stopJob, 2);
        _progressRow.Children.Add(_stopJob);

        var bottom = new Panel { Children = { _normalRow, _progressRow }, VerticalAlignment = VerticalAlignment.Bottom, MinHeight = 30 };
        var info = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(14, 11, 10, 11) };
        info.Children.Add(_name);
        Grid.SetRow(_version, 1);
        info.Children.Add(_version);
        Grid.SetRow(bottom, 2);
        info.Children.Add(bottom);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(coverBox);
        Grid.SetRow(info, 1);
        root.Children.Add(info);
        Child = root;

        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _pressed = false;
        ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(this, atPointer: true);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && e.Source == this)
            {
                e.Handled = true;
                _open(this);
            }
        };

        Show(pack);
        SetStatus(App.State.Instances.GetStatus(pack.Id));
    }

    /// <summary>Styled as a Border, so the page's <c>Border.instanceCard</c> styles reach it.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public PackSummary Pack { get; private set; }

    public Guid Id => Pack.Id;

    /// <summary>Points the card at a newer summary of the same instance, keeping its progress.</summary>
    public void Rebind(PackSummary pack)
    {
        if (ReferenceEquals(Pack, pack)) return;
        var renamed = pack.Name != Pack.Name;
        Pack = pack;
        Show(pack);
        if (renamed) RefreshCover();
    }

    /// <summary>Reads the cover again (the icon changed, or the name its colour comes from).</summary>
    public void RefreshCover()
    {
        _cover.Background = InstanceArt.CoverBrush(Pack.Name);
        _letter.Text = InstanceArt.Initial(Pack.Name);
        InstanceArt.SetIcon(_image, _letter, InstanceArt.Icon(Pack.Id));
    }

    /// <summary>Pinned instances sort first and wear a pin.</summary>
    public void RefreshUsage() => _pin.IsVisible = App.State.Settings.GetPackUsage(Pack.Id).IsPinned;

    public void SetStatus(MinecraftInstanceStatus status)
    {
        _status = status;
        UpdateActions();
    }

    public void SetProgress(ProgressInfo? info)
    {
        _progress = info;
        UpdateActions();
    }

    /// <summary>A transfer for this instance started, paused, resumed or ended.</summary>
    public void RefreshJob() => UpdateActions();

    private void Show(PackSummary pack)
    {
        _name.Text = pack.Name;
        ToolTip.SetTip(_name, pack.Name);
        _version.Text = InstanceArt.VersionLine(pack);
        _owner.Text = InstanceArt.Owner(pack.Id, pack.OwnerUsername);
        var tag = InstanceArt.Tag(pack.IsEmpty, pack.IsShared, pack.Visibility);
        _tagText.Text = InstanceArt.TagText(tag);
        _tagText.Foreground = InstanceArt.TagForeground(tag);
        _loaderText.Text = InstanceArt.LoaderBadge(pack.IsEmpty, pack.Loader);
        _playButton.IsEnabled = !pack.IsEmpty;
        ToolTip.SetTip(_playButton, pack.IsEmpty ? "This instance is empty, so there is nothing to play" : null);
        RefreshUsage();
    }

    private void UpdateActions()
    {
        var busy = _status != MinecraftInstanceStatus.Idle;
        var job = PackJobs.For(Pack.Id);
        // An export only reads the instance: it has its own card and leaves Play alone.
        var transfer = job is { Kind: not PackJobKind.Export } ? job : null;
        var working = !busy && (_progress is not null || transfer is not null);

        _normalRow.IsVisible = !working;
        _progressRow.IsVisible = working;
        _playButton.IsVisible = !busy;
        _stopButton.IsVisible = busy;
        ToolTip.SetTip(_stopButton, _status == MinecraftInstanceStatus.Launching ? "Cancel the launch" : "Stop Minecraft");
        if (!working) return;

        var label = _progress?.Label is { Length: > 0 } l ? l
            : transfer is not null ? char.ToUpperInvariant(transfer.KindLabel[0]) + transfer.KindLabel[1..] + "..."
            : "Working...";
        var paused = transfer is { IsPaused: true };
        _progressLabel.Text = paused ? "Paused" : label;
        ToolTip.SetTip(_progressRow, paused ? "Paused: " + label : label);

        var fraction = _progress?.Fraction ?? -1;
        _progressBar.IsIndeterminate = fraction < 0 && !paused;
        _progressBar.Value = fraction < 0 ? 0 : fraction * 100;
        _progressPercent.Text = fraction < 0 ? "" : $"{(int)(fraction * 100)}%";

        _pauseJob.IsVisible = transfer is not null;
        _stopJob.IsVisible = transfer is not null;
        if (transfer is null) return;
        _pauseJob.Content = paused ? "\uE768" : "\uE769";
        _pauseJob.IsEnabled = !transfer.IsStopping;
        _stopJob.IsEnabled = !transfer.IsStopping;
        ToolTip.SetTip(_pauseJob, paused ? $"Resume the {transfer.KindLabel}" : $"Pause the {transfer.KindLabel}");
        ToolTip.SetTip(_stopJob, $"Stop the {transfer.KindLabel} and remove the files it downloaded");
    }

    private void TogglePause()
    {
        if (PackJobs.For(Pack.Id) is not { IsStopping: false } job) return;
        if (job.IsPaused) job.Resume();
        else job.Pause();
    }

    private void StopJob() => PackJobs.For(Pack.Id)?.Stop();

    public void ShowMenu(Control target, bool atPointer)
    {
        try { _menu(this).ShowAt(target, atPointer); }
        catch (Exception ex) { AppLog.LogError("instances.menu", ex); }
    }

    // ── click to open ──

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Buttons on the card handle their own presses, so a press that gets here is on the card.
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsInsideButton(e.Source);
        if (_pressed) Focus();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed || e.InitialPressMouseButton != MouseButton.Left) return;
        _pressed = false;
        var at = e.GetPosition(this);
        if (at.X < 0 || at.Y < 0 || at.X > Bounds.Width || at.Y > Bounds.Height) return;
        e.Handled = true;
        _open(this);
    }

    private static bool IsInsideButton(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button) return true;
            if (v is InstanceCard) return false;
        }
        return false;
    }

    // ── little builders ──

    private static Button ActionButton(string glyph, string text, string glyphBrush)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 11, Margin = new Thickness(0, 0, 7, 0) };
        icon.Classes.Add("icon");
        var label = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Padding = new Thickness(13, 5),
            MinHeight = 30,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } }
        };
        icon.Bind(TextBlock.ForegroundProperty, button.GetResourceObservable(glyphBrush));
        if (glyphBrush == "DangerBrush")
            label.Bind(TextBlock.ForegroundProperty, button.GetResourceObservable(glyphBrush));
        return button;
    }

    private static Button SmallIconButton(string glyph)
    {
        var b = new Button { Content = glyph, Width = 24, Height = 24, FontSize = 11, Margin = new Thickness(2, 0, 0, 0) };
        b.Classes.Add("icon");
        return b;
    }
}
