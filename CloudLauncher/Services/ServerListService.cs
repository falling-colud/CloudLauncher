using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// One entry in an instance's multiplayer list.
/// </summary>
/// <param name="SourcePackId">The instance whose <c>servers.dat</c> this came out of.</param>
/// <param name="Index">Position in that file's list: the order the game shows, and the entry's only
/// identity, since <c>servers.dat</c> stores no ids.</param>
/// <param name="Hidden">True for the entries Minecraft writes for Direct Connect. The game keeps them
/// but never shows them; the Servers page shows them behind a toggle.</param>
public sealed record ServerEntry(
    Guid SourcePackId,
    string SourcePackName,
    int Index,
    string Name,
    string Address,
    string? IconBase64,
    bool Hidden,
    bool? AcceptTextures)
{
    public string Host => MinecraftServerPing.ParseAddress(Address).Host;
    public int Port => MinecraftServerPing.ParseAddress(Address).Port;

    /// <summary>The address as a key across instances: the same server in several instances is one
    /// server, and its console credentials and ping belong to the address.</summary>
    public string Key => AppSettings.ServerKey(Address);
}

/// <summary>Reads and writes the multiplayer server lists Minecraft keeps inside each
/// instance.</summary>
/// <remarks>
/// <para>The list is <c>&lt;instance game dir&gt;/servers.dat</c>, an uncompressed NBT file with one
/// list named <c>servers</c>. Writes keep the file's format and Minecraft's <c>servers.dat_old</c>
/// backup. The file is per-machine (<see cref="RuleAction.Ignored"/> in the default rules), so it is
/// never synced.</para>
/// <para>Reads treat a missing or unreadable file as empty; writes throw on an unreadable file
/// rather than replace the player's list (see <see cref="Edit"/>).</para>
/// </remarks>
public sealed class ServerListService(PackFolderService packs)
{
    private const string ListTag = "servers";

    public string FileFor(Guid packId) => Path.Combine(packs.GameDir(packId), "servers.dat");

    /// <summary>Every server in one instance's list, in the order the game shows them.</summary>
    public List<ServerEntry> Read(Guid packId, string packName)
    {
        var results = new List<ServerEntry>();
        var root = Nbt.ReadFile(FileFor(packId));
        if (root?[ListTag] is not { } list) return results;

        for (var i = 0; i < list.Children.Count; i++)
        {
            var entry = list.Children[i];
            var address = entry["ip"]?.AsString() ?? "";
            if (string.IsNullOrWhiteSpace(address)) continue;   // an entry with no address cannot be joined
            results.Add(new ServerEntry(
                packId,
                packName,
                i,
                entry["name"]?.AsString() ?? address,
                address.Trim(),
                entry["icon"]?.AsString(),
                entry["hidden"]?.AsBool() ?? false,
                entry["acceptTextures"]?.AsBool()));
        }
        return results;
    }

    /// <summary>Every server across the given instances. Reads files, so call it off the UI
    /// thread.</summary>
    public List<ServerEntry> ScanAll(IEnumerable<PackSummary> instances) => ScanAll(instances, out _);

    /// <summary>Every server across the given instances, plus the instances that could not be
    /// read.</summary>
    /// <param name="unreadable">Names of instances whose list threw. The caller shows the count so their
    /// servers don't just look deleted.</param>
    /// <remarks>Catches everything per instance, so one broken instance can't empty the page.</remarks>
    public List<ServerEntry> ScanAll(IEnumerable<PackSummary> instances, out List<string> unreadable)
    {
        var all = new List<ServerEntry>();
        unreadable = new List<string>();
        foreach (var pack in instances)
        {
            try { all.AddRange(Read(pack.Id, pack.Name)); }
            catch (Exception) { unreadable.Add(pack.Name); }
        }
        return all;
    }

    /// <summary>Appends a server to an instance's list, creating the file if the instance has none.</summary>
    public void Add(Guid packId, string name, string address, string? iconBase64 = null)
    {
        Edit(packId, list =>
        {
            list.Children.Add(BuildEntry(name, address, iconBase64, hidden: false, acceptTextures: null));
            return true;
        });
    }

    /// <summary>
    /// Renames or re-addresses an existing entry in place.
    /// </summary>
    /// <remarks>In place so the player's order on the multiplayer screen survives a typo fix. The
    /// entry's icon and texture-prompt answer are kept too.</remarks>
    public bool Update(ServerEntry entry, string name, string address) =>
        Edit(entry.SourcePackId, list =>
        {
            if (Locate(list, entry) is not { } tag) return false;
            SetString(tag, "name", name);
            SetString(tag, "ip", address);
            return true;
        });

    /// <summary>Removes an entry from its instance's list.</summary>
    public bool Remove(ServerEntry entry) =>
        Edit(entry.SourcePackId, list =>
        {
            if (Locate(list, entry) is not { } tag) return false;
            return list.Children.Remove(tag);
        });

