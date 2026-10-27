using System.Runtime.InteropServices;

namespace AIL2.Parity;

/// <summary>Runs the supplied SOUNDFX.C, whose AIL calls enter the original music driver oracle.</summary>
public sealed class OriginalSoundEffects : IDisposable
{
    /// <summary>The loaded original-helper shim library.</summary>
    private readonly nint _library;

    /// <summary>The shim's operation dispatcher.</summary>
    private readonly Command _command;

    /// <summary>The rooted AIL boundary callback; the shim retains only its native function pointer.</summary>
    private readonly Boundary _sink;

    /// <summary>A callback failure retained for propagation after native control returns.</summary>
    private Exception? _error;

    /// <summary>Gets the service period requested by original SOUNDFX.C, in microseconds.</summary>
    public int PeriodMicroseconds { get; private set; }

    /// <summary>Loads the locally built original helper and installs its AIL boundary callback.</summary>
    /// <param name="root">The workspace containing artifacts/soundfx.</param>
    /// <param name="driver">The caller-owned original music oracle receiving helper operations.</param>
    /// <exception cref="InvalidOperationException">The helper requests an undocumented boundary or initialization fails.</exception>
    public OriginalSoundEffects(string root, OriginalMusicDriver driver)
    {
        var file = OperatingSystem.IsMacOS() ? "libsoundfx.dylib" : "libsoundfx.so";
        if (OperatingSystem.IsWindows())
        {
            file = "soundfx.dll";
        }

        _library = NativeLibrary.Load(Path.Combine(root, "artifacts/soundfx", file));
        _command = Marshal.GetDelegateForFunctionPointer<Command>(NativeLibrary.GetExport(_library, "sfx_command"));

        _sink = Boundary;
        Marshal.GetDelegateForFunctionPointer<SetSink>(NativeLibrary.GetExport(_library, "sfx_set_sink"))(_sink);
        Invoke(0);
        return;

        int Boundary(int op, int a, int b, int c, int d)
        {
            try
            {
                switch (op)
                {
                    case 0:
                        return (int)driver.Call(191, (uint)a);
                    case 1:
                        driver.Call(186, (uint)a, (uint)b, (uint)c, (uint)d);
                        break;
                    case 2:
                        driver.Call(193, (uint)a, (uint)b);
                        break;
                    case 3:
                        return 1;
                    case 4:
                        PeriodMicroseconds = b;
                        break;
                    case 5:
                    case 6:
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected SOUNDFX boundary {op}.");
                }
            }
            catch (Exception e)
            {
                _error = e;
            }

            return 0;
        }
    }

    /// <summary>Invokes an original-helper operation through the local C shim.</summary>
    /// <param name="operation">The shim operation number defined in build-soundfx-oracle.mjs.</param>
    /// <param name="a">The first operation-specific integer argument.</param>
    /// <param name="b">The second operation-specific integer argument.</param>
    /// <param name="c">The third operation-specific integer argument.</param>
    /// <param name="d">The fourth operation-specific integer argument.</param>
    /// <param name="e">The fifth operation-specific integer argument.</param>
    /// <returns>The original helper result.</returns>
    /// <exception cref="InvalidOperationException">An AIL boundary callback failed.</exception>
    public int Invoke(int operation, int a = 0, int b = 0, int c = 0, int d = 0, int e = 0)
    {
        var result = _command(operation, a, b, c, d, e);
        return _error is not null ? throw new InvalidOperationException("Original SOUNDFX boundary failed.", _error) : result;
    }

    /// <summary>Shuts down the original helper and unloads its native library.</summary>
    public void Dispose()
    {
        try
        {
            Invoke(5);
        }
        finally
        {
            NativeLibrary.Free(_library);
        }
    }

    /// <summary>The shim-to-managed AIL boundary callback.</summary>
    /// <param name="op">The boundary operation code.</param>
    /// <param name="a">The first original AIL argument.</param>
    /// <param name="b">The second original AIL argument.</param>
    /// <param name="c">The third original AIL argument.</param>
    /// <param name="d">The fourth original AIL argument.</param>
    /// <returns>The value returned to original SOUNDFX.C.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Boundary(int op, int a, int b, int c, int d);

    /// <summary>The managed-to-native original-helper dispatcher ABI.</summary>
    /// <param name="op">The shim operation code.</param>
    /// <param name="a">The first operation argument.</param>
    /// <param name="b">The second operation argument.</param>
    /// <param name="c">The third operation argument.</param>
    /// <param name="d">The fourth operation argument.</param>
    /// <param name="e">The fifth operation argument.</param>
    /// <returns>The original helper result.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Command(int op, int a, int b, int c, int d, int e);

    /// <summary>Installs the callback used by the native shim.</summary>
    /// <param name="boundary">The rooted managed AIL boundary callback.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSink(Boundary boundary);
}
