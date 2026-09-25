using System.Collections.ObjectModel;
using System.Windows.Controls;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// Reconciles an <see cref="ObservableCollection{T}"/> to a freshly scanned list in place, keyed,
/// without ever raising a Reset.
/// </summary>
/// <remarks>
/// <para>Replacing <c>ItemsSource</c> with a new list loses the scroll position, selection and
/// keyboard focus, and restarts <see cref="Animations.Animate"/>'s entrance stagger. Diffing keeps
/// the row objects, which all of those are attached to.</para>
/// <para>This keeps selection, focus and the animation epoch, but not the viewport: rows appearing
/// or disappearing above it still slide the visible ones. <see cref="ListScrollAnchor"/> fixes that,
/// and <see cref="PageRefresh"/> combines both. Use <see cref="Apply"/> alone for a filter pass
/// outside a load.</para>
/// <para>Never raise a Reset (so never <see cref="ObservableCollection{T}.Clear"/>): consumers treat
/// it as a fresh load, which drops the selection and re-animates.</para>
/// <para>A rescan returns new row instances, so surviving rows are updated in place through
/// <c>update</c>. UI thread only.</para>
/// </remarks>
public static class ListDiff
{
    /// <summary>
    /// Makes <paramref name="target"/> hold the same rows as <paramref name="desired"/>, in the same
    /// order, using only Add, Remove and Move.
    /// </summary>
    /// <param name="target">The live collection something is bound to. Mutated in place.</param>
    /// <param name="desired">What the scan, search or filter says should be there now.</param>
    /// <param name="key">The row's identity: a file path, an instance id, a world's key. Keep it stable
    /// for the life of the row; if it changes, the row is treated as new on the next pass and loses
    /// its selection.</param>
    /// <param name="update">Copies the fresh row's values onto the one already on screen, for every
    /// row whose key survived. Without it, surviving rows keep their old values (a world's old size,
    /// a mod's old version). Assign through the row's property setters so bindings repaint, or put the
    /// volatile data in the key.</param>
    /// <param name="comparer">For keys whose equality is not the default, such as case-insensitive
    /// paths. Null uses <see cref="EqualityComparer{T}.Default"/>.</param>
    /// <returns>The number of collection notifications raised. Zero for equal input, so reopening a
    /// page whose scan finds the same rows doesn't move the list.</returns>
    /// <remarks>
    /// <para>Repeated keys are kept: a key that appears n times in <paramref name="desired"/> keeps n
    /// rows, paired with existing rows in list order. Dropping them would make the page's counts wrong,
    /// and throwing would crash a background rescan.</para>
    /// <para>The same row object must not appear twice in either list; WPF can't handle that
    /// either.</para>
    /// <para>Linear in both lists' lengths. A first paint into an empty collection is n Adds, which is
    /// the pass that should animate anyway.</para>
    /// </remarks>
    public static int Apply<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> desired,
                                     Func<T, TKey> key, Action<T, T>? update = null,
                                     IEqualityComparer<TKey>? comparer = null)
        where T : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(key);

        var keys = comparer ?? EqualityComparer<TKey>.Default;
        var changes = 0;

        // Read every key once up front, so an unstable selector can't make the phases below disagree
        // about which rows exist (an index crash on the UI thread).
        var here = new TKey[target.Count];
        for (var i = 0; i < here.Length; i++) here[i] = key(target[i]);
        var wants = new TKey[desired.Count];
        for (var i = 0; i < wants.Length; i++) wants[i] = key(desired[i]);

        // ── 1. take out what is no longer wanted ─────────────────────────────
        // Counted rather than a set, so a repeated key keeps n rows.
        var quota = new Dictionary<TKey, int>(wants.Length, keys);
        foreach (var k in wants) quota[k] = quota.TryGetValue(k, out var seen) ? seen + 1 : 1;

        List<int>? drop = null;
        var survivors = new List<T>(here.Length);
        var survivorKeys = new List<TKey>(here.Length);
        for (var i = 0; i < here.Length; i++)
        {
            if (quota.TryGetValue(here[i], out var left) && left > 0)
            {
                quota[here[i]] = left - 1;
                survivors.Add(target[i]);
                survivorKeys.Add(here[i]);
            }
            else (drop ??= []).Add(i);
        }

        // Back to front, so the remaining indexes stay valid.
        if (drop is not null)
            for (var i = drop.Count - 1; i >= 0; i--)
            {
                target.RemoveAt(drop[i]);
                changes++;
            }

        // ── 2. pair the survivors with their fresh twins ─────────────────────
        // A queue per key, so repeated keys pair up by position.
        var pool = new Dictionary<TKey, Queue<T>>(keys);
        for (var i = 0; i < survivors.Count; i++)
        {
            if (!pool.TryGetValue(survivorKeys[i], out var queue)) pool[survivorKeys[i]] = queue = new Queue<T>();
            queue.Enqueue(survivors[i]);
        }

        var order = new List<T>(survivors.Count);      // the survivors, in the order desired wants
        List<(int Index, T Row)>? arrivals = null;     // and the rows that are new
        for (var i = 0; i < wants.Length; i++)
        {
            if (pool.TryGetValue(wants[i], out var queue) && queue.Count > 0)
            {
                var kept = queue.Dequeue();
                order.Add(kept);
                // Keep the object on screen and update its values, unless the page handed back the same
                // instance.
                if (update is not null && !ReferenceEquals(kept, desired[i])) update(kept, desired[i]);
            }
            else (arrivals ??= []).Add((i, desired[i]));
        }

        // ── 3. put the survivors in that order, by swapping ──────────────────
        if (order.Count > 1)
        {
            // Reference identity: two rows can be Equal and still be different objects with their own
            // containers.
            var at = new Dictionary<T, int>(order.Count, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < target.Count; i++) at[target[i]] = i;

            for (var i = 0; i < order.Count; i++)
            {
                var want = order[i];
                if (ReferenceEquals(target[i], want)) continue;

                // Everything below i is already final, so the row we want is strictly after i.
                var from = at[want];
                var displaced = target[i];

                if (from == i + 1)
                {
                    // Adjacent rows: one Move is already the swap.
                    target.Move(from, i);
                    changes++;
                }
                else
                {
                    // Swap with two Moves. A single Move(from, i) would shift every row in between,
                    // invalidating `at` and making a re-sort O(n^2); a swap touches only two rows.
                    target.Move(from, i);
                    target.Move(i + 1, from);
                    changes += 2;
                }

                at[want] = i;
                at[displaced] = from;
            }
        }

        // ── 4. put the new rows where they belong ────────────────────────────
        if (arrivals is not null)
            foreach (var (index, row) in arrivals)
            {
                // In ascending order, so earlier inserts have already made the prefix match `desired`.
                target.Insert(index, row);
                changes++;
            }

        return changes;
    }
}

