using Silk.NET.OpenAL;

namespace AIL2.Player.Audio;

/// <summary>Provides a bounded OpenAL streaming queue. The calling worker owns its context and native resources.</summary>
public sealed unsafe class OpenAlDevice : IAudioDevice
{
    /// <summary>The owned OpenAL rendering API binding.</summary>
    private readonly AL _al;

    /// <summary>The owned OpenAL context API binding.</summary>
    private readonly ALContext _alc;

    /// <summary>The buffer frame rate in hertz.</summary>
    private readonly int _rate;

    /// <summary>The opened native output or loopback device.</summary>
    private Device* _device;

    /// <summary>The native context made current on the thread that opened the device.</summary>
    private Context* _context;

    /// <summary>The native streaming source identifier.</summary>
    private uint _source;

    /// <summary>The three owned streaming buffer identifiers.</summary>
    private uint[] _buffers = [];

    /// <summary>Buffers available for the next write.</summary>
    private readonly Queue<uint> _free = [];

    /// <summary>Whether native resources have been released.</summary>
    private bool _disposed;

    /// <summary>The optional OpenAL Soft entry point used by loopback integration tests.</summary>
    private readonly delegate* unmanaged[Cdecl]<Device*, void*, int, void> _renderLoopback;

    /// <summary>Opens the default device and creates a stereo streaming context on the calling thread.</summary>
    /// <param name="sampleRate">The output frame rate in hertz.</param>
    /// <exception cref="InvalidOperationException">
    ///     OpenAL cannot open a device, create a context or allocate streaming
    ///     resources.
    /// </exception>
    public OpenAlDevice(int sampleRate) : this(sampleRate, false)
    {
    }

    /// <summary>Opens either a normal output device or an OpenAL Soft loopback device.</summary>
    /// <param name="sampleRate">The output frame rate in hertz.</param>
    /// <param name="loopback">Whether to use OpenAL Soft's loopback extension instead of a physical output device.</param>
    /// <exception cref="NotSupportedException">Loopback was requested but its extension or entry points are unavailable.</exception>
    /// <exception cref="InvalidOperationException">Device, context or resource creation fails.</exception>
    internal OpenAlDevice(int sampleRate, bool loopback)
    {
        _rate = sampleRate;
        _al = new AL(new OpenAlLibrary());
        _alc = new ALContext(new OpenAlLibrary());
        try
        {
            if (loopback)
            {
                if (!_alc.IsExtensionPresent(null, "ALC_SOFT_loopback"))
                {
                    throw new NotSupportedException(
                        "OpenAL loopback extension is required for headless streaming checks.");
                }

                var open = (delegate* unmanaged[Cdecl]<byte*, Device*>)_alc.GetProcAddress(null,
                    "alcLoopbackOpenDeviceSOFT");
                _renderLoopback =
                    (delegate* unmanaged[Cdecl]<Device*, void*, int, void>)_alc.GetProcAddress(null,
                        "alcRenderSamplesSOFT");
                if (open == null || _renderLoopback == null)
                {
                    throw new NotSupportedException("OpenAL loopback entry points are unavailable.");
                }

                _device = open(null);
            }
            else
            {
                _device = _alc.OpenDevice(null);
            }

            if (_device == null)
            {
                throw new InvalidOperationException("OpenAL could not open an audio device.");
            }

            // ALC_SOFT_loopback requires frequency, channel layout and representation explicitly.
            var attributes = stackalloc int[] { 0x1007, sampleRate, 0x1990, 0x1501, 0x1991, 0x1402, 0 };
            _context = _alc.CreateContext(_device, loopback ? attributes : null);
            if (_context == null || !_alc.MakeContextCurrent(_context))
            {
                throw new InvalidOperationException("OpenAL could not create an audio context.");
            }

            _source = _al.GenSource();
            _buffers = _al.GenBuffers(3);
            Check();
            foreach (var buffer in _buffers)
            {
                _free.Enqueue(buffer);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Converts an OpenAL error into a managed device failure.</summary>
    /// <exception cref="InvalidOperationException">OpenAL reports an error.</exception>
    private void Check()
    {
        var error = _al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL operation failed: {error}.");
        }
    }

    /// <summary>Advances a loopback device and captures its signed stereo output.</summary>
    /// <param name="stereoFrames">The destination for complete interleaved stereo frames.</param>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The device is not a loopback device or OpenAL fails.</exception>
    /// <exception cref="ArgumentException">The destination length is odd.</exception>
    internal void RenderLoopback(Span<short> stereoFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderLoopback == null)
        {
            throw new InvalidOperationException("This is not a loopback device.");
        }

        if (stereoFrames.Length % 2 != 0)
        {
            throw new ArgumentException("Expected complete stereo frames.", nameof(stereoFrames));
        }

        fixed (short* samples = stereoFrames)
        {
            _renderLoopback(_device, samples, stereoFrames.Length / 2);
        }

        Check();
    }

    /// <inheritdoc />
    public bool CanWrite
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
            while (processed-- > 0)
            {
                uint buffer;
                _al.SourceUnqueueBuffers(_source, 1, &buffer);
                _free.Enqueue(buffer);
            }

            Check();
            return _free.Count != 0;
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The streaming queue is full or OpenAL fails.</exception>
    public void Write(ReadOnlySpan<short> stereoFrames)
    {
        if (_free.Count == 0)
        {
            throw new InvalidOperationException("OpenAL queue is full.");
        }

        var buffer = _free.Dequeue();
        fixed (short* bytes = stereoFrames)
        {
            _al.BufferData(buffer, BufferFormat.Stereo16, bytes, stereoFrames.Length * sizeof(short), _rate);
        }

        _al.SourceQueueBuffers(_source, 1, &buffer);
        Resume();
        Check();
    }

    /// <inheritdoc />
    public void Pause()
    {
        _al.SourcePause(_source);
        Check();
    }

    /// <inheritdoc />
    public void Resume()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
        if ((SourceState)state != SourceState.Playing)
        {
            _al.SourcePlay(_source);
        }

        Check();
    }

    /// <inheritdoc />
    public void Clear()
    {
        _al.SourceStop(_source);
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var queued);
        while (queued-- > 0)
        {
            uint buffer;
            _al.SourceUnqueueBuffers(_source, 1, &buffer);
        }

        _free.Clear();
        foreach (var buffer in _buffers)
        {
            _free.Enqueue(buffer);
        }

        Check();
    }

    /// <summary>Releases the source, buffers, context, device and API bindings on their owning thread.</summary>
    /// <remarks>Repeated disposal does nothing.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source != 0)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
            _source = 0;
        }

        if (_buffers.Length != 0)
        {
            _al.DeleteBuffers(_buffers);
            _buffers = [];
        }

        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _context = null;
        }

        if (_device != null)
        {
            _alc.CloseDevice(_device);
            _device = null;
        }

        _al.Dispose();
        _alc.Dispose();
    }
}
