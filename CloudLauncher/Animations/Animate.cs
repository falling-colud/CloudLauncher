using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Animations;

/// <summary>
/// Attached behaviours that add motion to the existing styles without rewriting control templates.
/// Everything is opt-in through attached properties, so one style setter or XAML attribute turns an
/// animation on app-wide.
/// </summary>
/// <remarks>
/// Only <see cref="UIElement.Opacity"/> and <see cref="UIElement.RenderTransform"/> are animated:
/// brushes from <c>StaticResource</c> are frozen and would throw, so colour changes are left to the
/// template triggers. Animations are short (under ~300 ms) and ease out.
/// </remarks>
public static class Animate
{
    // Shared easing functions, created once and frozen.
    private static readonly IEasingFunction EaseOut =
        Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseOutBack =
        Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

    private static IEasingFunction Freeze(Freezable f) { f.Freeze(); return (IEasingFunction)f; }

    /// <summary>A duration after the look's motion setting (Slate: 0 = off, 2 = half speed).</summary>
    private static TimeSpan Dur(double ms) => TimeSpan.FromMilliseconds(LookState.Current.Ms(ms));

    /// <summary>The Slate style moves in whole pixels: a press nudges down one pixel instead of
    /// shrinking, a hover lifts instead of growing, and nothing overshoots. Scaling pixel art by 2-4%
    /// smears its edges across two screen pixels.</summary>
    private static bool Pixel => LookState.Current.IsSlate;

    // ── Press feedback: buttons rise slightly on hover and dip on press ──

