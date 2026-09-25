using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CloudLauncher.Views;

/// <summary>
/// Paging for the browse lists (mods, packs, resource packs, worlds), which grow a page at a time
/// as they are scrolled.
/// </summary>
/// <remarks>
/// The next page is fetched while the end is still a screen and a half away. A per-pass cap stops
/// a list that doesn't fill its window from loading the whole store on first paint. ScrollChanged
/// fires on every frame of the smooth-scroll ease, so checks are throttled, and the top-up loop
/// yields to layout instead of calling <c>UpdateLayout</c> inside a scroll frame.
/// </remarks>
internal static class InfiniteScroll
{
    /// <summary>How far past the bottom edge to keep loaded, in viewports.</summary>
    private const double PrefetchViewports = 1.5;

    /// <summary>A floor for the above, for a short list in a small window.</summary>
    private const double MinBufferPixels = 400;

    /// <summary>Maximum pages loaded per scroll event.</summary>
    private const int MaxPagesPerPass = 3;

    /// <summary>Minimum gap between buffer checks. Short enough that a flick still loads ahead, long
    /// enough that ~60 ease frames a second become about eight checks.</summary>
    private const double ThrottleMs = 120;

    /// <summary>A jump at least this far (scrollbar drag, page key) is checked straight away,
    /// ignoring the throttle.</summary>
    private const double MoveBypassPx = 200;

    /// <summary>True when the buffer below the viewport has run down far enough to top up.</summary>
    public static bool NeedsMore(ScrollViewer sv)
    {
        // Check the viewport rather than the extent: a zero extent with a visible viewport means nothing
        // has loaded yet, and that list still needs its first page.
        if (sv.ViewportHeight <= 0) return false;
        var buffer = Math.Max(MinBufferPixels, sv.ViewportHeight * PrefetchViewports);
        return sv.ExtentHeight - (sv.VerticalOffset + sv.ViewportHeight) <= buffer;
    }

    // ── throttle state, stored on the ScrollViewer ──
    // Attached properties instead of a side table: no allocation per event, and the state goes away
    // with the viewer.

    private static readonly DependencyProperty LastCheckTicksProperty =
        DependencyProperty.RegisterAttached("LastCheckTicks", typeof(long), typeof(InfiniteScroll),
            new PropertyMetadata(0L));

