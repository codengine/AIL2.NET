using System.Buffers.Binary;
using System.Text;

namespace AIL2.Parity;

/// <summary>Deterministic, authored test inputs. No bytes are copied from third-party resources.</summary>
public static class SyntheticResources
{
    /// <summary>Authors a short melody with pan changes and optional unbounded repetition.</summary>
    /// <param name="loop">Whether to enclose the melody in a zero-count FOR loop.</param>
    /// <returns>A complete XMIDI resource requiring patch 68.</returns>
    public static byte[] Song(bool loop = false)
    {
        return Xmi([
                0xc1, 68, .. (loop ? new byte[] { 0xb1, 116, 0 } : []),
                0xb1, 10, 0, 0x91, 60, 100, 30, 45, 0xb1, 10, 127, 0x91, 64, 110, 20, 30,
                .. (loop ? new byte[] { 0xb1, 117, 127 } : []), 0xff, 0x2f, 0
            ],
            timbres: [68]);
    }

    /// <summary>Wraps caller-authored events and declarations in a single XMID FORM.</summary>
    /// <param name="events">The EVNT bytes, including caller-supplied end-of-track data.</param>
    /// <param name="branches">Marker identifiers and zero-based EVNT offsets, or null.</param>
    /// <param name="timbres">TIMB keys with bank in the high byte, or null.</param>
    /// <returns>A newly allocated IFF resource.</returns>
    public static byte[] Xmi(byte[] events, (byte Marker, int Offset)[]? branches = null, ushort[]? timbres = null)
    {
        var chunks = new List<byte>();
        if (timbres is { Length: > 0 })
        {
            var body = new byte[2 + (timbres.Length * 2)];
            BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)timbres.Length));
            for (var i = 0; i < timbres.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2 + (i * 2)), timbres[i]);
            }

            chunks.AddRange(Chunk("TIMB", body));
        }

        if (branches is { Length: > 0 })
        {
            var body = new byte[2 + (branches.Length * 6)];
            BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)branches.Length));
            for (var i = 0; i < branches.Length; i++)
            {
                body[2 + (i * 6)] = branches[i].Marker;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 + (i * 6)), branches[i].Offset);
            }

            chunks.AddRange(Chunk("RBRN", body));
        }

        chunks.AddRange(Chunk("EVNT", events));
        return Chunk("FORM", [.. "XMID"u8, .. chunks]);
    }

    /// <summary>Authors an XDIR resource containing one finite and one looping sequence.</summary>
    /// <returns>A complete two-sequence XMIDI resource.</returns>
    public static byte[] MultipleSequences()
    {
        return
        [
            .. Chunk("FORM", [.. "XDIR"u8, .. Chunk("INFO", [2, 0])]),
            .. Chunk("CAT ", [.. "XMID"u8, .. Song(), .. Song(true)])
        ];
    }

    /// <summary>Frames an IFF chunk with big-endian length and even-byte padding.</summary>
    /// <param name="tag">The four-character ASCII chunk identifier.</param>
    /// <param name="body">The unpadded payload.</param>
    /// <returns>The complete padded chunk.</returns>
    private static byte[] Chunk(string tag, byte[] body)
    {
        var bytes = new byte[8 + body.Length + (body.Length & 1)];
        Encoding.ASCII.GetBytes(tag, bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), body.Length);
        body.CopyTo(bytes, 8);
        return bytes;
    }

    /// <summary>Frames caller-authored VOC blocks with a Creative Voice header.</summary>
    /// <param name="blocks">Ordered type/payload pairs; include a type-zero terminator when desired.</param>
    /// <returns>The complete authored byte stream, without validating its playback semantics.</returns>
    public static byte[] Voc(params (byte Type, byte[] Bytes)[] blocks)
    {
        var bytes = new List<byte>("Creative Voice File\u001a"u8.ToArray());
        bytes.AddRange([26, 0, 10, 1, 41, 17]);
        foreach (var (type, body) in blocks)
        {
            bytes.Add(type);
            if (type == 0)
            {
                continue;
            }

            bytes.AddRange([(byte)body.Length, (byte)(body.Length >> 8), (byte)(body.Length >> 16)]);
            bytes.AddRange(body);
        }

        return bytes.ToArray();
    }

    /// <summary>Authors a finite VOC exercising PCM, continuation, repeats and silence.</summary>
    /// <returns>A complete deterministic sample resource.</returns>
    public static byte[] Sample()
    {
        return Voc((1, [211, 0, .. Enumerable.Range(0, 2048).Select(i => (byte)i)]),
            (6, [2, 0]),
            (2, [0, 64, 128, 255]),
            (3, [9, 0, 200]),
            (7, []),
            (0, []));
    }

    /// <summary>Authors melodic, percussion and bank-switch timbres for a selected music policy.</summary>
    /// <param name="profile">The policy; MT-32 receives parameter definitions and FM receives two-operator definitions.</param>
    /// <param name="variant">A nonnegative FM instrument variant used to expose stale-cache mistakes.</param>
    /// <returns>A complete original-format global timbre bank.</returns>
    public static byte[] Bank(MusicProfile profile, int variant = 0)
    {
        var entries = new List<(ushort Key, byte[] Bytes)>();
        for (var patch = 0; patch < 128; patch++)
        {
            entries.Add(((ushort)patch, Timbre(patch, false)));
        }

        for (var note = 35; note < 83; note++)
        {
            entries.Add(((ushort)(0x7f00 | note), Timbre(note, true)));
        }

        entries.Add((0x0144,
            Timbre(68, false))); // Bank switch exercises MT-32 patch mapping and its repaired stack boundary.
        var offset = (entries.Count * 6) + 2;
        var bytes = new byte[offset + entries.Sum(e => e.Bytes.Length)];
        for (var i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 6), entries[i].Key);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((i * 6) + 2), offset);
            entries[i].Bytes.CopyTo(bytes, offset);
            offset += entries[i].Bytes.Length;
        }

        bytes[(entries.Count * 6) + 1] = 255;
        return bytes;

        byte[] Timbre(int patch, bool percussion)
        {
            if (profile == MusicProfile.Mt32)
            {
                var data = new byte[248];
                data[0] = 248;
                Encoding.ASCII.GetBytes("TEST TONE ", data.AsSpan(2));
                data[14] = 1;
                return data;
            }

            return
            [
                14, 0, (byte)(percussion ? patch : 0), (byte)(0x21 + ((patch + variant) % 4)), 24, 0xf2, 0x74, 0,
                (byte)(variant % 2 == 0 ? 0 : 1), (byte)(0x21 + (variant % 3)), 0, 0xf2, 0x74, (byte)(variant % 4)
            ];
        }
    }

    /// <summary>Writes the default authored music, sample and bank corpus.</summary>
    /// <param name="directory">The destination folder; existing matching filenames are overwritten.</param>
    public static void WriteDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "finite.xmi"), Song());
        File.WriteAllBytes(Path.Combine(directory, "loop.xmi"), Song(true));
        File.WriteAllBytes(Path.Combine(directory, "multiple.xmi"), MultipleSequences());
        File.WriteAllBytes(Path.Combine(directory, "effect.voc"), Sample());
        File.WriteAllBytes(Path.Combine(directory, "instruments.ad"), Bank(MusicProfile.AdLib));
        File.WriteAllBytes(Path.Combine(directory, "instruments.opl"), Bank(MusicProfile.SoundBlasterPro2, 1));
        File.WriteAllBytes(Path.Combine(directory, "instruments.mt"), Bank(MusicProfile.Mt32));
    }
}
