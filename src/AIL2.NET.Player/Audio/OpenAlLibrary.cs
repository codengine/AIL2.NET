using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;

namespace AIL2.Player.Audio;

/// <summary>Loads OpenAL through native exports without reflection or assembly-location discovery.</summary>
internal sealed class OpenAlLibrary : INativeContext
{
    /// <summary>The owned native library reference, or zero after disposal.</summary>
    private nint _library;

    /// <summary>Loads the packaged OpenAL library or the platform's installed copy.</summary>
    /// <exception cref="DllNotFoundException">No compatible OpenAL library can be loaded.</exception>
    public OpenAlLibrary()
    {
        var name = "libopenal.so";
        if (OperatingSystem.IsWindows())
        {
            name = "soft_oal.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            name = "libopenal.dylib";
        }

        if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out _library) || NativeLibrary.TryLoad(
                Path.Combine(AppContext.BaseDirectory,
                    "runtimes",
                    RuntimeInformation.RuntimeIdentifier,
                    "native",
                    name),
                out _library))
        {
            return;
        }

        _library = NativeLibrary.Load(OperatingSystem.IsLinux() ? "libopenal.so.1" : name);
    }

    /// <summary>Resolves a required native OpenAL export by its symbol name.</summary>
    /// <param name="proc">The exported function name.</param>
    /// <param name="slot">An optional binding slot; native symbol lookup does not use it.</param>
    /// <returns>The native function address.</returns>
    /// <exception cref="ObjectDisposedException">The library reference has been released.</exception>
    /// <exception cref="EntryPointNotFoundException">The library does not export the requested symbol.</exception>
    public nint GetProcAddress(string proc, int? slot = null)
    {
        ObjectDisposedException.ThrowIf(_library == 0, this);
        return NativeLibrary.GetExport(_library, proc);
    }

    /// <summary>Attempts to resolve an optional native OpenAL export.</summary>
    /// <param name="proc">The exported function name.</param>
    /// <param name="addr">Receives the function address, or zero when absent.</param>
    /// <param name="slot">An optional binding slot; native symbol lookup does not use it.</param>
    /// <returns>True when the symbol is exported by the loaded library.</returns>
    /// <exception cref="ObjectDisposedException">The library reference has been released.</exception>
    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        ObjectDisposedException.ThrowIf(_library == 0, this);
        return NativeLibrary.TryGetExport(_library, proc, out addr);
    }

    /// <summary>Releases this native library reference; repeated calls do nothing.</summary>
    public void Dispose()
    {
        if (_library == 0)
        {
            return;
        }

        NativeLibrary.Free(_library);
        _library = 0;
    }
}
