using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace AIL2.Formats;

/// <summary>Owns validated IFF XMIDI sequence resources, including original event byte offsets.</summary>
public sealed class XmidiFile
{
    /// <summary>Gets tracks in original container traversal order.</summary>
    public IReadOnlyList<XmidiTrack> Sequences { get; }

    /// <summary>Wraps validated tracks in a read-only collection.</summary>
    /// <param name="tracks">The tracks in original sequence order.</param>
    private XmidiFile(List<XmidiTrack> tracks)
    {
        Sequences = new ReadOnlyCollection<XmidiTrack>(tracks);
    }

    /// <summary>Copies and parses an IFF XMIDI resource.</summary>
    /// <param name="source">The complete resource; the caller may reuse the input after loading.</param>
    /// <returns>An owned resource with validated event and branch boundaries.</returns>
    /// <exception cref="InvalidDataException">
    ///     IFF chunks, events, timbre declarations or branch offsets are malformed, or no
    ///     sequence is present.
    /// </exception>
    public static XmidiFile Load(ReadOnlySpan<byte> source)
    {
        var owned = source.ToArray();
        var tracks = new List<XmidiTrack>();
        ReadChunks(owned, 0, owned.Length, tracks);
        return tracks.Count == 0 ? throw new InvalidDataException("No FORM XMID sequence found.") : new XmidiFile(tracks);
    }

    /// <summary>Traverses nested IFF containers iteratively, preserving source order.</summary>
    /// <param name="data">The owned resource bytes.</param>
    /// <param name="start">The first byte of the container contents.</param>
    /// <param name="end">The exclusive container boundary.</param>
    /// <param name="tracks">The destination for parsed tracks.</param>
    /// <exception cref="InvalidDataException">A chunk header, size, padding or container type is invalid.</exception>
    private static void ReadChunks(byte[] data, int start, int end, List<XmidiTrack> tracks)
    {
        var pending = new Stack<(int Start, int End)>();
        pending.Push((start, end));
        while (pending.TryPop(out var range))
        {
            var at = range.Start;
            var boundary = range.End;
            while (at < boundary)
            {
                if (boundary - at < 8)
                {
                    throw new InvalidDataException("Truncated IFF header.");
                }

                var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
                if (size > boundary - at - 8)
                {
                    throw new InvalidDataException("IFF chunk exceeds its container.");
                }

                var body = at + 8;
                var limit = body + (int)size;
                var next = limit + ((int)size & 1);
                if (next > boundary && limit != boundary)
                {
                    throw new InvalidDataException("Missing IFF padding.");
                }

                if (data.AsSpan(at, 4).SequenceEqual("FORM"u8) || data.AsSpan(at, 4).SequenceEqual("CAT "u8) ||
                    data.AsSpan(at, 4).SequenceEqual("LIST"u8))
                {
                    if (size < 4)
                    {
                        throw new InvalidDataException("Missing IFF container type.");
                    }

                    if (data.AsSpan(at, 4).SequenceEqual("FORM"u8) && data.AsSpan(body, 4).SequenceEqual("XMID"u8))
                    {
                        tracks.Add(ReadTrack(data, body + 4, limit));
                    }
                    else
                    {
                        if (next < boundary)
                        {
                            pending.Push((next, boundary));
                        }

                        at = body + 4;
                        boundary = limit;
                        continue;
                    }
                }

                at = next;
            }
        }
    }

    /// <summary>Parses one XMID FORM and validates its branch destinations.</summary>
    /// <param name="data">The owned resource bytes.</param>
    /// <param name="start">The first child-chunk byte.</param>
    /// <param name="end">The exclusive FORM boundary.</param>
    /// <returns>A track retaining slices of the owned input.</returns>
    /// <exception cref="InvalidDataException">
    ///     A child chunk, timbre list, branch list or event boundary is invalid, or EVNT is
    ///     missing.
    /// </exception>
    private static XmidiTrack ReadTrack(byte[] data, int start, int end)
    {
        ReadOnlyMemory<byte> events = default;
        var timbres = new List<ushort>();
        var branches = new Dictionary<byte, int>();
        for (var at = start; at < end;)
        {
            if (end - at < 8)
            {
                throw new InvalidDataException("Truncated XMID chunk.");
            }

            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
            if (size > end - at - 8)
            {
                throw new InvalidDataException("XMID chunk exceeds its FORM.");
            }

            var body = data.AsSpan(at + 8, (int)size);
            if (data.AsSpan(at, 4).SequenceEqual("EVNT"u8))
            {
                events = data.AsMemory(at + 8, (int)size);
            }
            else if (data.AsSpan(at, 4).SequenceEqual("TIMB"u8))
            {
                if (body.Length < 2 || BinaryPrimitives.ReadUInt16LittleEndian(body) > (body.Length - 2) / 2)
                {
                    throw new InvalidDataException("Truncated TIMB list.");
                }

                for (var i = 0; i < BinaryPrimitives.ReadUInt16LittleEndian(body); i++)
                {
                    timbres.Add(BinaryPrimitives.ReadUInt16LittleEndian(body[(2 + (i * 2))..]));
                }
            }
            else if (data.AsSpan(at, 4).SequenceEqual("RBRN"u8))
            {
                if (body.Length < 2 || BinaryPrimitives.ReadUInt16LittleEndian(body) > (body.Length - 2) / 6)
                {
                    throw new InvalidDataException("Truncated RBRN list.");
                }

                for (var i = 0; i < BinaryPrimitives.ReadUInt16LittleEndian(body); i++)
                {
                    var offset = BinaryPrimitives.ReadUInt32LittleEndian(body[(4 + (i * 6))..]);
                    if (offset > int.MaxValue)
                    {
                        throw new InvalidDataException("Invalid RBRN offset.");
                    }

                    branches.TryAdd(body[2 + (i * 6)], (int)offset);
                }
            }

            at += 8 + (int)size + ((int)size & 1);
        }

        if (events.IsEmpty)
        {
            throw new InvalidDataException("Missing EVNT chunk.");
        }

        var boundaries = ValidateEvents(events.Span);
        return branches.Values.Any(offset => !boundaries.Contains(offset)) ? throw new InvalidDataException("RBRN does not reference an event boundary.") : new XmidiTrack(events, timbres.ToArray(), new ReadOnlyDictionary<byte, int>(branches));
    }

