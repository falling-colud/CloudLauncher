using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace CloudLauncher.Animations;

/// <summary>
/// A bundle of attached behaviours that layer motion onto the existing styles without
/// rewriting control templates. Everything is opt-in via attached properties so a style
/// setter (or a single XAML attribute) lights up an animation app-wide.
///
/// Design rules followed throughout:
///  • Only ever animate <see cref="UIElement.Opacity"/> and <see cref="UIElement.RenderTransform"/>
///    (scale / translate). Those are always animatable and never collide with the colour-swap
///    triggers already baked into the templates. Brushes from <c>StaticResource</c> are frozen
///    and would throw if animated, so we leave colour transitions to the existing triggers.
///  • Animations are short (≤ ~300 ms) and ease-out, so the UI stays snappy.
/// </summary>
public static class Animate
{
    // Shared easing — created once, frozen, reused everywhere.
    private static readonly IEasingFunction EaseOut =
        Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseOutBack =
        Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

    private static IEasingFunction Freeze(Freezable f) { f.Freeze(); return (IEasingFunction)f; }

    // ─────────────────────────────────────────────────────────────────────────
    //  PRESS FEEDBACK — buttons grow slightly on hover, dip on press.
    // ─────────────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty PressFeedbackProperty =
        DependencyProperty.RegisterAttached("PressFeedback", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnPressFeedbackChanged));

    public static bool GetPressFeedback(DependencyObject o) => (bool)o.GetValue(PressFeedbackProperty);
    public static void SetPressFeedback(DependencyObject o, bool v) => o.SetValue(PressFeedbackProperty, v);

    private const double HoverLiftPx = -2.0;   // buttons rise slightly on hover…
    private const double PressScale  = 0.96;    // …and dip on press (scale blurs text far less briefly)

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

    private static void Press_Enter(object s, RoutedEventArgs e) => LiftTo((FrameworkElement)s, HoverLiftPx, 140);
    private static void Press_Leave(object s, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)s;
        LiftTo(fe, 0, 180);
        DipTo(fe, 1.0, 160);
    }
    private static void Press_Down(object s, RoutedEventArgs e) => DipTo((FrameworkElement)s, PressScale, 90);
    private static void Press_Up(object s, RoutedEventArgs e)   => DipTo((FrameworkElement)s, 1.0, 150);

    /// <summary>Vertical hover-lift on a button's transform group (does not blur text).</summary>
    private static void LiftTo(FrameworkElement fe, double toY, double ms)
    {
        var (_, t) = EnsureButtonTransform(fe);
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(toY, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    /// <summary>Brief press-dip scale on a button's transform group.</summary>
    private static void DipTo(FrameworkElement fe, double to, double ms)
    {
        var (sc, _) = EnsureButtonTransform(fe);
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
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

    // ─────────────────────────────────────────────────────────────────────────
    //  HOVER LIFT — cards/tiles gently scale up while hovered.
    // ─────────────────────────────────────────────────────────────────────────

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

    private static void Lift_Enter(object s, RoutedEventArgs e) => ScaleTo((FrameworkElement)s, 1.02, 160);
    private static void Lift_Leave(object s, RoutedEventArgs e) => ScaleTo((FrameworkElement)s, 1.0, 200);

    /// <summary>Animate a uniform scale on the element's render transform (origin centred).</summary>
    private static void ScaleTo(FrameworkElement fe, double to, double ms)
    {
        var st = EnsureScale(fe);
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
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

    // ─────────────────────────────────────────────────────────────────────────
    //  PAGE / FRAME TRANSITION — fade + slide-up whenever a Frame navigates.
    // ─────────────────────────────────────────────────────────────────────────

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
        if (e.Content is FrameworkElement fe)
            SlideFadeIn(fe, 0, 16, 280);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TAB TRANSITION — fade + slide the content area on tab change.
    // ─────────────────────────────────────────────────────────────────────────

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
        var tc = (TabControl)sender;
        if (tc.Template?.FindName("PART_TabContentHost", tc) is FrameworkElement host)
            SlideFadeIn(host, 14, 0, 240);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  FADE IN — any element fades + rises on load (dialogs, panels…).
    // ─────────────────────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────────────────────
    //  WINDOW FADE IN — our own dialog/main windows fade up on first show.
    // ─────────────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty WindowFadeInProperty =
        DependencyProperty.RegisterAttached("WindowFadeIn", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnWindowFadeInChanged));

    public static bool GetWindowFadeIn(DependencyObject o) => (bool)o.GetValue(WindowFadeInProperty);
    public static void SetWindowFadeIn(DependencyObject o, bool v) => o.SetValue(WindowFadeInProperty, v);

    private static void OnWindowFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Only fade our own windows — never third-party (e.g. the MS auth) windows.
        if (d is not Window w || !(bool)e.NewValue) return;
        if (w.GetType().Namespace is not { } ns || !ns.StartsWith("CloudLauncher", StringComparison.Ordinal))
            return;

        w.Opacity = 0;
        void Handler(object s, RoutedEventArgs _)
        {
            w.Loaded -= Handler;
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut };
            anim.Completed += (_, __) => { w.BeginAnimation(UIElement.OpacityProperty, null); w.Opacity = 1; };
            w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        w.Loaded += Handler;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SLIDE-FADE — shared primitive used by the transitions above (also public
    //  so code-behind can reuse it, e.g. the side panel opening).
    // ─────────────────────────────────────────────────────────────────────────

    public static void SlideFadeIn(FrameworkElement fe, double fromX, double fromY, double ms)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        var tt = new TranslateTransform(fromX, fromY);
        fe.RenderTransform = tt;
        fe.Opacity = 0;

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

    // ─────────────────────────────────────────────────────────────────────────
    //  SMOOTH SCROLL — animate the wheel instead of jumping line-by-line.
    // ─────────────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty SmoothScrollProperty =
        DependencyProperty.RegisterAttached("SmoothScroll", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnSmoothScrollChanged));

    public static bool GetSmoothScroll(DependencyObject o) => (bool)o.GetValue(SmoothScrollProperty);
    public static void SetSmoothScroll(DependencyObject o, bool v) => o.SetValue(SmoothScrollProperty, v);

    // The live target we're easing toward, and whether an ease is in flight.
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached("TargetOffset", typeof(double), typeof(Animate),
            new PropertyMetadata(double.NaN));
    private static readonly DependencyProperty IsScrollingProperty =
        DependencyProperty.RegisterAttached("IsScrolling", typeof(bool), typeof(Animate),
            new PropertyMetadata(false));

    // Proxy DP whose changed-callback drives the actual scroll position.
    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(Animate),
            new PropertyMetadata(0d, OnAnimatedOffsetChanged));

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += Scroll_PreviewWheel;
        else                  sv.PreviewMouseWheel -= Scroll_PreviewWheel;
    }

    private static void Scroll_PreviewWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;

        // Don't hijack text-box / password content hosts, nor a ComboBox's drop-down.
        // The drop-down scrolls in item units (logical scrolling), whereas this animator
        // works in the device-pixel units of VerticalOffset — feeding it the raw wheel
        // delta there would leap ~120 items per notch (≈ half a long version list). Let
        // the combo scroll natively.
        if (sv.TemplatedParent is TextBoxBase or PasswordBox or ComboBox) return;
        // Nothing to scroll vertically — let it bubble (e.g. horizontal-only strips).
        if (sv.ScrollableHeight <= 0) return;
        // If a nested smooth-scroll viewer under the cursor can take it, defer to it.
        if (DefersToInner(sv, e.OriginalSource as DependencyObject)) return;

        e.Handled = true;

        bool animating = (bool)sv.GetValue(IsScrollingProperty);
        double basis = animating ? (double)sv.GetValue(TargetOffsetProperty) : sv.VerticalOffset;
        if (double.IsNaN(basis)) basis = sv.VerticalOffset;

        double target = Math.Max(0, Math.Min(sv.ScrollableHeight, basis - e.Delta));
        sv.SetValue(TargetOffsetProperty, target);
        sv.SetValue(IsScrollingProperty, true);

        sv.SetValue(AnimatedOffsetProperty, sv.VerticalOffset);
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut };
        anim.Completed += (_, __) =>
        {
            // Only clear the in-flight flag if no newer tick changed the target.
            if ((double)sv.GetValue(TargetOffsetProperty) == target)
                sv.SetValue(IsScrollingProperty, false);
        };
        sv.BeginAnimation(AnimatedOffsetProperty, anim);
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

    // ─────────────────────────────────────────────────────────────────────────
    //  ENTRANCE STAGGER — items fade + rise as the list (re)populates.
    // ─────────────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty EntranceStaggerProperty =
        DependencyProperty.RegisterAttached("EntranceStagger", typeof(bool), typeof(Animate),
            new PropertyMetadata(false, OnEntranceStaggerChanged));

    public static bool GetEntranceStagger(DependencyObject o) => (bool)o.GetValue(EntranceStaggerProperty);
    public static void SetEntranceStagger(DependencyObject o, bool v) => o.SetValue(EntranceStaggerProperty, v);

    // Monotonically increasing "load generation" — bumped on (re)populate so containers
    // recycled by virtualization animate again on a fresh load but not while scrolling.
    private static readonly DependencyProperty EpochProperty =
        DependencyProperty.RegisterAttached("Epoch", typeof(int), typeof(Animate), new PropertyMetadata(0));
    private static readonly DependencyProperty AnimatedEpochProperty =
        DependencyProperty.RegisterAttached("AnimatedEpoch", typeof(int), typeof(Animate), new PropertyMetadata(-1));
    private static readonly DependencyProperty PendingProperty =
        DependencyProperty.RegisterAttached("Pending", typeof(bool), typeof(Animate), new PropertyMetadata(false));

    private static void OnEntranceStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl ic || !(bool)e.NewValue) return;

        ic.Loaded += (_, __) => { Bump(ic); Schedule(ic); };
        ic.ItemContainerGenerator.StatusChanged += (_, __) =>
        {
            if (ic.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                AnimateVisible(ic);
        };
        ((INotifyCollectionChanged)ic.Items).CollectionChanged += (_, args) =>
        {
            // A Reset is a fresh population (folder switch / refresh) — bump the epoch so
            // every container re-staggers. A plain Add (incremental/async load) must NOT
            // bump, or already-shown items would re-animate; we just reveal the new ones.
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

    private static void AnimateVisible(ItemsControl ic)
    {
        var gen = ic.ItemContainerGenerator;
        if (ic.Items.Count > 0 && gen.Status != GeneratorStatus.ContainersGenerated) return;

        int epoch = (int)ic.GetValue(EpochProperty);
        int shown = 0;
        for (int i = 0; i < ic.Items.Count; i++)
        {
            if (gen.ContainerFromIndex(i) is not FrameworkElement c) continue;
            if ((int)c.GetValue(AnimatedEpochProperty) == epoch) continue;
            c.SetValue(AnimatedEpochProperty, epoch);
            AnimateContainer(c, shown++);
        }
    }

    private static void AnimateContainer(FrameworkElement c, int order)
    {
        double delay = Math.Min(order, 14) * 22d;
        var begin = TimeSpan.FromMilliseconds(delay);
        var dur = TimeSpan.FromMilliseconds(300);

        var tt = new TranslateTransform(0, 14);
        c.RenderTransform = tt;
        c.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = EaseOut };
        fade.Completed += (_, __) => { c.BeginAnimation(UIElement.OpacityProperty, null); c.Opacity = 1; };
        c.BeginAnimation(UIElement.OpacityProperty, fade);

        var rise = new DoubleAnimation(14, 0, dur) { BeginTime = begin, EasingFunction = EaseOutBack };
        rise.Completed += (_, __) => { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; };
        tt.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
