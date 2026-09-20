using System.IO;
using System.IO.Compression;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>
/// A small, complete reader and writer for Minecraft's NBT format.
/// </summary>
/// <remarks>
/// <para>The launcher needs two NBT files that no library in this project can read: an instance's
/// <c>servers.dat</c> (the multiplayer list — read AND written by the Servers page) and a world's
/// <c>level.dat</c> (seed, game mode, difficulty, version — read by the Worlds page). Both are small,
/// both are plain NBT, and pulling in a dependency for them would be heavier than the format is.</para>
/// <para>Everything is big-endian. <c>level.dat</c> is gzip-compressed; <c>servers.dat</c> is not, and
/// writing it compressed makes the game silently drop the file — so compression is detected on read
/// and preserved on write rather than assumed either way.</para>
/// <para>Strings are read as UTF-8. Java writes "modified UTF-8", which differs only for NUL and for
/// characters outside the BMP; neither appears in a server address, a world name or a version string.
/// A name that did contain one still round-trips, because the writer re-encodes exactly what the
/// reader decoded.</para>
/// </remarks>
public enum NbtTagType : byte
{
    End = 0, Byte = 1, Short = 2, Int = 3, Long = 4, Float = 5, Double = 6,
    ByteArray = 7, String = 8, List = 9, Compound = 10, IntArray = 11, LongArray = 12
}

/// <summary>One NBT tag. The payload lives in whichever field matches <see cref="Type"/>.</summary>
public sealed class NbtTag
{
    public NbtTagType Type { get; init; }
    public string Name { get; set; } = "";

    public long NumericValue { get; set; }
    public double RealValue { get; set; }
    public string StringValue { get; set; } = "";
    public byte[]? ByteArrayValue { get; set; }
    public int[]? IntArrayValue { get; set; }
    public long[]? LongArrayValue { get; set; }

    /// <summary>Children of a Compound, or the elements of a List.</summary>
    public List<NbtTag> Children { get; } = new();

    /// <summary>For a List: the type every element has. Meaningless on other tags.</summary>
    public NbtTagType ListElementType { get; set; } = NbtTagType.End;

    // ── lookups, written for the way callers actually use this ───────────────

    /// <summary>A direct child of a compound by name, or null. Case-sensitive, as NBT is.</summary>
    public NbtTag? this[string name] =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>A child looked up by any of several names, for keys that moved between versions.</summary>
    public NbtTag? Find(params string[] names)
    {
        foreach (var n in names)
            if (this[n] is { } hit) return hit;
        return null;
    }

    /// <summary>Depth-first search for the first descendant with this name. Use when the path differs
    /// between Minecraft versions (level.dat moved several keys in and out of "Data").</summary>
    public NbtTag? FindDeep(string name)
    {
        if (string.Equals(Name, name, StringComparison.Ordinal)) return this;
        foreach (var c in Children)
            if (c.FindDeep(name) is { } hit) return hit;
        return null;
    }

    public string? AsString() => Type == NbtTagType.String ? StringValue : null;
    public long? AsLong() => Type is NbtTagType.Byte or NbtTagType.Short or NbtTagType.Int or NbtTagType.Long
        ? NumericValue : null;
    public int? AsInt() => (int?)AsLong();
    public bool? AsBool() => AsLong() is { } n ? n != 0 : null;
    public double? AsDouble() => Type switch
    {
        NbtTagType.Float or NbtTagType.Double => RealValue,
        NbtTagType.Byte or NbtTagType.Short or NbtTagType.Int or NbtTagType.Long => NumericValue,
        _ => null
    };

    public static NbtTag NewCompound(string name = "") => new() { Type = NbtTagType.Compound, Name = name };
    public static NbtTag NewList(string name, NbtTagType elementType) =>
        new() { Type = NbtTagType.List, Name = name, ListElementType = elementType };
    public static NbtTag NewString(string name, string value) =>
        new() { Type = NbtTagType.String, Name = name, StringValue = value ?? "" };
    public static NbtTag NewByte(string name, int value) =>
        new() { Type = NbtTagType.Byte, Name = name, NumericValue = value };
    public static NbtTag NewInt(string name, int value) =>
        new() { Type = NbtTagType.Int, Name = name, NumericValue = value };
}

/// <summary>How an NBT file was stored, so a file can be written back the way it was found.</summary>
public enum NbtCompression { None, GZip, ZLib }

