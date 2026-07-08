namespace CloudLauncher.Views;

public sealed class RichDescriptionOptions
{
    public bool EnableCommandRun { get; init; }
    public Action<string>? OnCommandRun { get; init; }
}
