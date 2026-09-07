using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AIL2.Parity;

/// <summary>Owns a bounded x86 guest. This assembly is never referenced by product projects.</summary>
internal sealed unsafe class UnicornMachine : IDisposable
{
    /// <summary>The logical import name resolved to Unicorn's platform library.</summary>
    private const string Library = "unicorn";

    /// <summary>The owned Unicorn engine handle.</summary>
    private nint _handle;

    /// <summary>Next aligned fixture address in ordinary guest memory.</summary>
    private uint _nextAllocation = 0x300000;

    /// <summary>Rooted callbacks for source procedure and boundary hooks.</summary>
    private readonly List<CodeHook> _codeHooks = [];

    /// <summary>Next aligned fixture address below the guest real-mode boundary.</summary>
    private uint _nextLowAllocation = 0x10000;

    /// <summary>A hook failure retained until native execution returns.</summary>
    private Exception? _hookError;

    /// <summary>Handles a guest input port and byte width; the default rejects every access.</summary>
    /// <exception cref="InvalidOperationException">No handler supports the guest input.</exception>
    public Func<uint, int, uint> ReadPort { get; set; } =
        (port, _) => throw new InvalidOperationException($"Unexpected IN {port:X}.");

    /// <summary>Handles a guest output port, width and value; the default rejects every access.</summary>
    /// <exception cref="InvalidOperationException">No handler supports the guest output.</exception>
    public Action<uint, int, uint> WritePort { get; set; } =
        (port, _, _) => throw new InvalidOperationException($"Unexpected OUT {port:X}.");

    /// <summary>Handles a guest interrupt number; the default rejects every interrupt.</summary>
    /// <exception cref="InvalidOperationException">No handler supports the guest interrupt.</exception>
    public Action<uint> Interrupt { get; set; } =
        number => throw new InvalidOperationException($"Unexpected INT {number:X}.");

    /// <summary>Creates a bounded 32-bit x86 guest and installs strict boundary callbacks.</summary>
    /// <param name="image">The relocated original AIL image to copy into the guest.</param>
    public UnicornMachine(LxImage image)
    {
        Check(uc_open(4, 4, out _handle));
        Check(uc_mem_map(_handle, 0, 0x1000000, 7));
        Write(LxImage.BaseAddress, image.Bytes);
        // Flat code/data selectors for original LES/LDS operations.
        var gdt = new byte[72];
        BinaryPrimitives.WriteUInt64LittleEndian(gdt.AsSpan(8), 0x00cf9b000000ffff);
        BinaryPrimitives.WriteUInt64LittleEndian(gdt.AsSpan(16), 0x00cf93000000ffff);
        BinaryPrimitives.WriteUInt64LittleEndian(gdt.AsSpan(64), 0x004093000400ffff);
        Write(0x1000, gdt);
        var descriptor = new MemoryManagementRegister { Base = 0x1000, Limit = 71 };
        Check(uc_reg_write(_handle, 243, &descriptor));
        SetRegister(11, 8);
        foreach (var r in new[] { 17, 28, 49 })
        {
            SetRegister(r, 16);
        }

        Write(0x463, [0xd4, 0x03, 0, 0]);

        Check(uc_hook_add(_handle, out _, 2, Marshal.GetFunctionPointerForDelegate((InputHook)Input), 0, 1, 0, 218));
        Check(uc_hook_add(_handle, out _, 2, Marshal.GetFunctionPointerForDelegate((OutputHook)Output), 0, 1, 0, 500));
        Check(uc_hook_add(_handle, out _, 1, Marshal.GetFunctionPointerForDelegate((InterruptHook)InterruptHook), 0, 1, 0, 0));
        return;

        void InterruptHook(IntPtr nint, uint number, IntPtr nint1)
        {
            try
            {
                Interrupt(number);
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
            }
        }

        void Output(IntPtr nint, uint port, int size, uint value, IntPtr nint1)
        {
            try
            {
                WritePort(port, size, value);
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
            }
        }

        uint Input(IntPtr nint, uint port, int size, IntPtr nint1)
        {
            try
            {
                return ReadPort(port, size);
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
                return 0;
            }
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

    /// <summary>Runs an observer or boundary substitution when the guest reaches an exact address.</summary>
    /// <param name="address">The guest instruction address.</param>
    /// <param name="action">The callback, whose failures are propagated after native execution returns.</param>
    public void Hook(uint address, Action action)
    {
        _codeHooks.Add(Callback);
        Check(uc_hook_add(_handle, out _, 4, Marshal.GetFunctionPointerForDelegate((CodeHook)Callback), 0, address, address, 0));
        return;

        void Callback(nint _1, ulong _2, uint _3, nint _4)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
            }
        }
    }