public static class Nbt
{
    /// <summary>
    /// Reads an NBT file. Returns the root compound, or null when the file is missing OR unreadable.
    /// </summary>
    /// <remarks>
    /// <para>Never throws for a malformed file: these are other programs' files, sometimes half-written
    /// by a crashed game, and one bad instance must not take a whole page down.</para>
    /// <para><b>null is deliberately ambiguous</b> — it means "no usable tree", which covers a file
    /// that is absent, locked by the game or a sync client, truncated, or not NBT at all. A caller
    /// about to WRITE the file back must not read null as "it was empty": check
    /// <see cref="File.Exists(string)"/> first and refuse rather than replace, or it will overwrite a
    /// list it merely failed to read. Callers that only display are free to treat null as empty.</para>
    /// </remarks>
    public static NbtTag? ReadFile(string path, out NbtCompression compression)
    {
        compression = NbtCompression.None;
        try
        {
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllBytes(path);
            return Read(raw, out compression);
        }
        // EndOfStreamException derives from IOException, but InvalidDataException does NOT — it comes
        // off SystemException — so it needs its own clause. Without it a corrupt gzip member or an
        // unknown tag byte escapes a method documented never to throw, and one bad level.dat takes
        // out the scan of every world in the instance.
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static NbtTag? ReadFile(string path) => ReadFile(path, out _);

    /// <summary>Reads NBT from bytes, detecting gzip/zlib/raw from the leading bytes.</summary>
    public static NbtTag? Read(byte[] raw, out NbtCompression compression)
    {
        compression = NbtCompression.None;
        if (raw.Length < 3) return null;

        Stream body;
        if (raw[0] == 0x1F && raw[1] == 0x8B)
        {
            compression = NbtCompression.GZip;
            body = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        }
        else if (raw[0] == 0x78)
        {
            // zlib: 0x78 then one of the usual FLG bytes. Written by some server tooling.
            compression = NbtCompression.ZLib;
            body = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        }
        else
        {
            body = new MemoryStream(raw);
        }

        // The catch has to sit OUTSIDE the finally, because disposing a GZipStream or ZLibStream over
        // corrupt data validates the trailer and throws InvalidDataException from Dispose itself — and
        // an exception raised in a finally block sails straight past that same try's catch clauses.
        try
        {
            try
            {
                using var reader = new BinaryReader(body);
                var type = (NbtTagType)reader.ReadByte();
                if (type != NbtTagType.Compound) return null;   // every real NBT file starts with one
                var name = ReadString(reader);
                var root = new NbtTag { Type = NbtTagType.Compound, Name = name };
                ReadCompoundBody(reader, root);
                return root;
            }
            finally { body.Dispose(); }
        }
        catch (IOException) { return null; }                  // truncated mid-tag
        catch (InvalidDataException) { return null; }         // corrupt deflate stream, or unknown tag byte
        catch (ArgumentOutOfRangeException) { return null; }  // a nonsense length in a corrupt file
    }

    /// <summary>
    /// Writes an NBT file, replacing it atomically.
    /// </summary>
    /// <remarks>Writes to a sibling temp file and renames, because the file being replaced is one the
    /// game reads at startup: a half-written servers.dat costs the player their whole server list.
    /// Minecraft itself keeps the previous copy as <c>servers.dat_old</c>; so does this.</remarks>
    public static void WriteFile(string path, NbtTag root, NbtCompression compression = NbtCompression.None,
                                 bool keepBackup = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Stream body = compression switch
            {
                NbtCompression.GZip => new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true),
                NbtCompression.ZLib => new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true),
                _ => file
            };
            try
            {
                using var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
                writer.Write((byte)NbtTagType.Compound);
                WriteString(writer, root.Name);
                WriteCompoundBody(writer, root);
            }
            finally { if (!ReferenceEquals(body, file)) body.Dispose(); }
        }

        if (keepBackup && File.Exists(path))
        {
            var old = path + "_old";
            try { File.Copy(path, old, overwrite: true); } catch (IOException) { }
        }
        File.Move(tmp, path, overwrite: true);
    }

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The largest array payload this reader will allocate for, in bytes.
    /// </summary>
    /// <remarks>Generous for the files the launcher reads — servers.dat and level.dat are kilobytes —
    /// and small enough that a corrupt or hostile file cannot exhaust memory. Without it a single
    /// flipped byte in a length field asks for a 2 GB allocation and takes the launcher down with an
    /// OutOfMemoryException, which is exactly what a file scan must never do.</remarks>
    private const int MaxArrayBytes = 64 * 1024 * 1024;

    /// <summary>Validates an element count read from the file before anything is allocated for it.</summary>
    private static int CheckCount(BinaryReader r, int count, int elementSize)
    {
        if (count < 0)
            throw new InvalidDataException($"Negative NBT array length ({count})");

        long bytes = (long)count * elementSize;
        if (bytes > MaxArrayBytes)
            throw new InvalidDataException($"NBT array of {count} elements exceeds the {MaxArrayBytes} byte limit");

        // When the length is knowable, an array claiming more than the file holds is corrupt, and
        // saying so here is cheaper and clearer than a truncated read further down.
        var stream = r.BaseStream;
        if (stream.CanSeek && bytes > stream.Length - stream.Position)
            throw new InvalidDataException($"NBT array of {count} elements runs past the end of the file");

        return count;
    }

    private static void ReadCompoundBody(BinaryReader r, NbtTag parent)
    {
        while (true)
        {
            var type = (NbtTagType)r.ReadByte();
            if (type == NbtTagType.End) return;
            var name = ReadString(r);
            parent.Children.Add(ReadPayload(r, type, name));
        }
    }

    private static NbtTag ReadPayload(BinaryReader r, NbtTagType type, string name)
    {
        var tag = new NbtTag { Type = type, Name = name };
        switch (type)
        {
            case NbtTagType.Byte: tag.NumericValue = (sbyte)r.ReadByte(); break;
            case NbtTagType.Short: tag.NumericValue = BE16(r); break;
            case NbtTagType.Int: tag.NumericValue = BE32(r); break;
            case NbtTagType.Long: tag.NumericValue = BE64(r); break;
            case NbtTagType.Float: tag.RealValue = BitConverter.Int32BitsToSingle(BE32(r)); break;
            case NbtTagType.Double: tag.RealValue = BitConverter.Int64BitsToDouble(BE64(r)); break;
            case NbtTagType.String: tag.StringValue = ReadString(r); break;

            case NbtTagType.ByteArray:
            {
                var n = CheckCount(r, BE32(r), sizeof(byte));
                tag.ByteArrayValue = n > 0 ? r.ReadBytes(n) : [];
                break;
            }
            case NbtTagType.IntArray:
            {
                var n = CheckCount(r, BE32(r), sizeof(int));
                var a = new int[n];
                for (var i = 0; i < a.Length; i++) a[i] = BE32(r);
                tag.IntArrayValue = a;
                break;
            }
            case NbtTagType.LongArray:
            {
                var n = CheckCount(r, BE32(r), sizeof(long));
                var a = new long[n];
                for (var i = 0; i < a.Length; i++) a[i] = BE64(r);
                tag.LongArrayValue = a;
                break;
            }
            case NbtTagType.List:
            {
                var elem = (NbtTagType)r.ReadByte();
                // One byte is the smallest a list element can be, so this bounds the element COUNT
                // without assuming anything about how big each element turns out to be.
                var n = CheckCount(r, BE32(r), sizeof(byte));
                tag.ListElementType = elem;
                for (var i = 0; i < n; i++) tag.Children.Add(ReadPayload(r, elem, ""));
                break;
            }
            case NbtTagType.Compound:
                ReadCompoundBody(r, tag);
                break;

            default:
                throw new InvalidDataException($"Unknown NBT tag type {(byte)type}");
        }
        return tag;
    }

    private static short BE16(BinaryReader r)
    {
        var b = r.ReadBytes(2);
        if (b.Length < 2) throw new EndOfStreamException();
        return (short)((b[0] << 8) | b[1]);
    }

    private static int BE32(BinaryReader r)
    {
        var b = r.ReadBytes(4);
        if (b.Length < 4) throw new EndOfStreamException();
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static long BE64(BinaryReader r)
    {
        var b = r.ReadBytes(8);
        if (b.Length < 8) throw new EndOfStreamException();
        long v = 0;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }

    /// <summary>
    /// Reads a Java "modified UTF-8" string: a 16-bit byte count followed by that many bytes.
    /// </summary>
    /// <remarks>
    /// Not plain UTF-8, and the difference is not academic here. Java encodes NUL as the two bytes
    /// <c>C0 80</c>, and encodes a character outside the BMP as its two UTF-16 surrogates, each in
    /// three bytes (CESU-8) rather than as one four-byte sequence. <see cref="Encoding.UTF8"/> turns
    /// both of those into U+FFFD, so decoding a world named with an emoji and writing it back — which
    /// is exactly what renaming a world does — would silently replace the name with question marks in
    /// the player's own level.dat. Decoding per UTF-16 code unit reassembles the surrogate pair
    /// correctly and costs nothing.
    /// </remarks>
    private static string ReadString(BinaryReader r)
    {
        var len = (ushort)BE16(r);
        if (len == 0) return "";
        var bytes = r.ReadBytes(len);
        if (bytes.Length < len) throw new EndOfStreamException();

        var sb = new StringBuilder(len);
        var i = 0;
        while (i < len)
        {
            int a = bytes[i];
            if (a < 0x80)
            {
                sb.Append((char)a);
                i += 1;
            }
            else if ((a & 0xE0) == 0xC0)
            {
                if (i + 1 >= len) throw new InvalidDataException("Truncated two-byte character in NBT string");
                sb.Append((char)(((a & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                i += 2;
            }
            else if ((a & 0xF0) == 0xE0)
            {
                if (i + 2 >= len) throw new InvalidDataException("Truncated three-byte character in NBT string");
                sb.Append((char)(((a & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                i += 3;
            }
            else
            {
                throw new InvalidDataException($"Invalid modified-UTF-8 lead byte 0x{a:X2} in NBT string");
            }
        }
        return sb.ToString();
    }

    // ── writing ──────────────────────────────────────────────────────────────

    private static void WriteCompoundBody(BinaryWriter w, NbtTag compound)
    {
        foreach (var child in compound.Children)
        {
            w.Write((byte)child.Type);
            WriteString(w, child.Name);
            WritePayload(w, child);
        }
        w.Write((byte)NbtTagType.End);
    }

    private static void WritePayload(BinaryWriter w, NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtTagType.Byte: w.Write((byte)(sbyte)tag.NumericValue); break;
            case NbtTagType.Short: WBE16(w, (short)tag.NumericValue); break;
            case NbtTagType.Int: WBE32(w, (int)tag.NumericValue); break;
            case NbtTagType.Long: WBE64(w, tag.NumericValue); break;
            case NbtTagType.Float: WBE32(w, BitConverter.SingleToInt32Bits((float)tag.RealValue)); break;
            case NbtTagType.Double: WBE64(w, BitConverter.DoubleToInt64Bits(tag.RealValue)); break;
            case NbtTagType.String: WriteString(w, tag.StringValue); break;

            case NbtTagType.ByteArray:
            {
                var a = tag.ByteArrayValue ?? [];
                WBE32(w, a.Length);
                w.Write(a);
                break;
            }
            case NbtTagType.IntArray:
            {
                var a = tag.IntArrayValue ?? [];
                WBE32(w, a.Length);
                foreach (var v in a) WBE32(w, v);
                break;
            }
            case NbtTagType.LongArray:
            {
                var a = tag.LongArrayValue ?? [];
                WBE32(w, a.Length);
                foreach (var v in a) WBE64(w, v);
                break;
            }
            case NbtTagType.List:
            {
                // An empty list still needs an element type. End is what Minecraft writes, and what it
                // accepts back; writing the declared type for an empty list is also legal.
                var elem = tag.Children.Count > 0 ? tag.Children[0].Type : tag.ListElementType;
                w.Write((byte)elem);
                WBE32(w, tag.Children.Count);
                foreach (var c in tag.Children) WritePayload(w, c);
                break;
            }
            case NbtTagType.Compound:
                WriteCompoundBody(w, tag);
                break;

            case NbtTagType.End:
                break;
        }
    }

    private static void WBE16(BinaryWriter w, short v)
    {
        w.Write((byte)(v >> 8)); w.Write((byte)v);
    }

    private static void WBE32(BinaryWriter w, int v)
    {
        w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v);
    }

    private static void WBE64(BinaryWriter w, long v)
    {
        for (var shift = 56; shift >= 0; shift -= 8) w.Write((byte)(v >> shift));
    }

    /// <summary>Writes a Java "modified UTF-8" string — the exact inverse of <see cref="ReadString"/>.</summary>
    /// <remarks>Encoding per UTF-16 code unit is what makes it modified UTF-8 rather than plain: a NUL
    /// becomes <c>C0 80</c> so it can never terminate the string, and an astral character becomes its
    /// two surrogates in three bytes each, which is what Java reads back. See the remark on
    /// <see cref="ReadString"/> for why this matters to a file the player owns.</remarks>
    private static void WriteString(BinaryWriter w, string? s)
    {
        s ??= "";
        var buffer = new List<byte>(s.Length + 8);
        foreach (var c in s)
        {
            if (c is >= '\u0001' and <= '\u007F')
            {
                buffer.Add((byte)c);
            }
            else if (c == '\0' || c <= '\u07FF')
            {
                buffer.Add((byte)(0xC0 | (c >> 6)));
                buffer.Add((byte)(0x80 | (c & 0x3F)));
            }
            else
            {
                buffer.Add((byte)(0xE0 | (c >> 12)));
                buffer.Add((byte)(0x80 | ((c >> 6) & 0x3F)));
                buffer.Add((byte)(0x80 | (c & 0x3F)));
            }
        }
        if (buffer.Count > ushort.MaxValue) throw new InvalidDataException("NBT string too long");
        WBE16(w, (short)(ushort)buffer.Count);
        w.Write(buffer.ToArray());
    }
}