/// <summary>
/// What a page remembered from its last scan, and when that scan happened.
/// </summary>
/// <remarks>
/// The age goes with the rows so <see cref="PageRefresh"/> can say how old they are. <c>default</c>
/// is a valid "nothing remembered", since <see cref="Rows"/> never returns null.
/// </remarks>
/// <typeparam name="T">The page's row view model.</typeparam>
public readonly struct CachedRows<T>
{
    private readonly IReadOnlyList<T>? _rows;

    /// <param name="rows">The remembered rows as the page's view models. Null or empty both mean
    /// "nothing remembered".</param>
    /// <param name="scannedUtc">When the scan behind those rows ran, from
    /// <see cref="CachedScan{T}.ScannedUtc"/> (the oldest one if the page combines several). Pass it
    /// unconverted; <see cref="TimeFormat"/> localises it.</param>
    public CachedRows(IReadOnlyList<T>? rows, DateTimeOffset? scannedUtc)
    {
        _rows = rows;
        ScannedUtc = scannedUtc;
    }

    /// <summary>The remembered rows, never null.</summary>
    public IReadOnlyList<T> Rows => _rows ?? [];

    /// <summary>When the scan that produced <see cref="Rows"/> ran, or null if that is not known.</summary>
    public DateTimeOffset? ScannedUtc { get; }

    /// <summary>True when there is something to paint before the rescan.</summary>
    public bool Any => Rows.Count > 0;

    /// <summary>Nothing remembered: a first open, or a forced refresh that ignores the cache.</summary>
    public static CachedRows<T> None => default;
}

