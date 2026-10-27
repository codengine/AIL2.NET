using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AIL2.Parity;

/// <summary>Owns a bounded 32-bit protected-mode guest. This assembly is never referenced by product projects.</summary>
internal sealed class UnicornMachine : UnicornEngine
{
    /// <summary>Next aligned fixture address in ordinary guest memory.</summary>
    private uint _nextAllocation = 0x300000;

    /// <summary>Next aligned fixture address below the guest real-mode boundary.</summary>
    private uint _nextLowAllocation = 0x10000;

    /// <summary>Creates a bounded 32-bit x86 guest and installs strict boundary callbacks.</summary>
    /// <param name="image">The relocated original AIL image to copy into the guest.</param>
    public UnicornMachine(LxImage image) : base(4, 0x1000000)
    {
        Write(LxImage.BaseAddress, image.Bytes);
        // Flat code/data selectors for original LES/LDS operations.
        var gdt = new byte[72];
        BinaryPrimitives.WriteUInt64LittleEndian(gdt.AsSpan(8), 0x00cf9b000000ffff);
        BinaryPrimitives.WriteUInt64LittleEndian(gdt.AsSpan(16), 0x00cf93000000ffff);
        BinaryPrimitives.WriteUInt64LittleEndian(gdt.AsSpan(64), 0x004093000400ffff);
        Write(0x1000, gdt);
        SetRegister(243, new MemoryManagementRegister { Base = 0x1000, Limit = 71 });
        SetRegister(11, 8);
        foreach (var r in new[] { 17, 28, 49 })
        {
            SetRegister(r, 16);
        }
    }

    /// <summary>Copies fixture bytes into the aligned ordinary guest allocation region.</summary>
    /// <param name="bytes">The source bytes.</param>
    /// <returns>The guest linear address of the copy.</returns>
    /// <exception cref="InvalidOperationException">The bounded guest fixture region is exhausted.</exception>
    public uint Allocate(ReadOnlySpan<byte> bytes)
    {
        var address = _nextAllocation;
        _nextAllocation = checked(address + (uint)((bytes.Length + 15) & ~15));
        if (_nextAllocation >= 0xe00000)
        {
            throw new InvalidOperationException("Guest fixture memory exhausted.");
        }

        Write(address, bytes);
        return address;
    }

    /// <summary>Allocates zero-filled ordinary guest fixture memory.</summary>
    /// <param name="size">The requested byte count.</param>
    /// <returns>The guest linear address.</returns>
    public uint Allocate(int size)
    {
        return Allocate(new byte[size]);
    }

    /// <summary>Copies a fixture into the bounded low-memory region used by original sample pointers.</summary>
    /// <param name="bytes">The source bytes.</param>
    /// <returns>The guest linear address of the copy.</returns>
    /// <exception cref="InvalidOperationException">The bounded low-memory fixture region is exhausted.</exception>
    public uint AllocateLow(ReadOnlySpan<byte> bytes)
    {
        var address = _nextLowAllocation;
        _nextLowAllocation = checked(address + (uint)((bytes.Length + 15) & ~15));
        if (_nextLowAllocation >= 0xf0000)
        {
            throw new InvalidOperationException("Low fixture memory exhausted.");
        }

        Write(address, bytes);
        return address;
    }

    /// <summary>Reads a 32-bit argument from the active guest call frame.</summary>
    /// <param name="index">The zero-based argument index after the return address.</param>
    /// <returns>The original argument word.</returns>
    public uint Argument(int index)
    {
        return ReadUInt32(Register(30) + (uint)(4 + (index * 4)));
    }

    /// <summary>Returns from a substituted guest procedure by restoring its caller address.</summary>
    /// <param name="value">An optional EAX result; null preserves EAX.</param>
    public void Return(uint? value = null)
    {
        var stack = Register(30);
        if (value is { } result)
        {
            SetRegister(19, result);
        }

        SetRegister(26, ReadUInt32(stack));
        SetRegister(30, stack + 4);
    }

    /// <summary>Executes an original guest function with a fresh call frame and bounded instruction count.</summary>
    /// <param name="address">The guest entry address.</param>
    /// <param name="arguments">Original 32-bit argument words in call order.</param>
    /// <returns>The guest EAX result.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A hook fails, Unicorn reports an error, or execution does not return within
    ///     its instruction budget.
    /// </exception>
    public uint Call(uint address, params uint[] arguments)
    {
        const uint stack = 0xf00000;
        var bytes = new byte[(arguments.Length + 1) * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x8000);
        for (var i = 0; i < arguments.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((i + 1) * 4), arguments[i]);
        }

        Write(stack, bytes);
        SetRegister(30, stack);
        SetRegister(25, 2);
        // An instruction budget bounds every call; a per-call wall-clock timer creates a native
        // watchdog thread on every 120 Hz service and needlessly dominates corpus execution.
        Run(address, 0x8000, 20_000_000);
        return Register(26) != 0x8000 ? throw new InvalidOperationException($"Oracle instruction/time budget exhausted at {Register(26):X8}.") : Register(19);
    }

    /// <summary>Native Unicorn layout for a memory-management register.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryManagementRegister
    {
        /// <summary>The segment selector field.</summary>
        public ushort Selector;

        /// <summary>The linear descriptor-table base.</summary>
        public ulong Base;

        /// <summary>The descriptor-table byte limit.</summary>
        public uint Limit;

        /// <summary>The native register flags.</summary>
        public uint Flags;
    }
}