    /// <summary>Checks event lengths and collects valid branch destinations.</summary>
    /// <param name="data">The complete EVNT payload.</param>
    /// <returns>Byte offsets beginning events or delay bytes.</returns>
    /// <exception cref="InvalidDataException">An event is truncated or invalid, or no end-of-track event is present.</exception>
    private static HashSet<int> ValidateEvents(ReadOnlySpan<byte> data)
    {
        var boundaries = new HashSet<int>();
        var at = 0;
        var ended = false;
        while (at < data.Length)
        {
            boundaries.Add(at);
            var status = ReadByte(data, ref at);
            if (status < 128)
            {
                continue;
            }

            var kind = status & 0xf0;
            if (kind == 0xf0)
            {
                var meta = status == 255 ? ReadByte(data, ref at) : -1;
                if (status is not (240 or 247 or 255))
                {
                    throw new InvalidDataException("Unsupported XMIDI system status.");
                }

                var count = ReadVariable(data, ref at);
                if (count > data.Length - at)
                {
                    throw new InvalidDataException("Truncated XMIDI meta/SysEx event.");
                }

                switch (meta)
                {
                    case 0x51 when count != 3:
                    case 0x58 when count < 2:
                        throw new InvalidDataException("Invalid tempo/time signature event.");
                    case 0x58 when (data[at] == 0 || data[at + 1] > 7):
                        throw new InvalidDataException("Invalid time signature.");
                    default:
                        at += (int)count;
                        ended |= meta == 0x2f;
                        break;
                }
            }
            else
            {
                if (ReadByte(data, ref at) > 127 || (kind is not (0xc0 or 0xd0) && ReadByte(data, ref at) > 127))
                {
                    throw new InvalidDataException("Invalid MIDI data byte.");
                }

                if (kind is 0x80 or 0x90)
                {
                    _ = ReadVariable(data, ref at);
                }
            }
        }

        return !ended ? throw new InvalidDataException("XMIDI sequence has no end-of-track event.") : boundaries;
    }

    /// <summary>Reads one event byte and advances the cursor.</summary>
    /// <param name="data">The EVNT payload.</param>
    /// <param name="at">The next byte offset, incremented on success.</param>
    /// <returns>The byte read.</returns>
    /// <exception cref="InvalidDataException">The cursor has reached the end of the payload.</exception>
    internal static byte ReadByte(ReadOnlySpan<byte> data, ref int at)
    {
        return at < data.Length ? data[at++] : throw new InvalidDataException("Truncated XMIDI event.");
    }

    /// <summary>Reads a checked XMIDI variable-length unsigned integer.</summary>
    /// <param name="data">The EVNT payload.</param>
    /// <param name="at">The next byte offset, advanced through the encoded integer.</param>
    /// <returns>The decoded unsigned value.</returns>
    /// <exception cref="InvalidDataException">The integer is truncated, unterminated or exceeds 32 bits.</exception>
    internal static uint ReadVariable(ReadOnlySpan<byte> data, ref int at)
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = ReadByte(data, ref at);
            if (value > uint.MaxValue >> 7)
            {
                throw new InvalidDataException("XMIDI variable number overflow.");
            }

            value = (value << 7) | (uint)(b & 127);
            if ((b & 128) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Unterminated XMIDI variable number.");
    }
}

/// <summary>Immutable sequence bytes and original timbre/branch declarations.</summary>
public sealed class XmidiTrack
{
    /// <summary>Gets the owned EVNT payload, preserving the byte offsets used by branches.</summary>
    public ReadOnlyMemory<byte> Events { get; }

    /// <summary>Gets TIMB keys in source order, with bank in the high byte and patch in the low byte.</summary>
    public IReadOnlyList<ushort> RequiredTimbres { get; }

    /// <summary>Gets markers mapped to zero-based EVNT byte offsets; the first duplicate declaration wins.</summary>
    public IReadOnlyDictionary<byte, int> Branches { get; }

    /// <summary>Retains validated track data behind read-only views.</summary>
    /// <param name="events">The owned EVNT slice.</param>
    /// <param name="timbres">The TIMB keys in source order.</param>
    /// <param name="branches">The validated marker-to-offset map.</param>
    internal XmidiTrack(ReadOnlyMemory<byte> events, ushort[] timbres, IReadOnlyDictionary<byte, int> branches)
    {
        Events = events;
        RequiredTimbres = Array.AsReadOnly(timbres);
        Branches = branches;
    }
}
