using System.Buffers.Binary;

namespace AIL2.Parity;

/// <summary>Loads original AIL LX objects and internal relocations into an isolated guest image.</summary>
internal sealed class LxImage
{
    /// <summary>The fixed guest base used by the isolated oracle image.</summary>
    public const uint BaseAddress = 0x100000;

    /// <summary>Gets copied and relocated object bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>Gets original AIL function numbers mapped to guest entry addresses.</summary>
    public Dictionary<int, uint> Functions { get; } = [];

    /// <summary>Loads object pages and source-supported internal relocations.</summary>
    /// <param name="file">The complete original LX module bytes.</param>
    /// <exception cref="InvalidDataException">The module signature, page flags or relocation form is unsupported.</exception>
    public LxImage(byte[] file)
    {
        var lx = U32(0x3c);
        if (file[lx] != 'L' || file[lx + 1] != 'X')
        {
            throw new InvalidDataException("Expected AIL LX module.");
        }

        var count = U32(lx + 0x44);
        var pageSize = U32(lx + 0x28);
        var objectTable = lx + U32(lx + 0x40);
        var pageTable = lx + U32(lx + 0x48);
        var dataPages = U32(lx + 0x80);
        var objects = new int[count + 1];
        var pageOffsets = new Dictionary<int, int>();
        var memory = new byte[0x100000];
        var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = objectTable + (i * 24);
            cursor = (cursor + 15) & ~15;
            objects[i + 1] = cursor;
            var length = U32(entry);
            var firstPage = U32(entry + 12) - 1;
            var pages = U32(entry + 16);
            for (var j = 0; j < pages; j++)
            {
                var p = pageTable + ((firstPage + j) * 8);
                var flags = U16(p + 6);
                var destination = cursor + (j * pageSize);
                pageOffsets.Add(firstPage + j, destination);
                if (flags == 3)
                {
                    continue; // LX zero-filled page.
                }

                if (flags != 0)
                {
                    throw new InvalidDataException($"Unsupported LX page flags {flags}.");
                }

                var source = dataPages + (U32(p) << U32(lx + 0x2c));
                var size = Math.Min(U16(p + 4), length - (j * pageSize));
                file.AsSpan(source, size).CopyTo(memory.AsSpan(destination));
            }

            cursor += length;
        }

        var fixupPages = lx + U32(lx + 0x68);
        var fixupRecords = lx + U32(lx + 0x6c);
        for (var page = 0; page < U32(lx + 0x14); page++)
        {
            var at = fixupRecords + U32(fixupPages + (page * 4));
            var end = fixupRecords + U32(fixupPages + ((page + 1) * 4));
            while (at < end)
            {
                var kind = file[at++];
                var flags = file[at++];
                if (kind != 7 || (flags & ~0x10) != 0)
                {
                    throw new InvalidDataException($"Unsupported LX fixup {kind:X2}/{flags:X2}.");
                }

                var source = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at));
                at += 2;
                var targetObject = file[at++];
                var target = (flags & 0x10) != 0 ? U32(at) : U16(at);
                at += (flags & 0x10) != 0 ? 4 : 2;
                if (source < 0)
                {
                    continue; // Duplicate page-crossing relocation, as in DLLLOAD.C.
                }

                BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(pageOffsets[page] + source),
                    BaseAddress + (uint)(objects[targetObject] + target));
            }
        }

        Bytes = memory[..cursor];
        var index = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(Bytes) - BaseAddress));
        while (BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(index)) != -1)
        {
            Functions.Add(BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(index)),
                BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(index + 4)));
            index += 8;
        }

        return;

        int U16(int offset)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset));
        }

        int U32(int offset)
        {
            return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset)));
        }
    }
}
