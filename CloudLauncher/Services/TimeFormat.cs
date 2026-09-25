using System.Globalization;

namespace CloudLauncher.Services;

/// <summary>Every date and time the launcher shows, and every one it writes into a file name.</summary>
/// <remarks>
/// <para>Anything a person reads uses a standard specifier (<c>d</c>, <c>g</c>, <c>t</c>, <c>f</c>...)
/// with <see cref="CultureInfo.CurrentCulture"/>, i.e. the Windows locale. Anything a machine reads
/// back (backup folder names, version strings) uses <see cref="CultureInfo.InvariantCulture"/>;
/// otherwise a Thai or Saudi locale writes a year the invariant parse can't read.</para>
/// <para>There is no plain <see cref="DateTime"/> overload: callers disagree about
/// <see cref="DateTime.Kind"/>, and <c>ToLocalTime()</c> treats Unspecified as UTC.
/// <see cref="FromUtc"/> and <see cref="FromLocal"/> make the caller say what it has.</para>
/// <para>Every method here converts to local time itself, so callers must not convert first. Sort
/// on the <see cref="DateTimeOffset"/>, not on the returned string.</para>
/// </remarks>
public static class TimeFormat
{
    // ── reading a person's date and time ─────────────────────────────────────

    /// <summary>Date only, in the user's own order.</summary>
    /// <remarks>Short form, since several version tables pin their date column at 88-100 px.</remarks>
    public static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    /// <summary>Date and time.</summary>
    public static string DateTime(DateTimeOffset at) => at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Time of day, with a 12-hour clock where that is the norm.</summary>
    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Time of day with seconds.</summary>
    public static string TimeWithSeconds(DateTimeOffset at) => at.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    /// <summary>Long date plus time.</summary>
    public static string LongDateTime(DateTimeOffset at) => at.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    /// <summary>Month and day, no year.</summary>
    public static string MonthDay(DateTimeOffset at) => at.ToLocalTime().ToString("M", CultureInfo.CurrentCulture);

    /// <summary>Month and day for something from this year, the full date for anything older.</summary>
    /// <remarks>Without the year, an old backup's date would read as recent.</remarks>
    public static string MonthDayOrDate(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Year == DateTimeOffset.Now.Year ? MonthDay(at) : Date(at);
    }

    /// <summary>Weekday plus month and day.</summary>
    /// <remarks>Built by hand: there's no standard specifier for it, and a custom pattern would fix the
    /// order of the parts. Joined with the culture's list separator.</remarks>
    public static string WeekdayMonthDay(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        var weekday = culture.DateTimeFormat.GetDayName(local.DayOfWeek);
        return $"{weekday}{culture.TextInfo.ListSeparator} {MonthDay(at)}";
    }

    /// <summary>"just now", "3 hours ago", "on 14 Sep": the launcher's relative wording.</summary>
    /// <remarks>Delegates to <see cref="PackListCache.Describe"/>. The phrases stay English, like every
    /// other string in the app.</remarks>
    public static string? Ago(DateTimeOffset? at) => PackListCache.Describe(at);

    /// <summary>A heading for a day's worth of rows: "Today", "Yesterday", the weekday, or the date.</summary>
    public static string DayHeading(DateTimeOffset at)
    {
        var day = at.ToLocalTime().Date;
        var today = System.DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        // Within the last week the weekday is the useful label; after that it's ambiguous, so use the
        // date.
        return (today - day).TotalDays < 7 ? WeekdayMonthDay(at) : Date(at);
    }

    // ── writing something a machine reads back ───────────────────────────────

    /// <summary>A sortable stamp for a backup file or folder name. Always invariant, since it is parsed
    /// back with <see cref="CultureInfo.InvariantCulture"/>. Not for display.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Stamp(DateTimeOffset)"/>
    public static string StampNow() => Stamp(DateTimeOffset.Now);

    /// <summary>A generated version string, e.g. <c>2026.09.22.0007</c>. Always invariant, since
    /// versions are parsed and compared elsewhere.</summary>
    public static string VersionStamp(DateTimeOffset at) =>
        at.ToString("yyyy.MM.dd.HHmm", CultureInfo.InvariantCulture);

    /// <summary>A generated version string with no time part, e.g. <c>2026.09.22</c>.</summary>
    public static string VersionStampDate(DateTimeOffset at) =>
        at.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

    /// <summary>A log line's clock. Invariant so a log reads the same whoever sends it in.</summary>
    public static string LogClock(DateTimeOffset at) =>
        at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ── turning a DateTime into something this class will accept ─────────────

    /// <summary>Wraps a <see cref="DateTimeKind.Utc"/> (or unspecified-but-known-UTC) value.</summary>
    public static DateTimeOffset FromUtc(System.DateTime utc) =>
        new(System.DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    /// <summary>Wraps a local-time value, e.g. from <c>File.GetLastWriteTime</c> or the non-Utc
    /// <see cref="System.IO.FileSystemInfo"/> properties.</summary>
    public static DateTimeOffset FromLocal(System.DateTime local) =>
        new(System.DateTime.SpecifyKind(local, DateTimeKind.Local));

    /// <summary>Wraps a value whose <see cref="DateTime.Kind"/> is already set correctly.</summary>
    /// <remarks>Throws on <see cref="DateTimeKind.Unspecified"/> rather than guessing; use
    /// <see cref="FromUtc"/> or <see cref="FromLocal"/> instead.</remarks>
    public static DateTimeOffset From(System.DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => FromUtc(value),
        DateTimeKind.Local => FromLocal(value),
        _ => throw new ArgumentException(
            "DateTimeKind.Unspecified - call FromUtc or FromLocal so the offset is not guessed.",
            nameof(value)),
    };
}
