using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace AIL2.Formats;

/// <summary>Owns an original VOC resource and validates all block boundaries without decoding audio.</summary>
public sealed class VocFile
{
    /// <summary>Gets blocks in source order, including the type-zero terminator.</summary>
    public IReadOnlyList<VocBlock> Blocks { get; }

    /// <summary>Wraps validated blocks in a read-only collection.</summary>
    /// <param name="blocks">The ordered blocks backed by owned bytes.</param>
    private VocFile(List<VocBlock> blocks)
    {
        Blocks = new ReadOnlyCollection<VocBlock>(blocks);
    }

    /// <summary>Copies a Creative Voice resource and validates its block boundaries.</summary>
    /// <param name="source">The complete VOC resource.</param>
    /// <returns>An owned block resource without decoding the audio.</returns>
    /// <remarks>
    ///     Playback checks profile and codec capabilities. Loading checks the signature, offsets, block bounds and
    ///     terminator; it does not check the header version checksum.
    /// </remarks>
    /// <exception cref="InvalidDataException">The header, offset or block payload is invalid, or the terminator is missing.</exception>
    public static VocFile Load(ReadOnlySpan<byte> source)
    {
        if (source.Length < 26 || !source[..20].SequenceEqual("Creative Voice File\x1a"u8))
        {
            throw new InvalidDataException("Invalid Creative Voice header.");
        }

        var data = source.ToArray();
        var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20));
        if (at < 26 || at > data.Length)
        {
            throw new InvalidDataException("Invalid VOC first-block offset.");
        }

        var blocks = new List<VocBlock>();
        var ended = false;
        while (at < data.Length)
        {
            var type = data[at++];
            if (type == 0)
            {
                blocks.Add(new VocBlock(0, default));
                ended = true;
                break;
            }

            if (at > data.Length - 3)
            {
                throw new InvalidDataException("Truncated VOC block header.");
            }

            var length = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);
            at += 3;
            if (length > data.Length - at)
            {
                throw new InvalidDataException("VOC block exceeds resource.");
            }

            var minimum = type switch { 1 or 3 => 3, 2 => 1, 4 or 6 => 2, 8 => 4, _ => 0 };
            if (length < minimum)
            {
                throw new InvalidDataException($"Truncated VOC type {type} payload.");
            }

            blocks.Add(new VocBlock(type, data.AsMemory(at, length)));
            at += length;
        }

        return !ended ? throw new InvalidDataException("VOC has no terminator.") : new VocFile(blocks);
    }
}

/// <summary>Original typed block payload, excluding its header. Bytes are owned by the source resource.</summary>
/// <param name="Type">The original block type; zero denotes the terminator.</param>
/// <param name="Payload">The bytes excluding the type and length header.</param>
/// <remarks>
///     VocFile supplies owned memory. Direct construction retains the supplied memory without copying; its owner must
///     keep it immutable.
/// </remarks>
public sealed record VocBlock(byte Type, ReadOnlyMemory<byte> Payload);