/// <summary>
/// Reopening a page, refreshing it, or re-running its scan as a silent background refresh:
/// the rows already on screen stay, a 2px hairline shows work in flight, and only what changed
/// moves.
/// </summary>
/// <remarks>
/// <para><see cref="ListDiff.Apply"/> keeps selection, focus and the entrance animation;
/// <see cref="ListScrollAnchor"/> keeps the viewport. Pages filtering in place call
/// <see cref="ListDiff.Apply"/> directly.</para>
/// <para>Pages must meet <see cref="IReusablePage"/>'s contract first, or a reused page can look
/// right while its event subscriptions or token source are dead.</para>
/// <para>A cancelled pass paints nothing (the page reports a user cancel from its CancelRequested
/// handler). A failed pass keeps the rows and reports through <see cref="PageState.Error"/>.
/// <see cref="PageState.Offline"/> and empty-filter messages are the page's job, guarded by the
/// returned result.</para>
/// </remarks>
/// <example>
/// A whole page load. <c>_rows</c> holds the scan's rows; <c>ApplyFilter</c> narrows them into the
/// bound collection with <see cref="ListDiff.Apply"/> and calls <see cref="PageState.Content"/>:
/// <code>
/// private async Task LoadAsync(bool force = false)
/// {
///     _scanCts?.Cancel();                       // IReusablePage point 2: a fresh CTS every pass
///     _scanCts = new CancellationTokenSource();
///     var ok = await PageRefresh.RunAsync(_state, _rows, WorldList, r =&gt; r.Source.Key,
///         cached:  () =&gt; force ? CachedRows&lt;WorldRow&gt;.None : Remembered(_allPacks),
///         rescan:  ct =&gt; Rescan(_allPacks, force, ct),          // Task.Run'd for you
///         ct:      _scanCts.Token,
///         filter:  ApplyFilter,                                 // owns Content() and EmptyFiltered()
///         note:    n =&gt; _note = n,                              // the age note
///         update:  (row, scanned) =&gt; row.CopyFrom(scanned),     // or the row shows old sizes
///         prepare: async ct =&gt; _allPacks = await PacksAsync(ct),   // runs before cached()
///         failed:  "The saves folders could not be read.");
///     if (ok &amp;&amp; App.State.Api.PackListStale is { Length: &gt; 0 } why)
///         _state.Offline(why, PackListCache.AgeInWords());
/// }
/// </code>
/// </example>
public static class PageRefresh
{
    /// <summary>
    /// The sentence a cached paint puts on the status line, from
    /// <see cref="TimeFormat.Ago(DateTimeOffset?)"/>'s wording for the age.
    /// </summary>
    /// <remarks>Shared so every page words it the same. Pages can pass <c>cachedNote</c> to
    /// <c>RunAsync</c> to name what is being re-read.</remarks>
    /// <param name="age">"3 hours ago", or null when the scan's timestamp is not known.</param>
    public static string RememberedNote(string? age) =>
        age is { Length: > 0 }
            ? $"Remembered from the last scan ({age}) - re-reading it now."
            : "Showing the last scan - re-reading it now.";