    /// <summary>
    /// Copies an entry into another instance's list, icon and all.
    /// </summary>
    /// <returns>False when that instance already lists the same address; nothing is added.</returns>
    public bool CopyTo(ServerEntry entry, Guid targetPackId) =>
        Edit(targetPackId, list =>
        {
            var key = AppSettings.ServerKey(entry.Address);
            if (list.Children.Any(c => AppSettings.ServerKey(c["ip"]?.AsString() ?? "") == key)) return false;
            list.Children.Add(BuildEntry(entry.Name, entry.Address, entry.IconBase64,
                                         hidden: false, entry.AcceptTextures));
            return true;
        });

    /// <summary>Moves an entry up or down the instance's list, which is the order the game shows.</summary>
    public bool Move(ServerEntry entry, int delta) =>
        Edit(entry.SourcePackId, list =>
        {
            if (Locate(list, entry) is not { } tag) return false;
            var from = list.Children.IndexOf(tag);
            var to = from + delta;
            if (to < 0 || to >= list.Children.Count) return false;
            list.Children.RemoveAt(from);
            list.Children.Insert(to, tag);
            return true;
        });

    // ── the file ─────────────────────────────────────────────────────────────

    /// <summary>Re-reads the instance's list, hands it to <paramref name="mutate"/>, and writes it
    /// back if <paramref name="mutate"/> reports a change.</summary>
    /// <returns>What <paramref name="mutate"/> returned: true when the file was rewritten.</returns>
    /// <exception cref="IOException">The instance has a <c>servers.dat</c> that could not be read. It
    /// must never be treated as "no servers yet".</exception>
    /// <remarks>Re-reads because the game may have rewritten the file, and keeps its compression
    /// (Minecraft ignores a gzipped one). An empty list is only created when the file doesn't exist:
    /// <see cref="Nbt.ReadFile(string, out NbtCompression)"/> also returns null for a locked or
    /// half-written file, and treating that as empty would wipe the player's list.</remarks>
    private bool Edit(Guid packId, Func<NbtTag, bool> mutate)
    {
        var path = FileFor(packId);
        NbtTag? root;
        NbtCompression compression;

        if (File.Exists(path))
        {
            root = Nbt.ReadFile(path, out compression);
            if (root is null)
                throw new IOException(
                    $"Could not read this instance's server list ({path}). It may be open in Minecraft " +
                    "or held by another program - close the game and try again. Nothing was changed.");
        }
        else
        {
            // An instance that has never been launched has no servers.dat; creating one is correct.
            root = NbtTag.NewCompound();
            compression = NbtCompression.None;
        }

        var list = root[ListTag];
        if (list is null || list.Type != NbtTagType.List)
        {
            if (list is not null) root.Children.Remove(list);
            list = NbtTag.NewList(ListTag, NbtTagType.Compound);
            root.Children.Add(list);
        }

        if (!mutate(list)) return false;
        Nbt.WriteFile(path, root, compression);
        return true;
    }

    /// <summary>
    /// Finds the tag an on-screen row refers to in a freshly read list.
    /// </summary>
    /// <remarks>By index first, since duplicate addresses are legal. If the address at that index
    /// disagrees, falls back to searching by address, in case the game changed the list while the page
    /// was open.</remarks>
    private static NbtTag? Locate(NbtTag list, ServerEntry entry)
    {
        var key = AppSettings.ServerKey(entry.Address);
        if (entry.Index >= 0 && entry.Index < list.Children.Count)
        {
            var candidate = list.Children[entry.Index];
            if (AppSettings.ServerKey(candidate["ip"]?.AsString() ?? "") == key) return candidate;
        }
        return list.Children.FirstOrDefault(c => AppSettings.ServerKey(c["ip"]?.AsString() ?? "") == key);
    }

    private static NbtTag BuildEntry(string name, string address, string? iconBase64, bool hidden,
                                     bool? acceptTextures)
    {
        var tag = NbtTag.NewCompound();
        // Minecraft writes name then ip; keeping the same order keeps a diff of the file readable.
        tag.Children.Add(NbtTag.NewString("name", name ?? ""));
        tag.Children.Add(NbtTag.NewString("ip", (address ?? "").Trim()));
        if (!string.IsNullOrWhiteSpace(iconBase64))
            tag.Children.Add(NbtTag.NewString("icon", iconBase64));
        if (acceptTextures is { } accept)
            tag.Children.Add(NbtTag.NewByte("acceptTextures", accept ? 1 : 0));
        if (hidden)
            tag.Children.Add(NbtTag.NewByte("hidden", 1));
        return tag;
    }

    private static void SetString(NbtTag compound, string name, string value)
    {
        if (compound[name] is { } existing && existing.Type == NbtTagType.String)
            existing.StringValue = value ?? "";
        else
            compound.Children.Add(NbtTag.NewString(name, value ?? ""));
    }
}
