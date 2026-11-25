namespace CloudLauncher.Platform;

/// <summary>WPF's decode options, which the shared image helpers take as a parameter. The Linux decoder
/// (Skia, through Avalonia) has no use for them; they exist so the calls compile unchanged.</summary>
[Flags]
public enum BitmapCreateOptions
{
    None = 0,
    PreservePixelFormat = 1,
    IgnoreColorProfile = 2,
    IgnoreImageCache = 4,
    DelayCreation = 8
}