    /// <summary>Requests a native stop without allowing managed exceptions to escape a native callback.</summary>
    /// <param name="error">The boundary failure to retain.</param>
    private void StopAfterHookFailure(Exception error)
    {
        _hookError = error;
        var result = uc_emu_stop(_handle);
        if (result != 0)
        {
            // Report after control returns from native code; never throw through a native callback.
            _hookError = new AggregateException(error,
                new InvalidOperationException($"Unicorn stop failed: {Marshal.PtrToStringAnsi(uc_strerror(result))}"));
        }
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

    /// <summary>Copies bytes into mapped guest memory.</summary>
    /// <param name="address">The guest linear destination.</param>
    /// <param name="bytes">The borrowed source bytes.</param>
    public void Write(uint address, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* data = bytes)
        {
            Check(uc_mem_write(_handle, address, data, (nuint)bytes.Length));
        }
    }

    /// <summary>Copies bytes from mapped guest memory.</summary>
    /// <param name="address">The guest linear source.</param>
    /// <param name="length">The number of bytes to copy.</param>
    /// <returns>A new managed array containing the guest bytes.</returns>
    public byte[] Read(uint address, int length)
    {
        var data = new byte[length];
        fixed (byte* p = data)
        {
            Check(uc_mem_read(_handle, address, p, (nuint)length));
        }

        return data;
    }

