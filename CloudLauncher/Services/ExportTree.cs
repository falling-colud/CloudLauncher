using System.IO;

namespace CloudLauncher.Services;

/// <summary>One file or folder under an instance's <c>game/</c>, as the export card's tree shows
/// it.</summary>
/// <remarks>Built off the UI thread by <see cref="ExportTree.Walk"/> and handed over whole through
/// <see cref="ExportTreeNode.Adopt"/>, so the walker never writes a node the card can see.</remarks>
public sealed class ExportTreeNode
{
    public required string Name { get; init; }

    /// <summary>Relative to <c>game/</c>, <c>/</c>-separated: the key <see cref="ExportSelection"/>
    /// uses.</summary>
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }
    public required bool IsFolder { get; init; }

    /// <summary>A file's size, or everything under a folder once it has been walked.</summary>
    public long Bytes { get; private set; }

    /// <summary>1 for a file; the number of files under a folder once it has been walked.</summary>
    public int Files { get; private set; }

    /// <summary>A folder's entries, folders first and then files, each by name. Empty until walked.</summary>
    public IReadOnlyList<ExportTreeNode> Children { get; private set; } = [];

    /// <summary>A folder's whole subtree is known. Always true for a file.</summary>
    public bool Walked { get; private set; }

    /// <summary>Held back by <see cref="PrivateAssetPolicy"/> whatever is ticked: a private file, or a
    /// folder with nothing in it that is not private.</summary>
    public bool IsPrivate { get; private set; }

    /// <summary>Private files under this folder (the file itself, for a file).</summary>
    public int PrivateFiles { get; private set; }

    public static ExportTreeNode ForFile(string name, string relativePath, string fullPath, long bytes, bool isPrivate) => new()
    {
        Name = name, RelativePath = relativePath, FullPath = fullPath, IsFolder = false,
        Bytes = bytes, Files = 1, Walked = true, IsPrivate = isPrivate, PrivateFiles = isPrivate ? 1 : 0
    };

    public static ExportTreeNode ForFolder(string name, string relativePath, string fullPath) => new()
    {
        Name = name, RelativePath = relativePath, FullPath = fullPath, IsFolder = true
    };

    internal void Complete(List<ExportTreeNode> children)
    {
        Children = children;
        Bytes = children.Sum(c => c.Bytes);
        Files = children.Sum(c => c.Files);
        PrivateFiles = children.Sum(c => c.PrivateFiles);
        IsPrivate = Files > 0 && PrivateFiles == Files;
        Walked = true;
    }

    /// <summary>Takes a walked copy's results. UI thread only: this is when the walk becomes
    /// visible.</summary>
    public void Adopt(ExportTreeNode walked)
    {
        Children = walked.Children;
        Bytes = walked.Bytes;
        Files = walked.Files;
        PrivateFiles = walked.PrivateFiles;
        IsPrivate = walked.IsPrivate;
        Walked = walked.Walked;
    }
}

/// <summary>Reading an instance folder into <see cref="ExportTreeNode"/>s, and adding up a selection.</summary>
public static class ExportTree
{
    /// <summary>Matches the export's own walk: hidden files are included, and links (junctions,
    /// symlinks) are never followed, since they can point anywhere on the machine.</summary>
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>Half-downloaded files, which the export leaves out and so the tree does not offer.</summary>
    public static bool IsPartial(string name) =>
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".cldownload", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a whole folder, however deep. Call it off the UI thread and
    /// <see cref="ExportTreeNode.Adopt"/> the result on it.</summary>
    public static ExportTreeNode Walk(string fullPath, string relativePath, IReadOnlyList<string> privatePatterns,
                                      CancellationToken ct)
    {
        var folder = ExportTreeNode.ForFolder(Path.GetFileName(fullPath), relativePath, fullPath);
        Fill(folder, privatePatterns, ct);
        return folder;
    }

    private static void Fill(ExportTreeNode folder, IReadOnlyList<string> privatePatterns, CancellationToken ct)
    {
        var children = new List<ExportTreeNode>();
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(folder.FullPath).EnumerateFileSystemInfos("*", OneLevel); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            folder.Complete(children);
            return;
        }

        try
        {
            foreach (var info in entries)
            {
                ct.ThrowIfCancellationRequested();
                var rel = folder.RelativePath + "/" + info.Name;
                if (info is DirectoryInfo)
                {
                    var sub = ExportTreeNode.ForFolder(info.Name, rel, info.FullName);
                    Fill(sub, privatePatterns, ct);
                    children.Add(sub);
                }
                else if (!IsPartial(info.Name))
                {
                    long bytes = 0;
                    try { bytes = ((FileInfo)info).Length; } catch { /* vanished mid-walk */ }
                    children.Add(ExportTreeNode.ForFile(info.Name, rel, info.FullName, bytes, IsPrivate(rel, privatePatterns)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went away or locked up mid-listing: what was read is still what is there.
        }

        children.Sort(static (a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        folder.Complete(children);
    }

    public static bool IsPrivate(string relativePath, IReadOnlyList<string> privatePatterns)
    {
        foreach (var pattern in privatePatterns)
            if (PackRuleService.PatternMatches(relativePath, pattern)) return true;
        return false;
    }

    /// <summary>Files and bytes the selection sends, from the walked tree. <c>Complete</c> is false
    /// while a folder the selection reaches into has not been read yet; the numbers then cover only
    /// what has.</summary>
    /// <remarks>Private files are not counted: the export holds them back whatever is ticked.</remarks>
    public static (int Files, long Bytes, bool Complete) Included(IEnumerable<ExportTreeNode> roots, ExportSelection selection)
    {
        var files = 0;
        long bytes = 0;
        var complete = true;
        foreach (var root in roots) Visit(root);
        return (files, bytes, complete);

        void Visit(ExportTreeNode node)
        {
            if (!node.IsFolder)
            {
                if (!node.IsPrivate && selection.IsIncluded(node.RelativePath)) { files++; bytes += node.Bytes; }
                return;
            }
            var state = selection.StateOf(node.RelativePath);
            if (state == false) return;
            if (!node.Walked) { complete = false; return; }
            if (state == true && node.PrivateFiles == 0) { files += node.Files; bytes += node.Bytes; return; }
            foreach (var child in node.Children) Visit(child);
        }
    }
}
