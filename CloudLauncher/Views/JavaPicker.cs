using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using Microsoft.Win32;

namespace CloudLauncher.Views;

public enum JavaChoiceKind { Auto, Install, Browse }

/// <summary>One row of a Java runtime picker: automatic, a detected installation, or "browse".</summary>
public sealed record JavaChoice(string Label, string? Path, JavaChoiceKind Kind);

/// <summary>Shared plumbing for the two Java pickers (the launcher default in Settings and the
/// per-instance override on a pack's Options tab): the same detected-installations list, the same
/// file dialog, the same way of showing a stored path that isn't among the detected ones.</summary>
public static class JavaPicker
{
    public static async Task<List<JavaChoice>> BuildChoicesAsync(string autoLabel, bool refresh = false)
    {
        var list = new List<JavaChoice> { new(autoLabel, null, JavaChoiceKind.Auto) };
        IReadOnlyList<LaunchService.JavaInstall> installs;
        try { installs = await LaunchService.DetectJavaInstallsAsync(refresh); }
        catch { installs = Array.Empty<LaunchService.JavaInstall>(); }
        foreach (var j in installs)
            list.Add(new JavaChoice($"Java {j.Major}  ·  {j.Path}{(j.Managed ? "   (downloaded by the launcher)" : "")}", j.Path, JavaChoiceKind.Install));
        list.Add(new JavaChoice("Browse for java.exe…", null, JavaChoiceKind.Browse));
        return list;
    }

    /// <summary>Binds the choices and selects the one matching <paramref name="currentPath"/>; a stored
    /// path that isn't among the detected installations is added so it can still be shown and kept.</summary>
    public static void Apply(ComboBox box, List<JavaChoice> choices, string? currentPath)
    {
        JavaChoice? match = null;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            match = choices.FirstOrDefault(c => c.Kind == JavaChoiceKind.Install
                                                && string.Equals(c.Path, currentPath, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = new JavaChoice($"Java  ·  {currentPath}", currentPath, JavaChoiceKind.Install);
                choices.Insert(choices.Count - 1, match);
            }
        }
        box.ItemsSource = null;
        box.ItemsSource = choices;
        box.SelectedItem = match ?? choices[0];
    }

    public static string? Browse(Window? owner)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a Java executable",
            Filter = "Java executable (java.exe, javaw.exe)|java.exe;javaw.exe|All files|*.*",
            CheckFileExists = true
        };
        var ok = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
        return ok == true ? dlg.FileName : null;
    }
}