    /// <summary>Reads a little-endian guest word.</summary>
    /// <param name="address">The guest linear source address.</param>
    /// <returns>The decoded 32-bit value.</returns>
    public uint ReadUInt32(uint address)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));
    }

    /// <summary>Reads a 32-bit x86 register.</summary>
    /// <param name="register">The Unicorn register identifier.</param>
    /// <returns>The current register value.</returns>
    public uint Register(int register)
    {
        uint value = 0;
        Check(uc_reg_read(_handle, register, &value));
        return value;
    }

    /// <summary>Writes a 32-bit x86 register.</summary>
    /// <param name="register">The Unicorn register identifier.</param>
    /// <param name="value">The replacement register value.</param>
    public void SetRegister(int register, uint value)
    {
        Check(uc_reg_write(_handle, register, &value));
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
        _hookError = null;
        // An instruction budget bounds every call; a per-call wall-clock timer creates a native
        // watchdog thread on every 120 Hz service and needlessly dominates corpus execution.
        var error = uc_emu_start(_handle, address, 0x8000, 0, 20_000_000);
        if (_hookError is not null)
        {
            throw new InvalidOperationException($"Oracle boundary failure at {Register(26):X8}.", _hookError);
        }

        if (error != 0)
        {
            throw new InvalidOperationException(
                $"Unicorn {Marshal.PtrToStringAnsi(uc_strerror(error))} at {Register(26):X8}, call {address:X8}.");
        }

        return Register(26) != 0x8000 ? throw new InvalidOperationException($"Oracle instruction/time budget exhausted at {Register(26):X8}.") : Register(19);
    }

    /// <summary>Converts a Unicorn status code into an explicit harness failure.</summary>
    /// <param name="result">The native status code, with zero meaning success.</param>
    /// <exception cref="InvalidOperationException">Unicorn reports a nonzero status.</exception>
    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"Unicorn: {Marshal.PtrToStringAnsi(uc_strerror(result))}");
        }
    }

    /// <summary>Closes the guest engine; repeated calls do nothing.</summary>
    public void Dispose()
    {
        if (_handle != 0)
        {
            Check(uc_close(_handle));
            _handle = 0;
        }
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

    /// <summary>Native Unicorn callback for x86 input instructions.</summary>
    /// <param name="handle">The guest engine handle.</param>
    /// <param name="port">The guest port number.</param>
    /// <param name="size">The input width in bytes.</param>
    /// <param name="user">The opaque registration data, unused by this harness.</param>
    /// <returns>The value supplied to the guest.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint InputHook(nint handle, uint port, int size, nint user);

    /// <summary>Native Unicorn callback for x86 output instructions.</summary>
    /// <param name="handle">The guest engine handle.</param>
    /// <param name="port">The guest port number.</param>
    /// <param name="size">The output width in bytes.</param>
    /// <param name="value">The value emitted by the guest.</param>
    /// <param name="user">The opaque registration data, unused by this harness.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputHook(nint handle, uint port, int size, uint value, nint user);

    /// <summary>Native Unicorn callback for guest interrupts.</summary>
    /// <param name="handle">The guest engine handle.</param>
    /// <param name="number">The interrupt number.</param>
    /// <param name="user">The opaque registration data, unused by this harness.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InterruptHook(nint handle, uint number, nint user);

    /// <summary>Native Unicorn callback before a hooked guest instruction.</summary>
    /// <param name="handle">The guest engine handle.</param>
    /// <param name="address">The guest instruction address.</param>
    /// <param name="size">The instruction size in bytes.</param>
    /// <param name="user">The opaque registration data, unused by this harness.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CodeHook(nint handle, ulong address, uint size, nint user);

    /// <summary>Creates a native Unicorn engine.</summary>
    /// <param name="arch">The Unicorn architecture identifier.</param>
    /// <param name="mode">The architecture execution-mode flags.</param>
    /// <param name="handle">Receives the engine handle.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_open(int arch, int mode, out nint handle);

    /// <summary>Closes a native Unicorn engine.</summary>
    /// <param name="handle">The owned engine handle.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_close(nint handle);

    /// <summary>Maps guest memory with the requested access permissions.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="address">The page-aligned guest base.</param>
    /// <param name="size">The mapped byte count.</param>
    /// <param name="permissions">The Unicorn read/write/execute mask.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_mem_map(nint handle, ulong address, nuint size, uint permissions);

    /// <summary>Copies host bytes to guest memory.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="address">The guest destination.</param>
    /// <param name="data">The pinned host source.</param>
    /// <param name="size">The byte count.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_mem_write(nint handle, ulong address, void* data, nuint size);

    /// <summary>Copies guest bytes to host memory.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="address">The guest source.</param>
    /// <param name="data">The pinned host destination.</param>
    /// <param name="size">The byte count.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_mem_read(nint handle, ulong address, void* data, nuint size);

    /// <summary>Writes a native-format guest register value.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="register">The Unicorn register identifier.</param>
    /// <param name="value">A pointer to the matching native register representation.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_reg_write(nint handle, int register, void* value);

    /// <summary>Reads a native-format guest register value.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="register">The Unicorn register identifier.</param>
    /// <param name="value">A pointer to storage for the native register representation.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_reg_read(nint handle, int register, void* value);

    /// <summary>Runs guest instructions within explicit stop and budget boundaries.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="begin">The guest entry address.</param>
    /// <param name="until">The stop address.</param>
    /// <param name="timeout">The native timeout in microseconds, or zero to disable it.</param>
    /// <param name="count">The maximum number of instructions.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_emu_start(nint handle, ulong begin, ulong until, ulong timeout, nuint count);

    /// <summary>Requests termination of the active guest execution.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_emu_stop(nint handle);

    /// <summary>Obtains Unicorn's static diagnostic string for a status code.</summary>
    /// <param name="error">The Unicorn error code.</param>
    /// <returns>A borrowed null-terminated native string pointer.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint uc_strerror(int error);

    /// <summary>Registers a native instruction, interrupt or address hook.</summary>
    /// <param name="handle">The engine handle.</param>
    /// <param name="hook">Receives the registration identifier.</param>
    /// <param name="type">The Unicorn hook-kind mask.</param>
    /// <param name="callback">The rooted native callback pointer.</param>
    /// <param name="user">Opaque callback data.</param>
    /// <param name="begin">The first included guest address.</param>
    /// <param name="end">The last included guest address; below begin selects all addresses.</param>
    /// <param name="instruction">The instruction identifier for instruction hooks.</param>
    /// <returns>Zero on success, otherwise a Unicorn error code.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int uc_hook_add(nint handle,
        out nuint hook,
        int type,
        nint callback,
        nint user,
        ulong begin,
        ulong end,
        int instruction);
}
