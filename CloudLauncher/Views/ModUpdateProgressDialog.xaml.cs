using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// What "Update all" looks like while it runs: one row and one bar per mod, several moving at once,
/// plus an overall bar. Replaces the single status line that only ever named the mod currently being
/// fetched — with updates running in parallel there is no single "current mod", and with forty of
/// them a line of text is no way to tell whether anything is still happening.
/// </summary>
public partial class ModUpdateProgressDialog : UserControl
{
    private readonly List<ModUpdateRunner.Job> _jobs;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ModUpdateRunner.Summary> _tcs = new();
    private readonly DispatcherTimer _tick;
    private readonly int _concurrency;

    public ModUpdateProgressDialog(PackDetail pack, IReadOnlyList<(PackMod Mod, ModVersion Target)> updates, int concurrency)
    {
        InitializeComponent();
        _concurrency = concurrency;
        _jobs = updates.Select(u => new ModUpdateRunner.Job(u.Mod, u.Target)).ToList();
        JobList.ItemsSource = _jobs;

        TitleLabel.Text = $"Updating mods · {pack.Name}";
        SubLabel.Text = $"{_jobs.Count} mod(s), {concurrency} at a time. " +
                        "Change how many run together in Settings → Downloads.";
        FooterNote.Text = "You can keep using the launcher while this runs.";

        // The overall bar is recomputed on a timer rather than on every byte: each job already
        // throttles its own notifications, and summing forty of them per chunk would be the most
        // expensive thing in the batch.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => RefreshOverall();
        _tick.Start();

        Loaded += (_, _) => Animate.SlideFadeIn(this, 0, 14, 200);
        RefreshOverall();
    }

    public Task<ModUpdateRunner.Summary> Result => _tcs.Task;

    /// <summary>Runs the batch behind the card and returns its summary. The card closes itself when
    /// everything has finished and nothing failed; a failure leaves it up so the rows can be read.</summary>
    public static async Task<ModUpdateRunner.Summary> RunAsync(
        MainWindow host, PackDetail pack, IReadOnlyList<(PackMod Mod, ModVersion Target)> updates)
    {
        var card = new ModUpdateProgressDialog(pack, updates, App.State.Settings.EffectiveModDownloadConcurrency);
        _ = card.StartAsync();
        // No backdrop cancel: a mis-click outside must not abandon a half-finished batch of updates.
        await host.ShowCardAsync(card, card.Result);
        return await card.Result;
    }

    private async Task StartAsync()
    {
        ModUpdateRunner.Summary summary;
        try
        {
            summary = await ModUpdateRunner.RunAsync(_jobs, _concurrency, _cts.Token);
        }
        catch (Exception ex)
        {
            summary = new ModUpdateRunner.Summary(0, new[] { ex.Message }, _cts.IsCancellationRequested);
        }

        _tick.Stop();
        RefreshOverall();
        CancelButton.Visibility = Visibility.Collapsed;
        FooterNote.Text = summary.Describe();

        // Nothing went wrong and nothing to read: get out of the way. Otherwise wait to be dismissed.
        if (summary.Failed.Count == 0)
        {
            await Task.Delay(500);
            _tcs.TrySetResult(summary);
            return;
        }
        CloseButton.Visibility = Visibility.Visible;
        _pending = summary;
    }

    private ModUpdateRunner.Summary? _pending;

    private void RefreshOverall()
    {
        var total = _jobs.Count;
        if (total == 0) return;
        var finished = _jobs.Count(j => j.IsFinished);
        var running = _jobs.Count(j => j.IsRunning);
        // A running job contributes its own fraction, so the bar keeps moving between completions.
        var fraction = _jobs.Sum(j => j.IsFinished ? 1.0 : j.IsRunning && !j.IsIndeterminate ? j.Percent / 100.0 : 0.0) / total;

        OverallBar.Value = Math.Clamp(fraction * 100, 0, 100);
        OverallPercent.Text = $"{fraction * 100:0}%";
        OverallLabel.Text = finished >= total
            ? $"Finished {finished} of {total}"
            : $"{finished} of {total} done · {running} downloading";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        FooterNote.Text = "Stopping — downloads already under way will finish.";
    }

    private void OnClose(object sender, RoutedEventArgs e) =>
        _tcs.TrySetResult(_pending ?? new ModUpdateRunner.Summary(0, Array.Empty<string>(), true));
}