    private static readonly DependencyProperty LastCheckOffsetProperty =
        DependencyProperty.RegisterAttached("LastCheckOffset", typeof(double), typeof(InfiniteScroll),
            new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty PassActiveProperty =
        DependencyProperty.RegisterAttached("PassActive", typeof(bool), typeof(InfiniteScroll),
            new PropertyMetadata(false));

    /// <summary>Rate-limits the buffer check. Returns false when this scroll frame should be
    /// ignored.</summary>
    private static bool ShouldCheck(ScrollViewer sv, ref long lastTicks, ref double lastOffset)
    {
        var now = Environment.TickCount64;
        var offset = sv.VerticalOffset;
        var jumped = double.IsNaN(lastOffset) || Math.Abs(offset - lastOffset) > MoveBypassPx;
        if (!jumped && now - lastTicks < ThrottleMs) return false;

        lastTicks = now;
        lastOffset = offset;
        return true;
    }

    /// <summary>
    /// Tops the list up. <paramref name="canLoad"/> is the page's "more to load and not busy" test;
    /// <paramref name="count"/> is the loaded row count, which stops the loop when a page comes back
    /// empty.
    /// </summary>
    /// <remarks>
    /// For call sites not yet on <see cref="Pager"/>. The caller owns the in-flight task here, so the
    /// race that <see cref="Pager"/> avoids still applies.
    /// </remarks>
    public static async Task FillAheadAsync(
        ScrollViewer sv, Func<bool> canLoad, Func<int> count, Func<Task> loadMore)
    {
        if ((bool)sv.GetValue(PassActiveProperty)) return;

        var ticks = (long)sv.GetValue(LastCheckTicksProperty);
        var offset = (double)sv.GetValue(LastCheckOffsetProperty);
        var proceed = ShouldCheck(sv, ref ticks, ref offset);
        sv.SetValue(LastCheckTicksProperty, ticks);
        sv.SetValue(LastCheckOffsetProperty, offset);
        if (!proceed) return;

        sv.SetValue(PassActiveProperty, true);
        try
        {
            for (var pass = 0; pass < MaxPagesPerPass; pass++)
            {
                if (!canLoad() || !NeedsMore(sv)) return;

                var before = count();
                await loadMore();
                if (count() == before) return;

                // Let layout measure the new rows before the next pass, or it reads the old extent and fetches
                // a page it doesn't need. Yielding avoids a synchronous UpdateLayout inside a scroll frame.
                await Dispatcher.Yield(DispatcherPriority.Loaded);
            }
        }
        finally { sv.SetValue(PassActiveProperty, false); }
    }

    /// <summary>
    /// Owns one list's paging: the throttle state, the re-entrancy guard and the in-flight load task.
    /// </summary>
    /// <remarks>
    /// Only the pager starts loads, so there is only ever one task to await. When callers stored the
    /// task themselves, overlapping passes could overwrite it and a new search could clear the rows
    /// while an old page was still arriving.
    ///
    /// Call-site shape:
    /// <code>
    /// private readonly InfiniteScroll.Pager _pager;   // built in the ctor:
    /// _pager = new InfiniteScroll.Pager(
    ///     () =&gt; !_isLoading &amp;&amp; _hasMore &amp;&amp; _activeChip is not null &amp;&amp; _rows.Count &lt; MaxResults,
    ///     () =&gt; _rows.Count,
    ///     LoadMoreAsync);
    ///
    /// // new search: cancel, then drain, then clear
    /// _cts.Cancel(); await _pager.DrainAsync(); _pager.Reset(); _rows.Clear();
    /// await _pager.LoadPageAsync(_cts.Token);
    ///
    /// // scroll
    /// await _pager.FillAheadAsync(sv, _cts.Token);
    /// </code>
    /// </remarks>
    internal sealed class Pager
    {
        private readonly Func<bool> _canLoad;
        private readonly Func<int> _count;
        private readonly Func<CancellationToken, Task> _loadPage;

        private Task _inFlight = Task.CompletedTask;
        private bool _passActive;
        private long _lastCheckTicks;
        private double _lastCheckOffset = double.NaN;

        public Pager(Func<bool> canLoad, Func<int> count, Func<CancellationToken, Task> loadPage)
        {
            _canLoad = canLoad;
            _count = count;
            _loadPage = loadPage;
        }

        /// <summary>The load currently running, or a completed task. Never null; this is the task to
        /// wait on after cancelling.</summary>
        public Task InFlight => _inFlight;

        /// <summary>True while a page is on the wire.</summary>
        public bool IsLoading => !_inFlight.IsCompleted;

        /// <summary>Loads one page, or joins the load already running. Use this for the first page
        /// of a fresh search, where there is no buffer to test yet.</summary>
        public Task LoadPageAsync(CancellationToken ct)
        {
            if (!_inFlight.IsCompleted) return _inFlight;
            // A loader that throws before its first await would otherwise leave _inFlight pointing at the
            // previous page's task.
            try { return _inFlight = _loadPage(ct); }
            catch (Exception ex) { return _inFlight = Task.FromException(ex); }
        }

        /// <summary>Waits for the current load to finish, swallowing the cancellation exception.</summary>
        public async Task DrainAsync()
        {
            try { await _inFlight; }
            catch { /* cancelled, or the loader already handled the error */ }
        }

        /// <summary>Resets the throttle so the next scroll frame is acted on. Call it when the list is
        /// repopulated.</summary>
        public void Reset()
        {
            _lastCheckTicks = 0;
            _lastCheckOffset = double.NaN;
        }

        /// <summary>Tops the list up, throttled. Safe to call from every ScrollChanged.</summary>
        public async Task FillAheadAsync(ScrollViewer sv, CancellationToken ct)
        {
            if (_passActive) return;
            if (!ShouldCheck(sv, ref _lastCheckTicks, ref _lastCheckOffset)) return;

            _passActive = true;
            try
            {
                for (var pass = 0; pass < MaxPagesPerPass; pass++)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!_canLoad() || !NeedsMore(sv)) return;

                    var before = _count();
                    await LoadPageAsync(ct);
                    if (ct.IsCancellationRequested || _count() == before) return;

                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                }
            }
            finally { _passActive = false; }
        }
    }
}
