using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>A small, complete reader and writer for Minecraft's NBT format.</summary>
/// <remarks>Used for <c>servers.dat</c> (read and written by the Servers page) and <c>level.dat</c>
/// (read by the Worlds page). Big-endian throughout, strings in Java's modified UTF-8.
/// <c>level.dat</c> is gzip-compressed and <c>servers.dat</c> is not (the game ignores a compressed
/// one), so compression is detected on read and kept on write.</remarks>
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

    // ── lookups ──────────────────────────────────────────────────────────────

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
    /// <summary>Reads an NBT file. Returns the root compound, or null when the file is missing or
    /// unreadable.</summary>
    /// <remarks>
    /// <para>Never throws for a malformed file: these can be half-written by a crashed game, and one bad
    /// instance must not break a whole page.</para>
    /// <para>Null covers missing, locked, truncated and non-NBT files alike. A caller that will write the
    /// file back must check <see cref="File.Exists(string)"/> and refuse on null, or it will overwrite a
    /// list it merely failed to read.</para>
    /// <para>Worlds and server lists can come from other players, so reads are capped (decompressed
    /// size, nesting depth, tag count); a file past any cap counts as corrupt.</para>
    /// </remarks>
    public static NbtTag? ReadFile(string path, out NbtCompression compression)
    {
        compression = NbtCompression.None;
        try
        {
            if (!File.Exists(path)) return null;
            // Real files are far smaller, and reading a huge one whole would defeat the limit.
            if (new FileInfo(path).Length > MaxInputBytes) return null;
            var raw = File.ReadAllBytes(path);
            return Read(raw, out compression);
        }
        // InvalidDataException doesn't derive from IOException, so it needs its own clause or a corrupt
        // gzip stream escapes this method.
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
        // Everything below reads through this, so no file can make the parser take in more than
        // MaxInputBytes, however well it compresses.
        body = new BoundedInput(body, MaxInputBytes);

        // The catches sit outside the finally: disposing a GZipStream/ZLibStream over corrupt data throws
        // InvalidDataException from Dispose, which the inner try's catches would not see.
        try
        {
            try
            {
                using var reader = new BinaryReader(body);
                var type = (NbtTagType)reader.ReadByte();
                if (type != NbtTagType.Compound) return null;   // every real NBT file starts with one
                var name = ReadString(reader);
                var root = new NbtTag { Type = NbtTagType.Compound, Name = name };
                ReadCompoundBody(reader, root, depth: 1);
                return root;
            }
            finally { body.Dispose(); }
        }
        catch (IOException) { return null; }                  // truncated mid-tag
        catch (InvalidDataException) { return null; }         // corrupt deflate stream, or unknown tag byte
        catch (ArgumentOutOfRangeException) { return null; }  // a nonsense length in a corrupt file
    }

    /// <summary>Writes an NBT file, replacing it atomically.</summary>
    /// <remarks>Writes a sibling temp file and renames it, since a half-written servers.dat would lose
    /// the player's server list. Like Minecraft, keeps the previous copy as <c>{name}_old</c>.</remarks>
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

    /// <summary>The most data one read will take in, in bytes after decompression.</summary>
    /// <remarks>Real files are kilobytes. The cap stops a corrupt or hostile file (a gzip bomb, or a
    /// flipped byte in a length field) from running the launcher out of memory.</remarks>
    private const int MaxInputBytes = 64 * 1024 * 1024;

    /// <summary>The deepest compounds and lists may nest, the same limit Minecraft reads with. Reading
    /// recurses once per level, and a stack overflow cannot be caught.</summary>
    private const int MaxDepth = 512;

    /// <summary>The most tags one read may build.</summary>
    /// <remarks>A tag can take one byte of input but a hundred-odd bytes of memory, so the byte limit
    /// alone isn't enough. The largest real files (old modded level.dat files with a registry snapshot)
    /// have a few hundred thousand.</remarks>
    private const int MaxTags = 1_000_000;

    /// <summary>How much of an array is allocated before any of it has arrived.</summary>
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>Validates an element count read from the file before anything is allocated for it.</summary>
    private static int CheckCount(BinaryReader r, int count, int elementSize)
    {
        if (count < 0)
            throw new InvalidDataException($"Negative NBT array length ({count})");

        // A payload claiming more than the input still holds is corrupt. For compressed input, whose
        // length isn't known yet, the remaining limit stands in for it.
        if ((long)count * elementSize > Input(r).Remaining)
            throw new InvalidDataException($"NBT array of {count} elements runs past the end of the data");

        return count;
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"NBT nests deeper than {MaxDepth} levels");
    }

    /// <summary>The input a reader pulls from. <see cref="Read(byte[], out NbtCompression)"/> builds
    /// every reader over one.</summary>
    private static BoundedInput Input(BinaryReader r) => (BoundedInput)r.BaseStream;

    /// <param name="depth">How deeply <paramref name="parent"/> is nested; the root is 1.</param>
    private static void ReadCompoundBody(BinaryReader r, NbtTag parent, int depth)
    {
        while (true)
        {
            var type = (NbtTagType)r.ReadByte();
            if (type == NbtTagType.End) return;
            var name = ReadString(r);
            parent.Children.Add(ReadPayload(r, type, name, depth + 1));
        }
    }

    /// <param name="depth">How deeply the tag being read is nested.</param>
    private static NbtTag ReadPayload(BinaryReader r, NbtTagType type, string name, int depth)
    {
        Input(r).CountTag();
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
                tag.ByteArrayValue = ReadBlock(r, n);
                break;
            }
            case NbtTagType.IntArray:
            {
                var n = CheckCount(r, BE32(r), sizeof(int));
                // The array itself is only allocated once its bytes have actually arrived.
                var bytes = ReadBlock(r, n * sizeof(int));
                var a = new int[n];
                for (var i = 0; i < a.Length; i++)
                    a[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * sizeof(int)));
                tag.IntArrayValue = a;
                break;
            }
            case NbtTagType.LongArray:
            {
                var n = CheckCount(r, BE32(r), sizeof(long));
                var bytes = ReadBlock(r, n * sizeof(long));
                var a = new long[n];
                for (var i = 0; i < a.Length; i++)
                    a[i] = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(i * sizeof(long)));
                tag.LongArrayValue = a;
                break;
            }
            case NbtTagType.List:
            {
                CheckDepth(depth);
                var elem = (NbtTagType)r.ReadByte();
                // One byte is the smallest possible element, so this bounds the count. Children grows as
                // elements are read, so nothing is allocated up front.
                var n = CheckCount(r, BE32(r), sizeof(byte));
                tag.ListElementType = elem;
                for (var i = 0; i < n; i++) tag.Children.Add(ReadPayload(r, elem, "", depth + 1));
                break;
            }
            case NbtTagType.Compound:
                CheckDepth(depth);
                ReadCompoundBody(r, tag, depth);
                break;

            default:
                throw new InvalidDataException($"Unknown NBT tag type {(byte)type}");
        }
        return tag;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, allocating only as fast as they arrive.</summary>
    /// <remarks>For compressed input <see cref="CheckCount"/> can only check a length against the limit,
    /// so a lying length costs only what is really behind it. Up to <see cref="ReadChunkBytes"/> is one
    /// allocation.</remarks>
    private static byte[] ReadBlock(BinaryReader r, int count)
    {
        if (count == 0) return [];
        var buffer = new byte[Math.Min(count, ReadChunkBytes)];
        var filled = 0;
        while (true)
        {
            var read = r.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0) throw new EndOfStreamException();
            filled += read;
            if (filled == count) return buffer;
            if (filled == buffer.Length) Array.Resize(ref buffer, (int)Math.Min(count, buffer.Length * 2L));
        }
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

    /// <summary>Reads a Java "modified UTF-8" string: a 16-bit byte count followed by that many bytes.</summary>
    /// <remarks>Java writes NUL as <c>C0 80</c> and characters outside the BMP as two 3-byte surrogates
    /// (CESU-8). <see cref="Encoding.UTF8"/> would turn both into U+FFFD, so renaming a world with an
    /// emoji in its name would corrupt it. Decoding per UTF-16 code unit handles both.</remarks>
    private static string ReadString(BinaryReader r)
    {
        var len = (ushort)BE16(r);
        if (len == 0) return "";
        if (len > Input(r).Remaining) throw new InvalidDataException("NBT string runs past the end of the data");
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

    /// <summary>The stream one read pulls from, holding that read to <see cref="MaxInputBytes"/> and
    /// <see cref="MaxTags"/>.</summary>
    /// <remarks>Also tracks how much input is left, which a gzip or zlib stream can't report, so declared
    /// lengths can be checked before allocating. Going past a limit throws
    /// <see cref="InvalidDataException"/>, which the read treats as a corrupt file.</remarks>
    private sealed class BoundedInput(Stream inner, long limit) : Stream
    {
        private long _consumed;
        private int _tags;

        /// <summary>The most bytes the rest of the read can still get: what the limit allows, or what
        /// the source has left when it can say and that is less.</summary>
        public long Remaining
        {
            get
            {
                var allowed = limit - _consumed;
                return inner.CanSeek ? Math.Min(allowed, inner.Length - inner.Position) : allowed;
            }
        }

        public void CountTag()
        {
            if (++_tags > MaxTags)
                throw new InvalidDataException($"NBT holds more than {MaxTags} tags");
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_consumed >= limit)
                throw new InvalidDataException($"NBT data is larger than the {limit} byte limit");
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - _consumed)]);
            _consumed += read;
            return read;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // Disposing the gzip or zlib stream underneath can itself throw on a corrupt trailer; that has
        // to reach the caller, which is why Read catches outside its finally.
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
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

    /// <summary>Writes a Java "modified UTF-8" string, the inverse of <see cref="ReadString"/>.</summary>
    /// <remarks>Encodes per UTF-16 code unit: NUL becomes <c>C0 80</c> and an astral character becomes
    /// two 3-byte surrogates, which is what Java reads back.</remarks>
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
