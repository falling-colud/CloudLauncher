namespace CloudLauncher;

/// <summary>
/// Marks a master <see cref="System.Windows.Controls.Page"/> that <see cref="MainWindow"/> may keep
/// alive and navigate back into, instead of constructing a fresh one on every click of its nav button.
/// </summary>
/// <remarks>
/// <para>Keeping the page preserves scroll position, folder chip, search text and selection, and
/// turns a reopen into a background refresh: WPF raises <c>Loaded</c> again when the page is
/// navigated back into, and <see cref="Views.PageState"/> uses its quiet <c>Refreshing</c> state
/// while it still has data.</para>
/// <para>Opt-in, because a page written to be thrown away can break once it is reused. A page
/// must:</para>
/// <list type="number">
/// <item>Subscribe to global events in <c>Loaded</c>, not the constructor, to pair with the
/// <c>Unloaded</c> removals; otherwise the subscriptions are dead after the first navigation.</item>
/// <item>Replace any <see cref="System.Threading.CancellationTokenSource"/> cancelled in
/// <c>Unloaded</c>, either there or at the top of <c>Loaded</c>.</item>
/// <item>Re-run its load in <c>Loaded</c>, so a reopen refreshes.</item>
/// <item>Hold nothing invalidated by state it can't observe. For example, <c>PackListView</c>'s
/// cards are built from paths under the instances root, so moving that folder must go through
/// <see cref="MainWindow.EvictMaster"/>.</item>
/// </list>
/// <para>The frame's journal isn't used for this: <c>MainWindow</c> drains it, because its
/// keep-alive entries rooted every detail page along with its web host. The master pages are held
/// in an explicit dictionary instead.</para>
/// </remarks>
public interface IReusablePage;