    public static readonly DependencyProperty PressFeedbackProperty =
        DependencyProperty.RegisterAttached("PressFeedback", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnPressFeedbackChanged));

    public static bool GetPressFeedback(DependencyObject o) => (bool)o.GetValue(PressFeedbackProperty);
    public static void SetPressFeedback(DependencyObject o, bool v) => o.SetValue(PressFeedbackProperty, v);

    private const double HoverLiftPx = -2.0;   // buttons rise slightly on hover...
    private const double PressScale  = 0.96;    // ...and shrink slightly on press

    private static void OnPressFeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.MouseEnter                   += Press_Enter;
            fe.MouseLeave                   += Press_Leave;
            fe.PreviewMouseLeftButtonDown   += Press_Down;
            fe.PreviewMouseLeftButtonUp     += Press_Up;
        }
        else
        {
            fe.MouseEnter                   -= Press_Enter;
            fe.MouseLeave                   -= Press_Leave;
            fe.PreviewMouseLeftButtonDown   -= Press_Down;
            fe.PreviewMouseLeftButtonUp     -= Press_Up;
        }
    }

    private static void Press_Enter(object s, RoutedEventArgs e) => LiftTo((FrameworkElement)s, Pixel ? 0 : HoverLiftPx, 140);
    private static void Press_Leave(object s, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)s;
        LiftTo(fe, 0, 180);
        DipTo(fe, 1.0, 160);
    }
    private static void Press_Down(object s, RoutedEventArgs e)
    {
        if (Pixel) LiftTo((FrameworkElement)s, 1, 60);
        else DipTo((FrameworkElement)s, PressScale, 90);
    }

    private static void Press_Up(object s, RoutedEventArgs e)
    {
        if (Pixel) LiftTo((FrameworkElement)s, 0, 90);
        else DipTo((FrameworkElement)s, 1.0, 150);
    }

    /// <summary>Vertical hover-lift on a button's transform group (does not blur text).</summary>
    private static void LiftTo(FrameworkElement fe, double toY, double ms)
    {
        var (_, t) = EnsureButtonTransform(fe);
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(toY, Dur(ms)) { EasingFunction = EaseOut });
    }

    /// <summary>Brief press-dip scale on a button's transform group.</summary>
    private static void DipTo(FrameworkElement fe, double to, double ms)
    {
        var (sc, _) = EnsureButtonTransform(fe);
        var a = new DoubleAnimation(to, Dur(ms)) { EasingFunction = EaseOut };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a.Clone());
    }

    /// <summary>A centred Scale+Translate group so hover-lift and press-dip compose cleanly.</summary>
    private static (ScaleTransform scale, TranslateTransform translate) EnsureButtonTransform(FrameworkElement fe)
    {
        if (fe.RenderTransform is TransformGroup g && !g.IsFrozen && g.Children.Count == 2
            && g.Children[0] is ScaleTransform es && g.Children[1] is TranslateTransform et)
            return (es, et);
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        fe.RenderTransform = group;
        return (scale, translate);
    }

    // ── Hover lift: cards and tiles scale up slightly while hovered ──

    public static readonly DependencyProperty HoverLiftProperty =
        DependencyProperty.RegisterAttached("HoverLift", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject o) => (bool)o.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject o, bool v) => o.SetValue(HoverLiftProperty, v);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.MouseEnter += Lift_Enter;
            fe.MouseLeave += Lift_Leave;
        }
        else
        {
            fe.MouseEnter -= Lift_Enter;
            fe.MouseLeave -= Lift_Leave;
        }
    }

    // The element currently lifted. Weak, since list rows get recycled and shouldn't be kept alive
    // for an animation.
    private static WeakReference<FrameworkElement>? _lifted;

    private static void Lift_Enter(object s, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)s;
        // While scrolling, rows pass under a still cursor and each raises MouseEnter. Skip the lift: a
        // fractional scale knocks the text off the pixel grid and re-rasterizes ClearType every frame.
        if (IsAncestorScrolling(fe)) return;
        _lifted = new WeakReference<FrameworkElement>(fe);
        if (Pixel) ShiftTo(fe, -2, 120);
        else ScaleTo(fe, 1.02, 160);
    }

    private static void Lift_Leave(object s, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)s;
        if (_lifted is not null && _lifted.TryGetTarget(out var current) && ReferenceEquals(current, fe))
            _lifted = null;
        if (IsAncestorScrolling(fe)) { SnapScaleToRest(fe); return; }
        if (fe.RenderTransform is TranslateTransform) ShiftTo(fe, 0, 160);
        else ScaleTo(fe, 1.0, 200);
    }

    /// <summary>Drops the lift without animating, for when a scroll starts.</summary>
    private static void SnapScaleToRest(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform { IsFrozen: false } shift)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = 0;
            return;
        }
        if (fe.RenderTransform is not ScaleTransform st || st.IsFrozen) return;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = 1;
        st.ScaleY = 1;
    }

    private static void SnapLiftedToRest()
    {
        if (_lifted is null) return;
        if (_lifted.TryGetTarget(out var fe)) SnapScaleToRest(fe);
        _lifted = null;
    }

    /// <summary>Animate a uniform scale on the element's render transform (origin centred).</summary>
    private static void ScaleTo(FrameworkElement fe, double to, double ms)
    {
        var st = EnsureScale(fe);
        var anim = new DoubleAnimation(to, Dur(ms)) { EasingFunction = EaseOut };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
    }

    /// <summary>The pixel style's hover lift: a whole-pixel move up, on the element's own translate.</summary>
    private static void ShiftTo(FrameworkElement fe, double toY, double ms)
    {
        if (fe.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
        {
            tt = new TranslateTransform(0, 0);
            fe.RenderTransform = tt;
        }
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(toY, Dur(ms)) { EasingFunction = EaseOut });
    }

    private static ScaleTransform EnsureScale(FrameworkElement fe)
    {
        if (fe.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
            return existing;
        var st = new ScaleTransform(1, 1);
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        fe.RenderTransform = st;
        return st;
    }

    // ── Page transition: fade and slide up whenever a Frame navigates ──

    public static readonly DependencyProperty PageTransitionProperty =
        DependencyProperty.RegisterAttached("PageTransition", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnPageTransitionChanged));

    public static bool GetPageTransition(DependencyObject o) => (bool)o.GetValue(PageTransitionProperty);
    public static void SetPageTransition(DependencyObject o, bool v) => o.SetValue(PageTransitionProperty, v);

    private static void OnPageTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Frame frame) return;
        if ((bool)e.NewValue) frame.Navigated += Frame_Navigated;
        else                  frame.Navigated -= Frame_Navigated;
    }

    private static void Frame_Navigated(object sender, NavigationEventArgs e)
    {
        if (!LookState.Current.Transitions) return;
        // One entrance per navigation: if the side panel is already sliding in, the page inside it
        // doesn't animate as well. Checked on the frame, since the page may not be parented yet.
        if (sender is DependencyObject frame && IsTransitionActiveFor(frame)) return;
        if (e.Content is FrameworkElement fe)
            SlideFadeIn(fe, 0, Pixel ? 6 : 16, Pixel ? 180 : 280);
    }

    // ── Tab transition: fade and slide the content area on tab change ──

    public static readonly DependencyProperty TabTransitionProperty =
        DependencyProperty.RegisterAttached("TabTransition", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnTabTransitionChanged));

    public static bool GetTabTransition(DependencyObject o) => (bool)o.GetValue(TabTransitionProperty);
    public static void SetTabTransition(DependencyObject o, bool v) => o.SetValue(TabTransitionProperty, v);

    private static void OnTabTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabControl tc) return;
        if ((bool)e.NewValue) tc.SelectionChanged += Tab_SelectionChanged;
        else                  tc.SelectionChanged -= Tab_SelectionChanged;
    }

    private static void Tab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore selection bubbling up from lists/combos inside the tab content.
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (!LookState.Current.Transitions) return;
        var tc = (TabControl)sender;
        if (tc.Template?.FindName("PART_TabContentHost", tc) is FrameworkElement host)
            SlideFadeIn(host, Pixel ? 6 : 14, 0, Pixel ? 160 : 240);
    }

    // ── Fade in: an element fades and rises on load (dialogs, panels) ──

    public static readonly DependencyProperty FadeInProperty =
        DependencyProperty.RegisterAttached("FadeIn", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject o) => (bool)o.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject o, bool v) => o.SetValue(FadeInProperty, v);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        fe.Opacity = 0;
        void Handler(object s, RoutedEventArgs _)
        {
            fe.Loaded -= Handler;
            SlideFadeIn(fe, 0, 12, 260);
        }
        fe.Loaded += Handler;
    }

    // ── Window fade in: our own windows fade up on first show ──

    public static readonly DependencyProperty WindowFadeInProperty =
        DependencyProperty.RegisterAttached("WindowFadeIn", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnWindowFadeInChanged));

    public static bool GetWindowFadeIn(DependencyObject o) => (bool)o.GetValue(WindowFadeInProperty);
    public static void SetWindowFadeIn(DependencyObject o, bool v) => o.SetValue(WindowFadeInProperty, v);

    private static void OnWindowFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Only our own windows, never third-party ones such as the Microsoft sign-in window.
        if (d is not Window w || !(bool)e.NewValue) return;
        if (w.GetType().Namespace is not { } ns || !ns.StartsWith("CloudLauncher", StringComparison.Ordinal))
            return;

        w.Opacity = 0;
        void Handler(object s, RoutedEventArgs _)
        {
            w.Loaded -= Handler;
            var anim = new DoubleAnimation(0, 1, Dur(200)) { EasingFunction = EaseOut };
            anim.Completed += (_, __) => { w.BeginAnimation(UIElement.OpacityProperty, null); w.Opacity = 1; };
            w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        w.Loaded += Handler;
    }

    // ── Slide-fade: shared by the transitions above ──
    // Public so code-behind can use it too, e.g. when the side panel opens.

    public static void SlideFadeIn(FrameworkElement fe, double fromX, double fromY, double ms)
    {
        ms = LookState.Current.Ms(ms);
        if (ms <= 0)
        {
            // Motion off: arrive in place, clearing anything a previous slide left mid-flight.
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            fe.Opacity = 1;
            if (fe.RenderTransform is TranslateTransform { IsFrozen: false } old)
            {
                old.BeginAnimation(TranslateTransform.XProperty, null);
                old.BeginAnimation(TranslateTransform.YProperty, null);
                old.X = old.Y = 0;
            }
            return;
        }
        var dur = TimeSpan.FromMilliseconds(ms);
        var tt = new TranslateTransform(fromX, fromY);
        fe.RenderTransform = tt;
        fe.Opacity = 0;

        // A native surface (WebView2) ignores RenderTransform and would jump at the end, so airspace
        // hosts under `fe` hide it for the duration. Time-based (see IsTransitionActiveFor), so it also
        // covers a surface created during the slide.
        BeginAirspaceTransition(fe, ms);

        var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut };
        fade.Completed += (_, __) => { fe.BeginAnimation(UIElement.OpacityProperty, null); fe.Opacity = 1; };
        fe.BeginAnimation(UIElement.OpacityProperty, fade);

        if (fromX != 0)
        {
            var ax = new DoubleAnimation(fromX, 0, dur) { EasingFunction = EaseOut };
            ax.Completed += (_, __) => { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = 0; };
            tt.BeginAnimation(TranslateTransform.XProperty, ax);
        }
        if (fromY != 0)
        {
            var ay = new DoubleAnimation(fromY, 0, dur) { EasingFunction = EaseOut };
            ay.Completed += (_, __) => { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; };
            tt.BeginAnimation(TranslateTransform.YProperty, ay);
        }
    }

    // ── airspace transition coordination ──
    // Which element is animating, and until when. Time-based, so a superseded animation can't leave
    // a surface hidden forever.
    private static FrameworkElement? _transitionRoot;
    private static DateTime _transitionActiveUntil = DateTime.MinValue;

    /// <summary>Raised when a transition starts. Airspace hosts re-check
    /// <see cref="IsTransitionActiveFor"/>.</summary>
    public static event Action? AirspaceTransitionChanged;

    private static void BeginAirspaceTransition(FrameworkElement root, double ms)
    {
        _transitionRoot = root;
        // Small buffer past the animation so the reveal lands after it visually settles.
        var until = DateTime.Now.AddMilliseconds(ms + 60);
        if (until > _transitionActiveUntil) _transitionActiveUntil = until;
        AirspaceTransitionChanged?.Invoke();
    }

    /// <summary>True while a slide transition is in flight on an ancestor of <paramref name="element"/>.
    /// Airspace hosts call this to decide whether to keep their native surface hidden.</summary>
    public static bool IsTransitionActiveFor(DependencyObject element)
    {
        if (_transitionRoot is null || DateTime.Now >= _transitionActiveUntil) return false;
        for (DependencyObject? n = element; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, _transitionRoot)) return true;
        return false;
    }

    /// <summary>Milliseconds remaining until the current transition's reveal point (0 if none).</summary>
    public static double TransitionRemainingMs =>
        Math.Max(0, (_transitionActiveUntil - DateTime.Now).TotalMilliseconds);

    // ── Smooth scroll: animate the wheel instead of jumping line by line ──

    public static readonly DependencyProperty SmoothScrollProperty =
        DependencyProperty.RegisterAttached("SmoothScroll", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnSmoothScrollChanged));

    public static bool GetSmoothScroll(DependencyObject o) => (bool)o.GetValue(SmoothScrollProperty);
    public static void SetSmoothScroll(DependencyObject o, bool v) => o.SetValue(SmoothScrollProperty, v);

    /// <summary>
    /// How long one wheel notch takes to land. Much longer feels floaty; under ~150ms stops feeling
    /// smooth. Only affects detented wheels; touchpad gestures are applied directly.
    /// </summary>
    private const double EaseMs = 190;

    /// <summary>How far the real offset may drift from the one we last asked for before we
    /// stop calling the move ours. A pixel of slack covers layout rounding.</summary>
    private const double ForeignMovePx = 1.0;

    /// <summary>A re-anchor bigger than this fraction of a viewport is a jump, not a nudge, and
    /// gets a fresh ease. Anything smaller is folded into the one already running.</summary>
    private const double ReEaseViewportFraction = 0.25;

    /// <summary>Floor for the re-ease threshold, so a very short list doesn't re-ease every few
    /// pixels.</summary>
    private const double ReEaseMinPx = 48;

    /// <summary>Shortest re-ease allowed; anything shorter is effectively a jump.</summary>
    private const double MinEaseMs = 60;

    /// <summary>How long after the last movement a viewer stops counting as "scrolling".</summary>
    private const double ScrollIdleMs = 140;

    // The live target we're easing toward, and whether an ease is in flight.
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached("TargetOffset", typeof(double), typeof(Animate),
            new PropertyMetadata(double.NaN));
    private static readonly DependencyProperty IsEasingProperty =
        DependencyProperty.RegisterAttached("IsEasing", typeof(bool), typeof(Animate),
            new PropertyMetadata(false));

    /// <summary>
    /// True while this <see cref="ScrollViewer"/> is moving for any reason (wheel, touchpad, scrollbar
    /// drag, keyboard, BringIntoView). Cleared <see cref="ScrollIdleMs"/>ms after the last change.
    /// </summary>
    /// <remarks>
    /// Entrance stagger and hover lift check it, so rows scrolled past arrive already drawn and don't
    /// pick up a hover.
    /// </remarks>
    public static readonly DependencyProperty IsScrollingProperty =
        DependencyProperty.RegisterAttached("IsScrolling", typeof(bool), typeof(Animate),
            new PropertyMetadata(false));

    public static bool GetIsScrolling(DependencyObject o) => (bool)o.GetValue(IsScrollingProperty);

    /// <summary>True when the nearest <see cref="ScrollViewer"/> above <paramref name="element"/> is
    /// mid-scroll; false when there is none.</summary>
    public static bool IsAncestorScrolling(DependencyObject? element)
    {
        for (var depth = 0; element is not null && depth < 64; depth++)
        {
            if (element is ScrollViewer sv) return (bool)sv.GetValue(IsScrollingProperty);
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : (element as FrameworkContentElement)?.Parent;
        }
        return false;
    }

    // When the viewer last moved, and the timer that notices it stopped. One timer per viewer, only
    // running while scrolling; polling is cheaper than restarting a DispatcherTimer every frame.
    private static readonly DependencyProperty LastScrollTicksProperty =
        DependencyProperty.RegisterAttached("LastScrollTicks", typeof(long), typeof(Animate),
            new PropertyMetadata(0L));
    private static readonly DependencyProperty IdleTimerProperty =
        DependencyProperty.RegisterAttached("IdleTimer", typeof(DispatcherTimer), typeof(Animate),
            new PropertyMetadata(null));

    private static void MarkScrolling(ScrollViewer sv)
    {
        sv.SetValue(LastScrollTicksProperty, Environment.TickCount64);
        if ((bool)sv.GetValue(IsScrollingProperty)) return;     // already running; the timer polls

        sv.SetValue(IsScrollingProperty, true);
        SnapLiftedToRest();

        if (sv.GetValue(IdleTimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, sv.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ScrollIdleMs / 2),
            };
            timer.Tick += (s, _) =>
            {
                if (Environment.TickCount64 - (long)sv.GetValue(LastScrollTicksProperty) < ScrollIdleMs) return;
                ((DispatcherTimer)s!).Stop();
                sv.SetValue(IsScrollingProperty, false);
            };
            sv.SetValue(IdleTimerProperty, timer);
        }
        timer.Start();
    }

    // The offset the ease last asked for. If VerticalOffset comes back as something else,
    // the viewer moved behind our back and we are no longer the one driving.
    private static readonly DependencyProperty LastWrittenProperty =
        DependencyProperty.RegisterAttached("LastWritten", typeof(double), typeof(Animate),
            new PropertyMetadata(double.NaN));

    // Added to the animation's output before it is written, so a re-anchor is absorbed without
    // touching the running animation (see Scroll_Changed).
    private static readonly DependencyProperty OffsetBiasProperty =
        DependencyProperty.RegisterAttached("OffsetBias", typeof(double), typeof(Animate),
            new PropertyMetadata(0d));

    // When the current ease started and how long it was given, so a re-ease can hand back the time
    // that is left instead of starting a fresh full-length curve.
    private static readonly DependencyProperty EaseStartTicksProperty =
        DependencyProperty.RegisterAttached("EaseStartTicks", typeof(long), typeof(Animate),
            new PropertyMetadata(0L));
    private static readonly DependencyProperty EaseDurationProperty =
        DependencyProperty.RegisterAttached("EaseDuration", typeof(double), typeof(Animate),
            new PropertyMetadata(EaseMs));

    // Bumped per ease so a superseded animation's Completed can't tear down a newer one.
    private static readonly DependencyProperty EaseGenerationProperty =
        DependencyProperty.RegisterAttached("EaseGeneration", typeof(int), typeof(Animate),
            new PropertyMetadata(0));

    // Proxy DP whose changed-callback drives the actual scroll position.
    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(Animate),
            new PropertyMetadata(0d, OnAnimatedOffsetChanged));

    private static double ClampOffset(double v, double max) => v < 0 ? 0 : v > max ? max : v;

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        // Clamp against the current extent: the browse lists load more pages mid-scroll, and a
        // virtualizing panel keeps re-estimating its extent as rows of different heights realize.
        double v = ClampOffset((double)e.NewValue + (double)sv.GetValue(OffsetBiasProperty), sv.ScrollableHeight);
        sv.SetValue(LastWrittenProperty, v);
        sv.ScrollToVerticalOffset(v);
    }

    private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += Scroll_PreviewWheel;
            sv.ScrollChanged     += Scroll_Changed;
        }
        else
        {
            sv.PreviewMouseWheel -= Scroll_PreviewWheel;
            sv.ScrollChanged     -= Scroll_Changed;
        }
    }

    private static void Scroll_PreviewWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;

        // Leave text boxes, password boxes and ComboBox drop-downs alone. A drop-down scrolls in item
        // units, so the raw wheel delta would jump ~120 items per notch.
        if (sv.TemplatedParent is TextBoxBase or PasswordBox or ComboBox) return;
        // Nothing to scroll vertically; let it bubble (e.g. horizontal-only strips).
        if (sv.ScrollableHeight <= 0) return;
        // If a nested smooth-scroll viewer under the cursor can take it, defer to it.
        if (DefersToInner(sv, e.OriginalSource as DependencyObject)) return;

        e.Handled = true;

        bool animating = (bool)sv.GetValue(IsEasingProperty);
        double last = (double)sv.GetValue(LastWrittenProperty);

        // Touchpads send many small deltas per second. Easing each one restarts the animation before it
        // finishes, so the content lags and jitters. The gesture is already smooth, so apply it directly
        // and only ease real wheel notches.
        if (IsFineGrainedWheel(e.Delta))
        {
            if (animating) StopEase(sv, sv.VerticalOffset);
            double direct = ClampOffset(sv.VerticalOffset - e.Delta, sv.ScrollableHeight);
            sv.SetValue(LastWrittenProperty, direct);
            sv.ScrollToVerticalOffset(direct);
            return;
        }

        // Stack this notch on the target we're already heading for, so a fast flick covers
        // the whole distance instead of restarting from the middle of the last ease.
        double basis = animating ? (double)sv.GetValue(TargetOffsetProperty) : sv.VerticalOffset;
        if (double.IsNaN(basis)) basis = sv.VerticalOffset;

        // Start from the offset we last wrote while an ease is running: VerticalOffset
        // trails it by a layout pass, and starting from a trailing value steps backwards.
        double from = animating && !double.IsNaN(last) ? last : sv.VerticalOffset;

        BeginEase(sv, from, ClampOffset(basis - e.Delta, sv.ScrollableHeight));
    }

    /// <summary>
    /// True when this wheel event came from a precision touchpad (or a high-resolution wheel)
    /// rather than a detented mouse wheel.
    /// </summary>
    /// <remarks>
    /// A detented wheel reports whole multiples of WHEEL_DELTA (120). Checking the remainder rather
    /// than the size keeps a two-notch flick (240) on the eased path.
    /// </remarks>
    private static bool IsFineGrainedWheel(int delta)
    {
        if (delta == 0) return false;
        return Math.Abs(delta) % 120 != 0;
    }

    private static void BeginEase(ScrollViewer sv, double from, double target, double ms = EaseMs)
    {
        sv.SetValue(OffsetBiasProperty, 0d);          // the new From/To already account for reality
        sv.SetValue(TargetOffsetProperty, target);
        sv.SetValue(IsEasingProperty, true);
        sv.SetValue(EaseStartTicksProperty, Environment.TickCount64);
        sv.SetValue(EaseDurationProperty, ms);

        int gen = (int)sv.GetValue(EaseGenerationProperty) + 1;
        sv.SetValue(EaseGenerationProperty, gen);

        // Explicit From: otherwise WPF starts from the value the previous animation held, and anything
        // that moved the viewer since (scrollbar drag, keyboard, BringIntoView, re-anchoring after a
        // load) makes the next notch snap back to that stale offset first.
        var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        anim.Completed += (_, __) =>
        {
            if ((int)sv.GetValue(EaseGenerationProperty) != gen) return;
            // Park on the target as it stands now, not the one this animation was built with:
            // drift folded in along the way moved it.
            double landed = (double)sv.GetValue(TargetOffsetProperty);
            StopEase(sv, double.IsNaN(landed) ? target : landed);
        };
        sv.BeginAnimation(AnimatedOffsetProperty, anim);
    }

    /// <summary>What is left of the current ease, floored so a re-ease is still an ease.</summary>
    private static double RemainingEaseMs(ScrollViewer sv)
    {
        double total = (double)sv.GetValue(EaseDurationProperty);
        double elapsed = Environment.TickCount64 - (long)sv.GetValue(EaseStartTicksProperty);
        return Math.Max(MinEaseMs, total - elapsed);
    }

    /// <summary>Hand the offset back to the ScrollViewer. Parks the base value on
    /// <paramref name="park"/> first: the running animation still masks it, so dropping the
    /// animation afterwards reveals the same number and moves nothing.</summary>
    private static void StopEase(ScrollViewer sv, double park)
    {
        sv.SetValue(EaseGenerationProperty, (int)sv.GetValue(EaseGenerationProperty) + 1);
        // Zero the bias before the reveal below, or the parked value would be written with a
        // correction that has already been baked into it.
        sv.SetValue(OffsetBiasProperty, 0d);
        sv.SetValue(AnimatedOffsetProperty, park);
        sv.BeginAnimation(AnimatedOffsetProperty, null);
        sv.SetValue(IsEasingProperty, false);
        sv.SetValue(TargetOffsetProperty, double.NaN);
    }

    private static void Scroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged bubbles; only act on the viewer that actually moved.
        if (sender is not ScrollViewer sv || !ReferenceEquals(e.OriginalSource, sv)) return;
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;

        if (e.VerticalChange != 0) MarkScrolling(sv);

        double last = (double)sv.GetValue(LastWrittenProperty);
        if (!(bool)sv.GetValue(IsEasingProperty) || double.IsNaN(last))
        {
            sv.SetValue(LastWrittenProperty, sv.VerticalOffset);   // idle: stay in step with reality
            return;
        }

        double drift = sv.VerticalOffset - last;
        if (Math.Abs(drift) <= ForeignMovePx) return;              // that move was ours
        sv.SetValue(LastWrittenProperty, sv.VerticalOffset);

        double target = (double)sv.GetValue(TargetOffsetProperty);
        if (double.IsNaN(target)) { StopEase(sv, sv.VerticalOffset); return; }

        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // Something else moved the offset (drag, key, BringIntoView, touch pan). Stop easing rather
            // than fight it every frame.
            StopEase(sv, sv.VerticalOffset);
            return;
        }

        // Content changed size under the ease and the viewer re-anchored: shift the target by the same
        // amount. Small shifts go into the bias so the curve continues undisturbed; a virtualizing panel
        // re-estimates its extent on almost every measure, and restarting the ease each time feels
        // rubbery. Only a real jump gets a new ease, for the time left on the old one.
        double corrected = ClampOffset(target + drift, sv.ScrollableHeight);
        if (Math.Abs(drift) <= Math.Max(ReEaseMinPx, sv.ViewportHeight * ReEaseViewportFraction))
        {
            sv.SetValue(OffsetBiasProperty, (double)sv.GetValue(OffsetBiasProperty) + drift);
            sv.SetValue(TargetOffsetProperty, corrected);
            return;
        }

        BeginEase(sv, sv.VerticalOffset, corrected, RemainingEaseMs(sv));
    }

    private static bool DefersToInner(ScrollViewer outer, DependencyObject? src)
    {
        var d = src;
        while (d is not null && !ReferenceEquals(d, outer))
        {
            if (d is ScrollViewer inner && GetSmoothScroll(inner) && inner.ScrollableHeight > 0)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : null;
        }
        return false;
    }

    // ── Entrance stagger: items fade and rise as the list (re)populates ──

    public static readonly DependencyProperty EntranceStaggerProperty =
        DependencyProperty.RegisterAttached("EntranceStagger", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnEntranceStaggerChanged));

    public static bool GetEntranceStagger(DependencyObject o) => (bool)o.GetValue(EntranceStaggerProperty);
    public static void SetEntranceStagger(DependencyObject o, bool v) => o.SetValue(EntranceStaggerProperty, v);

    // Load generation, bumped on (re)populate so containers recycled by virtualization animate
    // again on a fresh load but not while scrolling.
    private static readonly DependencyProperty EpochProperty =
        DependencyProperty.RegisterAttached("Epoch", typeof(int), typeof(Animate), new PropertyMetadata(0));
    private static readonly DependencyProperty AnimatedEpochProperty =
        DependencyProperty.RegisterAttached("AnimatedEpoch", typeof(int), typeof(Animate), new PropertyMetadata(-1));
    private static readonly DependencyProperty PendingProperty =
        DependencyProperty.RegisterAttached("Pending", typeof(bool), typeof(Animate), new PropertyMetadata(false));

    /// <summary>Set after the control's first <c>Loaded</c>, so a later one doesn't re-stagger a
    /// list the user has already seen.</summary>
    /// <remarks>Reused pages (<see cref="CloudLauncher.IReusablePage"/>) get <c>Loaded</c> again on
    /// every navigation back. A repopulate still re-staggers through the collection Reset branch.</remarks>
    private static readonly DependencyProperty HasLoadedOnceProperty =
        DependencyProperty.RegisterAttached("HasLoadedOnce", typeof(bool), typeof(Animate), new PropertyMetadata(false));

    // The panel holding the containers, found once and cached. For a virtualizing panel its
    // Children are the realized range, the only set worth walking.
    private static readonly DependencyProperty ItemsHostProperty =
        DependencyProperty.RegisterAttached("ItemsHost", typeof(Panel), typeof(Animate), new PropertyMetadata(null));

    /// <summary>A list this small can be swept by index without it mattering; past it, the sweep is
    /// the cost. Only used when the items host cannot be found at all.</summary>
    private const int FallbackSweepCap = 200;

    private static void OnEntranceStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl ic || !(bool)e.NewValue) return;

        ic.Loaded += (_, __) =>
        {
            // First show only (see HasLoadedOnceProperty).
            if (!(bool)ic.GetValue(HasLoadedOnceProperty))
            {
                ic.SetValue(HasLoadedOnceProperty, true);
                Bump(ic);
            }

            // Scheduled either way: coming back into the tree still has to reveal whatever
            // containers were realised while the page was away.
            Schedule(ic);
        };
        ic.ItemContainerGenerator.StatusChanged += (_, __) =>
        {
            if (ic.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                AnimateVisible(ic);
        };
        ((INotifyCollectionChanged)ic.Items).CollectionChanged += (_, args) =>
        {
            // A Reset is a fresh population (folder switch, refresh): bump the epoch so every container
            // re-staggers. An Add (incremental load) must not bump, or items already shown would re-animate.
            if (args.Action == NotifyCollectionChangedAction.Reset)
                Bump(ic);
            if (args.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Add)
                Schedule(ic);
        };
    }

    private static void Bump(ItemsControl ic) => ic.SetValue(EpochProperty, (int)ic.GetValue(EpochProperty) + 1);

    private static void Schedule(ItemsControl ic)
    {
        if ((bool)ic.GetValue(PendingProperty)) return;
        ic.SetValue(PendingProperty, true);
        ic.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            ic.SetValue(PendingProperty, false);
            AnimateVisible(ic);
        });
    }

    /// <remarks>
    /// Runs on every ItemContainerGenerator.StatusChanged, which during a flick is every frame, so it
    /// walks only the realized containers instead of calling ContainerFromIndex for every item.
    /// </remarks>
    private static void AnimateVisible(ItemsControl ic)
    {
        var gen = ic.ItemContainerGenerator;
        if (ic.Items.Count > 0 && gen.Status != GeneratorStatus.ContainersGenerated) return;

        int epoch = (int)ic.GetValue(EpochProperty);
        var host = ItemsHost(ic);
        // Walk up from the items host: a ListBox has its ScrollViewer inside its template while the card
        // grids sit inside an outer one, and the host is below both. Rows that arrive while the page
        // around them is still sliding in arrive at rest, so the page doesn't appear to load twice.
        bool atRest = IsAncestorScrolling((DependencyObject?)host ?? ic)
                      || IsTransitionActiveFor((DependencyObject?)host ?? ic);
        int shown = 0;

        if (host is not null)
        {
            foreach (var child in host.Children)
                if (child is FrameworkElement c) Visit(c, epoch, atRest, ref shown);
            return;
        }

        // No items host found. A plain ItemsControl in a collapsed tab can still have containers, so
        // sweep by index, but only for short lists.
        if (ic.Items.Count > FallbackSweepCap) return;
        for (int i = 0; i < ic.Items.Count; i++)
            if (gen.ContainerFromIndex(i) is FrameworkElement c) Visit(c, epoch, atRest, ref shown);

        static void Visit(FrameworkElement c, int epoch, bool atRest, ref int shown)
        {
            if ((int)c.GetValue(AnimatedEpochProperty) == epoch) return;
            c.SetValue(AnimatedEpochProperty, epoch);
            // Marked as handled either way, so a row realized mid-flick doesn't fade in after the scroll
            // stops.
            if (atRest) RestContainer(c);
            else AnimateContainer(c, shown++);
        }
    }

    /// <summary>The panel holding this control's containers, cached on the control.</summary>
    private static Panel? ItemsHost(ItemsControl ic)
    {
        if (ic.GetValue(ItemsHostProperty) is Panel cached
            && cached.IsItemsHost
            && ReferenceEquals(ItemsControl.GetItemsOwner(cached), ic))
            return cached;

        var found = FindItemsHost(ic, ic, 0);
        ic.SetValue(ItemsHostProperty, found);
        return found;
    }

    private static Panel? FindItemsHost(DependencyObject node, ItemsControl owner, int depth)
    {
        if (depth > 12) return null;    // the host sits just under the template's ScrollViewer
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Panel p && p.IsItemsHost && ReferenceEquals(ItemsControl.GetItemsOwner(p), owner))
                return p;
            if (FindItemsHost(child, owner, depth + 1) is { } found) return found;
        }
        return null;
    }

    /// <summary>Put a container straight into its finished state. Also undoes a half-finished
    /// entrance left on a container that virtualization recycled mid-animation.</summary>
    private static void RestContainer(FrameworkElement c)
    {
        c.BeginAnimation(UIElement.OpacityProperty, null);
        c.Opacity = 1;
        if (c.RenderTransform is TranslateTransform tt && !tt.IsFrozen)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
    }

    private static void AnimateContainer(FrameworkElement c, int order)
    {
        // Backstop for other callers: never animate rows in while the list is scrolling.
        if (IsAncestorScrolling(c) || LookState.Current.Motion <= 0) { RestContainer(c); return; }

        // Slate's entrance: fade + a 6-pixel rise, 18 ms apart, and no overshoot (a bounce puts a
        // pixel shape between pixels for its last few frames).
        var pixel = Pixel;
        var riseBy = pixel ? 6d : 14d;
        double delay = Math.Min(order, 14) * (pixel ? 18d : 22d);
        var begin = Dur(delay);
        var dur = Dur(pixel ? 220 : 300);

        var tt = new TranslateTransform(0, riseBy);
        c.RenderTransform = tt;
        c.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = EaseOut };
        fade.Completed += (_, __) => { c.BeginAnimation(UIElement.OpacityProperty, null); c.Opacity = 1; };
        c.BeginAnimation(UIElement.OpacityProperty, fade);

        var rise = new DoubleAnimation(riseBy, 0, dur) { BeginTime = begin, EasingFunction = pixel ? EaseOut : EaseOutBack };
        rise.Completed += (_, __) => { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; };
        tt.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
