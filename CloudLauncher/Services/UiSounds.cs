using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CloudLauncher.Services;

/// <summary>
/// Click sounds for the Slate style: a soft low click on buttons and a quieter tick on switches,
/// check boxes and menu items, like the mod's <c>SlateSounds.click/tick</c>.
/// </summary>
/// <remarks>
/// <para>The sounds are synthesised at start-up because Minecraft's own click is not ours to ship.</para>
/// <para>A class handler on <see cref="ButtonBase.ClickEvent"/> covers every button in every window,
/// including ones built in code. It also listens to handled events, since most buttons' own handlers
/// mark the click handled.</para>
/// <para><see cref="SoundPlayer"/> plays asynchronously, off the UI thread, and each sound has its
/// own player so a click never cuts a tick short.</para>
/// </remarks>
public static class UiSounds
{
    private static SoundPlayer? _click;
    private static SoundPlayer? _tick;
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            _click = Load(Synthesise(frequency: 330, overtone: 2.02, lengthMs: 55, volume: 0.30, noise: 0.35));
            _tick = Load(Synthesise(frequency: 720, overtone: 1.51, lengthMs: 28, volume: 0.13, noise: 0.25));
            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
                new RoutedEventHandler(OnClick), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent,
                new RoutedEventHandler(OnMenuClick), handledEventsToo: true);
        }
        catch (Exception ex)
        {
            // No sound device, or a locked-down session: stay quiet.
            AppLog.LogError("ui-sounds", ex);
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        // Routed events bubble: only the button that was actually clicked plays.
        if (!ReferenceEquals(sender, e.OriginalSource) || !LookState.Current.UiSounds) return;
        Play(sender is ToggleButton ? _tick : _click);
    }

    private static void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || !LookState.Current.UiSounds) return;
        Play(_tick);
    }

    private static void Play(SoundPlayer? player)
    {
        try { player?.Play(); }
        catch { /* a sound is never worth an error */ }
    }

    private static SoundPlayer Load(byte[] wav)
    {
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    /// <summary>A short percussive tone as a 16-bit mono WAV: a sine plus one overtone with a fast
    /// exponential decay, and a little noise in the first milliseconds for the click edge.</summary>
    private static byte[] Synthesise(double frequency, double overtone, int lengthMs, double volume, double noise)
    {
        const int rate = 44100;
        var samples = rate * lengthMs / 1000;
        var rng = new Random(1234);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            var env = Math.Exp(-t * 1000.0 / (lengthMs * 0.28));
            var attack = Math.Min(1.0, i / (rate * 0.0015));
            var tone = Math.Sin(2 * Math.PI * frequency * t) * 0.75
                       + Math.Sin(2 * Math.PI * frequency * overtone * t) * 0.25;
            var burst = (rng.NextDouble() * 2 - 1) * noise * Math.Exp(-t * 1000.0 / 3.0);
            var v = (tone + burst) * env * attack * volume;
            w.Write((short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}
