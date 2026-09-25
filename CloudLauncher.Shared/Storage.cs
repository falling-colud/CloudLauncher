namespace CloudLauncher.Shared;

/// <summary>The four families of content the server keeps blobs for, spelled as
/// <see cref="CloudStorageItem.Kind"/> reports them.</summary>
/// <remarks>Strings rather than an enum, so an older launcher can receive a new kind and label it
/// "other" instead of failing to read the response.</remarks>
public static class CloudStorageKinds
{
    public const string Pack = "pack";
    public const string World = "world";
    public const string ResourcePack = "resourcepack";
    public const string Mod = "mod";
}

/// <summary>One thing the user owns, and what it is costing them on the server.</summary>
/// <param name="Kind">One of <see cref="CloudStorageKinds"/>.</param>
/// <param name="Id">The pack, world, resource pack or mod id, so a row can link to the thing it
/// describes. Unique only within a <paramref name="Kind"/>.</param>
/// <param name="Name">The item's name as its own page shows it.</param>
/// <param name="UniqueBytes">Bytes nothing else the user owns references: what deleting this item
/// would free.</param>
/// <param name="SharedBytes">Bytes this item references that another of the user's items references
/// too. Blobs are stored once, but these are counted in full against every item using them, so this
/// column sums to more than the account uses. Deleting one referent frees none of it.</param>
/// <param name="Versions">How many rows sit behind the item: uploaded versions for a mod, world or
/// resource pack, manifest entries (files) for a pack. Zero when nothing has been uploaded yet.</param>
/// <remarks>Shared bytes are not split between referents: deleting one of two packs frees none of a
/// shared blob, so showing half against each would be misleading.</remarks>
public sealed record CloudStorageItem(
    string Kind,
    Guid Id,
    string Name,
    long UniqueBytes,
    long SharedBytes,
    int Versions);

/// <summary>Where the user's server storage went, item by item.</summary>
/// <param name="UsedBytes">The same figure <c>auth/me/usage</c> returns, computed from the same
/// sources and filters, so the page can account for all of it.</param>
/// <param name="QuotaBytes">Their quota, or 0 for unlimited (not full). Check before dividing.</param>
/// <param name="Items">Every item the user owns, biggest footprint first, including ones costing
/// nothing (a shared pack with no files, a mod page with no versions). Unshared instances are left
/// out, since the server holds nothing for them.</param>
/// <remarks>
/// <para><c>Items.Sum(UniqueBytes) + SharedOnceBytes == UsedBytes</c>. The per-item columns don't add
/// up to <paramref name="UsedBytes"/>, since each shared blob counts against every item using it.</para>
/// <para>Content bundles are left out, here and in <paramref name="UsedBytes"/>; the total has never
/// charged for them.</para>
/// </remarks>
public sealed record CloudStorageBreakdown(
    long UsedBytes,
    long QuotaBytes,
    IReadOnlyList<CloudStorageItem> Items)
{
    /// <summary>Bytes referenced by more than one item, counted once: the part of
    /// <see cref="UsedBytes"/> no single deletion can reclaim.</summary>
    /// <remarks>Derived from the other two numbers rather than sent over the wire, so it can't
    /// contradict them.</remarks>
    public long SharedOnceBytes => Math.Max(0, UsedBytes - Items.Sum(i => i.UniqueBytes));
}
