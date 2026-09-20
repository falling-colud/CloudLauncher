using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// One entry in an instance's multiplayer list.
/// </summary>
/// <param name="SourcePackId">The instance whose <c>servers.dat</c> this came out of.</param>
/// <param name="Index">Where it sits in that file's list, which is also the order the game shows and
/// the only identity an entry has — <c>servers.dat</c> stores no ids.</param>
/// <param name="Hidden">True for the entries Minecraft writes when you use Direct Connect. The game
/// keeps them but never draws them; the Servers page shows them behind a toggle, because "the address
/// I typed in once last week" is exactly the thing people come looking for.</param>
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

    /// <summary>The address in the form used as a key across instances: one server that appears in
    /// four instances is one server, and its console credentials and ping belong to the address.</summary>
    public string Key => AppSettings.ServerKey(Address);
}

/// <summary>
/// Reads and writes the multiplayer server lists Minecraft keeps inside each instance.
/// </summary>
/// <remarks>
/// <para>The list lives at <c>&lt;instance game dir&gt;/servers.dat</c>, an uncompressed NBT file whose
/// root holds a single list named <c>servers</c>. <see cref="Nbt"/> does the format; this does the
/// meaning, including writing the file back the way it was found and keeping Minecraft's own
/// <c>servers.dat_old</c> backup convention.</para>
/// <para><c>servers.dat</c> is <see cref="RuleAction.Ignored"/> in the default pack rules, so it is a
/// per-machine file and nothing written here is ever synced to a team. That is deliberate and the UI
/// says so: adding a server for yourself must not add it for everyone in a shared pack.</para>
/// <para>A missing or unreadable file is an ordinary outcome — an instance that has never been
/// launched has no server list — and reads answer with an empty list rather than throwing.</para>
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

    /// <summary>Every server across the given instances. Reads files, so callers run it off the UI
    /// thread — a dozen instances on a slow disk is not free.</summary>
    public List<ServerEntry> ScanAll(IEnumerable<PackSummary> instances)
    {
        var all = new List<ServerEntry>();
        foreach (var pack in instances)
        {
            try { all.AddRange(Read(pack.Id, pack.Name)); }
            catch (IOException) { /* one unreadable instance must not empty the whole page */ }
            catch (UnauthorizedAccessException) { }
        }
        return all;
    }

    /// <summary>Appends a server to an instance's list, creating the file if the instance has none.</summary>
    public void Add(Guid packId, string name, string address, string? iconBase64 = null)
    {
        Edit(packId, list =>
        {
            list.Children.Add(BuildEntry(name, address, iconBase64, hidden: false, acceptTextures: null));
        });
    }

    /// <summary>
    /// Renames or re-addresses an existing entry in place.
    /// </summary>
    /// <remarks>In place matters: the order of <c>servers.dat</c> is the order of the multiplayer
    /// screen, and a player who has dragged their servers into an order they like should not find them
    /// reshuffled because they fixed a typo. The entry's icon and texture-prompt answer are kept for
    /// the same reason.</remarks>
    public bool Update(ServerEntry entry, string name, string address)
    {
        var changed = false;
        Edit(entry.SourcePackId, list =>
        {
            if (Locate(list, entry) is not { } tag) return;
            SetString(tag, "name", name);
            SetString(tag, "ip", address);
            changed = true;
        });
        return changed;
    }

    /// <summary>Removes an entry from its instance's list.</summary>
    public bool Remove(ServerEntry entry)
    {
        var removed = false;
        Edit(entry.SourcePackId, list =>
        {
            if (Locate(list, entry) is not { } tag) return;
            removed = list.Children.Remove(tag);
        });
        return removed;
    }

    /// <summary>
    /// Copies an entry into another instance's list, icon and all.
    /// </summary>
    /// <returns>False when that instance already lists the same address, which is a no-op rather than
    /// a duplicate row in the player's multiplayer screen.</returns>
    public bool CopyTo(ServerEntry entry, Guid targetPackId)
    {
        var copied = false;
        Edit(targetPackId, list =>
        {
            var key = AppSettings.ServerKey(entry.Address);
            if (list.Children.Any(c => AppSettings.ServerKey(c["ip"]?.AsString() ?? "") == key)) return;
            list.Children.Add(BuildEntry(entry.Name, entry.Address, entry.IconBase64,
                                         hidden: false, entry.AcceptTextures));
            copied = true;
        });
        return copied;
    }

    /// <summary>Moves an entry up or down the instance's list, which is the order the game shows.</summary>
    public bool Move(ServerEntry entry, int delta)
    {
        var moved = false;
        Edit(entry.SourcePackId, list =>
        {
            if (Locate(list, entry) is not { } tag) return;
            var from = list.Children.IndexOf(tag);
            var to = from + delta;
            if (to < 0 || to >= list.Children.Count) return;
            list.Children.RemoveAt(from);
            list.Children.Insert(to, tag);
            moved = true;
        });
        return moved;
    }

    // ── the file ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the instance's list, hands it to <paramref name="mutate"/>, and writes it back.
    /// </summary>
    /// <remarks>Deliberately re-reads rather than trusting the rows on screen: the game may have been
    /// running since the page loaded and rewritten the file on exit. The write preserves whatever
    /// compression the file used, because Minecraft writes this one uncompressed and silently drops a
    /// gzipped one.</remarks>
    private void Edit(Guid packId, Action<NbtTag> mutate)
    {
        var path = FileFor(packId);
        var root = Nbt.ReadFile(path, out var compression);
        if (root is null)
        {
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

        mutate(list);
        Nbt.WriteFile(path, root, compression);
    }

    /// <summary>
    /// Finds the tag an on-screen row refers to in a freshly read list.
    /// </summary>
    /// <remarks>Index first, because that is the row's identity and duplicates of one address are
    /// legal; the address is then verified, and only if it disagrees does this fall back to searching
    /// by address. That covers the case where the game added or removed a server while the page was
    /// open, which would otherwise silently edit the wrong entry.</remarks>
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
