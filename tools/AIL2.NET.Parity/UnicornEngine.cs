using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AIL2.Parity;

/// <summary>Owns a Unicorn x86 engine, its guest memory and strict machine-boundary hooks.</summary>
/// <remarks>Derived guests define the processor mode, call frames and fixture allocation.</remarks>
internal unsafe class UnicornEngine : IDisposable
{
    /// <summary>The logical import name resolved to Unicorn's platform library.</summary>
    private const string Library = "unicorn";

    /// <summary>
    ///     The enabled Windows mitigations that would terminate this process inside Unicorn, or null when none is. Unicorn
    ///     leaves its generated code through a register-restoring longjmp, which Control Flow Guard and CET user shadow
    ///     stacks end with a 0xC0000409 fast fail that no handler sees. tools/UnicornHost.targets builds the harness and
    ///     test executables without both; dotnet.exe keeps Control Flow Guard.
    /// </summary>
    private static readonly string? BlockingMitigations = OperatingSystem.IsWindows() ? FindBlockingMitigations() : null;

    /// <summary>The owned Unicorn engine handle.</summary>
    private nint _handle;

    /// <summary>
    ///     Every callback registered with Unicorn. Unicorn keeps only native function pointers, so the delegate instances
    ///     behind them must stay reachable for the engine's lifetime.
    /// </summary>
    private readonly List<Delegate> _callbacks = [];

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

    /// <summary>Creates an x86 engine, maps zeroed guest memory from address zero and installs boundary callbacks.</summary>
    /// <param name="mode">The Unicorn x86 mode flag.</param>
    /// <param name="memorySize">The mapped byte count, a multiple of the page size.</param>
    /// <remarks>
    ///     The BIOS data area names the color CRTC at 3D4h; the MT-32 drivers derive the retrace status port 3DAh from
    ///     it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Control Flow Guard or CET shadow stacks are enforced for this process, or Unicorn reports an error.
    /// </exception>
    protected UnicornEngine(int mode, nuint memorySize)
    {
        if (BlockingMitigations is not null)
        {
            throw new InvalidOperationException(
                $"{BlockingMitigations} would terminate this process inside Unicorn. Start the AIL2.NET.Parity or " +
                "AIL2.NET.Tests executable, which tools/UnicornHost.targets builds without them, not dotnet with the assembly.");
        }

        Check(uc_open(4, mode, out _handle));
        Check(uc_mem_map(_handle, 0, memorySize, 7));
        Write(0x463, [0xd4, 0x03, 0, 0]);
        InputHook input = (_, port, size, _) =>
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
        };
        OutputHook output = (_, port, size, value, _) =>
        {
            try
            {
                WritePort(port, size, value);
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
            }
        };
        InterruptHook interrupt = (_, number, _) =>
        {
            try
            {
                Interrupt(number);
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
            }
        };
        Check(uc_hook_add(_handle, out _, 2, Root(input), 0, 1, 0, 218));
        Check(uc_hook_add(_handle, out _, 2, Root(output), 0, 1, 0, 500));
        Check(uc_hook_add(_handle, out _, 1, Root(interrupt), 0, 1, 0, 0));
    }

    /// <summary>Runs an observer or boundary substitution when the guest reaches an exact address.</summary>
    /// <param name="address">The guest instruction address.</param>
    /// <param name="action">The callback, whose failures are propagated after native execution returns.</param>
    public void Hook(uint address, Action action)
    {
        CodeHook callback = (_, _, _, _) =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                StopAfterHookFailure(e);
            }
        };
        Check(uc_hook_add(_handle, out _, 4, Root(callback), 0, address, address, 0));
    }

    /// <summary>Keeps a callback reachable for the engine's lifetime and returns the pointer Unicorn will call.</summary>
    /// <typeparam name="T">The unmanaged-callable delegate type.</typeparam>
    /// <param name="callback">The delegate instance to register.</param>
    /// <returns>The native function pointer of exactly this instance.</returns>
    private nint Root<T>(T callback) where T : Delegate
    {
        _callbacks.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
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

    /// <summary>Writes a native-format register value, such as a descriptor-table register.</summary>
    /// <typeparam name="T">The unmanaged native register representation.</typeparam>
    /// <param name="register">The Unicorn register identifier.</param>
    /// <param name="value">The replacement value.</param>
    protected void SetRegister<T>(int register, T value) where T : unmanaged
    {
        Check(uc_reg_write(_handle, register, &value));
    }

    /// <summary>Executes guest instructions until the stop address, a hook failure or the instruction budget.</summary>
    /// <param name="begin">The guest entry address.</param>
    /// <param name="until">The stop address.</param>
    /// <param name="budget">The maximum number of instructions.</param>
    /// <exception cref="InvalidOperationException">A hook fails or Unicorn reports an error.</exception>
    /// <remarks>Exhausting the budget returns normally; callers detect it from the program counter.</remarks>
    protected void Run(ulong begin, ulong until, nuint budget)
    {
        _hookError = null;
        var error = uc_emu_start(_handle, begin, until, 0, budget);
        if (_hookError is not null)
        {
            throw new InvalidOperationException($"Oracle boundary failure at {Register(26):X8}.", _hookError);
        }

        if (error != 0)
        {
            throw new InvalidOperationException(
                $"Unicorn {Marshal.PtrToStringAnsi(uc_strerror(error))} at {Register(26):X8}, call {begin:X8}.");
        }
    }

    /// <summary>Names the enabled mitigations that terminate Unicorn in the current Windows process.</summary>
    /// <returns>The names joined with "and", or null when none is enabled.</returns>
    /// <remarks>A policy Windows cannot report, such as shadow stacks before their introduction, is not enforced.</remarks>
    private static string? FindBlockingMitigations()
    {
        var names = new List<string>();
        if (IsEnabled(7))
        {
            names.Add("Control Flow Guard");
        }

        if (IsEnabled(15))
        {
            names.Add("CET shadow stacks");
        }

        return names.Count == 0 ? null : string.Join(" and ", names);

        // ProcessControlFlowGuardPolicy and ProcessUserShadowStackPolicy both keep their enable flag in bit 0.
        static bool IsEnabled(int policy)
        {
            uint flags = 0;
            return GetProcessMitigationPolicy(-1, policy, &flags, sizeof(uint)) && (flags & 1) != 0;
        }
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

        GC.SuppressFinalize(this);
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

    /// <summary>Reads one mitigation policy of a Windows process.</summary>
    /// <param name="process">The process handle; -1 is the current process.</param>
    /// <param name="policy">The PROCESS_MITIGATION_POLICY value.</param>
    /// <param name="buffer">Receives the policy structure.</param>
    /// <param name="length">The structure size in bytes.</param>
    /// <returns>True when Windows reported the policy.</returns>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(nint process, int policy, void* buffer, nuint length);

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
