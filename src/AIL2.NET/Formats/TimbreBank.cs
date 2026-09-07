using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace AIL2.Formats;

/// <summary>Owns an original global timbre library. Each entry includes its original length prefix.</summary>
public sealed class TimbreBank
{
    /// <summary>Owned length-prefixed timbres, indexed by the original bank/patch word.</summary>
    private readonly Dictionary<ushort, ReadOnlyMemory<byte>> _entries;

    /// <summary>Gets unique keys in original index order, with bank in the high byte and patch in the low byte.</summary>
    public IReadOnlyCollection<ushort> Keys { get; }

    /// <summary>Retains validated entries and snapshots their index order.</summary>
    /// <param name="entries">The first entry for each key, backed by owned resource bytes.</param>
    private TimbreBank(Dictionary<ushort, ReadOnlyMemory<byte>> entries)
    {
        _entries = entries;
        Keys = new ReadOnlyCollection<ushort>(entries.Keys.ToArray());
    }

    /// <summary>Looks up an instrument without installing it in a driver.</summary>
    /// <param name="bank">The bank byte of the original key.</param>
    /// <param name="patch">The patch byte of the original key.</param>
    /// <param name="timbre">Receives the owned length-prefixed entry, or empty memory when absent.</param>
    /// <returns>True when present; the first duplicate in the source wins.</returns>
    public bool TryGet(byte bank, byte patch, out ReadOnlyMemory<byte> timbre)
    {
        return _entries.TryGetValue((ushort)((bank << 8) | patch), out timbre);
    }

    /// <summary>Copies and parses an original global timbre bank.</summary>
    /// <param name="source">The complete bank bytes, regardless of filename extension.</param>
    /// <returns>An owned bank containing the original length-prefixed entries.</returns>
    /// <exception cref="InvalidDataException">The index, terminator, timbre offset or timbre length is malformed.</exception>
    public static TimbreBank Load(ReadOnlySpan<byte> source)
    {
        var owned = source.ToArray();
        var entries = new Dictionary<ushort, ReadOnlyMemory<byte>>();
        var at = 0;
        var headerEnd = owned.Length;
        while (true)
        {
            if (at + 2 > headerEnd)
            {
                throw new InvalidDataException("Timbre bank has no terminator.");
            }

            if (owned[at + 1] == 255)
            {
                break;
            }

            if (at + 6 > headerEnd)
            {
                throw new InvalidDataException("Truncated timbre bank index.");
            }

            var key = BinaryPrimitives.ReadUInt16LittleEndian(owned.AsSpan(at));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(owned.AsSpan(at + 2));
            if (offset > owned.Length - 2 || offset < at + 6)
            {
                throw new InvalidDataException("Invalid timbre offset.");
            }

            headerEnd = Math.Min(headerEnd, (int)offset);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(owned.AsSpan((int)offset));
            if (length < 2 || offset + length > owned.Length)
            {
                throw new InvalidDataException("Invalid timbre length.");
            }

            entries.TryAdd(key, owned.AsMemory((int)offset, length)); // Original lookup chooses the first entry.
            at += 6;
        }

        return new TimbreBank(entries);
    }
}