    /// <summary>
    /// Runs one load: cached paint, hairline, rescan on a worker thread, diff, scroll restore.
    /// </summary>
    /// <param name="state">The page's <see cref="PageState"/>. This calls
    /// <see cref="PageState.Begin(string?, bool?)"/> and, on failure, <see cref="PageState.Error"/>;
    /// <see cref="PageState.Content"/> is left to <paramref name="filter"/> when there is one.</param>
    /// <param name="rows">Everything the scan found. Bound directly on a page that doesn't filter;
    /// otherwise the master collection that <paramref name="filter"/> narrows into the bound one with
    /// <see cref="ListDiff.Apply"/>.</param>
    /// <param name="list">The <see cref="ItemsControl"/> showing the visible rows, whose viewport is
    /// kept still. Null for a page without a scrolling list.</param>
    /// <param name="key">The row's identity, for <see cref="ListDiff.Apply"/>.</param>
    /// <param name="cached">The cheap remembered rows, read on the UI thread (a
    /// <see cref="Services.ScanCache{T}"/> <c>Get</c>, never a disk walk). Return
    /// <see cref="CachedRows{T}.None"/> for a forced refresh.</param>
    /// <param name="rescan">The real scan, run on a worker thread for you. Don't touch UI from it;
    /// capture what it needs first.</param>
    /// <param name="ct">The token for this pass. The page cancels the previous pass and makes a new
    /// source, per <see cref="IReusablePage"/> point 2.</param>
    /// <param name="filter">The page's own filter, sort and chip rebuild, run on the UI thread after
    /// each diff. It owns <see cref="PageState.Content"/>, including
    /// <see cref="PageState.EmptyFiltered"/>. If null, this writes <c>Content(rows.Count)</c>.</param>
    /// <param name="note">Receives the "remembered from" sentence while a cached paint is on screen,
    /// and null once fresh rows land. Store it where the filter re-applies it on every pass.</param>
    /// <param name="update">Folds a rescanned row into the one on screen. See
    /// <see cref="ListDiff.Apply"/>: without it, surviving rows keep stale values.</param>
    /// <param name="prepare">Cheap setup the cached read needs first, such as warming the
    /// <see cref="Services.ScanCache{T}"/> file or fetching the instance list. Same token and error
    /// handling as the rest; not for the expensive scan, since the busy state shows meanwhile.</param>
    /// <param name="busyLine">Passed to <see cref="PageState.Begin(string?, bool?)"/>. Null uses the
    /// page's <see cref="PageCopy.LoadingLine"/>.</param>
    /// <param name="failed">The clause under the page's <see cref="PageCopy.ErrorTitle"/> if the
    /// rescan throws, e.g. "the saves folders could not be read". The exception itself goes to
    /// <see cref="AppLog"/>. Empty leaves just the headline.</param>
    /// <param name="cachedNote">Overrides <see cref="RememberedNote"/> for a page that wants to name
    /// what it is re-reading. Takes the age phrase, which may be null.</param>
    /// <param name="quiet">True for a pass nobody asked for (opening or reopening the page). Over rows
    /// already on screen, the hairline and note only appear if the rescan is still running after
    /// <see cref="PageState.QuietRefreshDelay"/> (see
    /// <see cref="PageState.Begin(string?, bool?, bool, Action?)"/>). Pass false for Refresh/F5.</param>
    /// <returns>True only when this pass painted fresh rows; false when it was cancelled, superseded
    /// or failed. Guard anything the page does afterwards on it (an <see cref="PageState.Offline"/>
    /// note, for example), or a superseded pass can overwrite the newer one.</returns>
    public static Task<bool> RunAsync<T, TKey>(
        PageState state,
        ObservableCollection<T> rows,
        ItemsControl? list,
        Func<T, TKey> key,
        Func<CachedRows<T>> cached,
        Func<CancellationToken, IReadOnlyList<T>> rescan,
        CancellationToken ct,
        Action? filter = null,
        Action<string?>? note = null,
        Action<T, T>? update = null,
        Func<CancellationToken, Task>? prepare = null,
        string? busyLine = null,
        string failed = "",
        Func<string?, string>? cachedNote = null,
        bool quiet = true)
        where T : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rescan);
        return RunAsync(state, rows, list, key, cached,
                        token => Task.Run(() => rescan(token), token),
                        ct, filter, note, update, prepare, busyLine, failed, cachedNote, quiet);
    }

    /// <inheritdoc cref="RunAsync{T,TKey}(PageState, ObservableCollection{T}, ItemsControl, Func{T,TKey}, Func{CachedRows{T}}, Func{CancellationToken,IReadOnlyList{T}}, CancellationToken, Action, Action{string}, Action{T,T}, Func{CancellationToken,Task}, string, string, Func{string,string})"/>
    /// <param name="rescan">The real scan, already asynchronous (a store search, or a scan the page
    /// puts on a worker thread itself). Awaited as is, with no thread hop, so use the other overload
    /// for synchronous disk walks.</param>
    public static async Task<bool> RunAsync<T, TKey>(
        PageState state,
        ObservableCollection<T> rows,
        ItemsControl? list,
        Func<T, TKey> key,
        Func<CachedRows<T>> cached,
        Func<CancellationToken, Task<IReadOnlyList<T>>> rescan,
        CancellationToken ct,
        Action? filter = null,
        Action<string?>? note = null,
        Action<T, T>? update = null,
        Func<CancellationToken, Task>? prepare = null,
        string? busyLine = null,
        string failed = "",
        Func<string?, string>? cachedNote = null,
        bool quiet = true)
        where T : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(rescan);

        // Already cancelled (the page was left before we got here): don't start the busy state.
        if (ct.IsCancellationRequested) return false;

        // One anchor for both paints; it caches the ScrollViewer it finds.
        var anchor = list is null ? null : new ListScrollAnchor(list);

        // Begin before awaiting anything, so `prepare` runs under the busy state too. A reopened page
        // still has HasData, so this is a quiet refresh instead of the busy panel.
        state.Begin(busyLine, refreshing: null, quiet: quiet);
        try
        {
            if (prepare is not null) await prepare(ct);
            if (ct.IsCancellationRequested) return false;

            var memory = cached();
            if (memory.Any)
            {
                var remembered = (cachedNote ?? RememberedNote)(TimeFormat.Ago(memory.ScannedUtc));
                if (quiet)
                {
                    // The note appears with the hairline, only if the rescan is still running by then. It goes
                    // into the page's field (so filter passes keep it) and onto the status line.
                    note?.Invoke(null);
                    Paint(memory.Rows);
                    state.Begin(busyLine, refreshing: true, quiet: true, onReveal: () =>
                    {
                        note?.Invoke(remembered);
                        state.Note(remembered);
                    });
                }
                else
                {
                    // Set the note before painting: the filter writes the status line from the page's field.
                    note?.Invoke(remembered);
                    Paint(memory.Rows);
                    // Hairline after the paint: Content() sets HasData, and refreshing: true then keeps the rows
                    // visible.
                    state.Begin(busyLine, refreshing: true);
                }
            }

            var scanned = await rescan(ct);
            // Check again: the pass may have been superseded while the scan ran.
            if (ct.IsCancellationRequested) return false;

            note?.Invoke(null);
            Paint(scanned);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Say nothing: a newer pass owns the state, or the page's CancelRequested handler has
            // already reported it.
            return false;
        }
        catch (Exception ex)
        {
            // A cancellation that surfaced as something other than OperationCanceledException is
            // still a cancellation, and still must not paint.
            if (ct.IsCancellationRequested) return false;

            // Keep the rows: PageState.Error leaves them up when it has data and puts the sentence on the
            // status line. Clear the note, since we are no longer re-reading.
            note?.Invoke(null);
            state.Error(failed, ex);
            return false;
        }

        void Paint(IReadOnlyList<T> incoming)
        {
            var before = Snapshot(list);
            var capture = anchor?.Take(before);

            ListDiff.Apply(rows, incoming, key, update);

            // Filter between the diff and the count, so the count matches the rows on screen.
            if (filter is not null) filter();
            else state.Content(rows.Count);

            if (anchor is not null && capture is { } mark) anchor.Restore(mark, before, Snapshot(list));
        }
    }

    /// <summary>The order currently on screen, which <see cref="ListScrollAnchor"/> needs on both
    /// sides of a rebuild.</summary>
    /// <remarks>Reads the control's own <c>Items</c>, since a filtering page binds a narrowed
    /// collection. Null items are kept so the order still lines up with the screen.</remarks>
    private static object[]? Snapshot(ItemsControl? list)
    {
        if (list is null) return null;
        var items = list.Items;
        if (items.Count == 0) return [];
        var copy = new object[items.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = items[i]!;
        return copy;
    }
}
