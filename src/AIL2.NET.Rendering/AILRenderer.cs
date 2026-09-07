using System.Buffers.Binary;
using AIL2.Samples;
using NukedOPL3Sharp;

namespace AIL2.Rendering;

/// <summary>Renders an AIL session to signed 16-bit stereo PCM with NukedOPL3Sharp.</summary>
/// <remarks>
///     Use the same worker thread to construct, control, render, and dispose the renderer. No compression decoder is
///     provided.
/// </remarks>
public sealed class AILRenderer : IOplOutput, ISampleOutput, IMidiOutput, IDisposable
{
    /// <summary>NukedOPL3Sharp synthesizer instances; dual OPL2 uses independent instances.</summary>
    private readonly Opl3Chip[] _chips;

    /// <summary>Reusable stereo buffers for the synthesizer output.</summary>
    private readonly short[] _chipFrame = new short[2], _otherFrame = new short[2];

    /// <summary>Immutable payload retained for the active sample block.</summary>
    private ReadOnlyMemory<byte> _sampleBytes;

    /// <summary>Resolved source encoding, channel layout and frame rate.</summary>
    private SampleFormat _sampleFormat;

    /// <summary>The sample handle attached to the PCM path.</summary>
    private SamplePlayback? _sample;

    /// <summary>Logical block origin and pause timestamp used for resampling.</summary>
    private TimeSpan _sampleStart, _samplePause;

    /// <summary>PCM activity, pause and renderer-lifetime flags.</summary>
    private bool _sampleActive, _samplePaused, _disposed;

    /// <summary>Profile-derived gains applied once during mixing.</summary>
    private MixerLevels _sampleLevels = new(1, 1, 1), _oplLevels = new(1, 1, 1);

    /// <summary>The caller-owned MIDI sink, including messages sent during initialization.</summary>
    private readonly IMidiOutput? _midiObserver;

    /// <summary>Gets the output frame rate in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the renderer's session. Control it on the thread that created the renderer.</summary>
    public AILSession Session { get; }

    /// <summary>Occurs synchronously for each complete MIDI message. The message array is a copy.</summary>
    public event Action<TimeSpan, byte[]>? MidiMessage;

    /// <summary>Occurs synchronously with the logical start and duration of each MT-32 pacing request.</summary>
    public event Action<TimeSpan, TimeSpan>? MidiDelay;

    /// <summary>Gets or sets the final linear output gain. Render requires a finite value from zero through one.</summary>
    public double MasterVolume { get; set; } = 1;

    /// <summary>Gets the number of complete stereo frames generated since construction.</summary>
    public long PositionFrames { get; private set; }

    /// <summary>Creates a renderer and session on the calling thread.</summary>
    /// <param name="music">The device and chip selection.</param>
    /// <param name="digitalProfile">The sample profile, or null to disable samples.</param>
    /// <param name="sampleRate">The positive output frame rate in hertz, defaulting to 48000.</param>
    /// <param name="midiObserver">An optional caller-owned MIDI sink that also receives initialization messages.</param>
    /// <remarks>
    ///     Control, render, and dispose on the thread that calls this constructor. The MIDI sink is not disposed. MT-32
    ///     messages are exposed but not synthesized.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">sampleRate is nonpositive or digitalProfile is undefined.</exception>
    /// <exception cref="NotSupportedException">The device and chip combination has no matching driver profile.</exception>
    public AILRenderer(MusicConfiguration music,
        DigitalProfile? digitalProfile = DigitalProfile.SoundBlasterPro,
        int sampleRate = 48000,
        IMidiOutput? midiObserver = null) : this(music.DriverProfile, digitalProfile, sampleRate, midiObserver)
    {
    }

    /// <summary>Creates a stereo renderer with independently selected music and sample profiles.</summary>
    /// <param name="musicProfile">The music profile, or null to disable music.</param>
    /// <param name="digitalProfile">The sample profile, or null to disable samples.</param>
    /// <param name="sampleRate">The positive output frame rate in hertz, defaulting to 48000.</param>
    /// <param name="midiObserver">An optional caller-owned MIDI sink that also receives initialization messages.</param>
    /// <remarks>
    ///     Event handlers added after construction cannot capture initialization; pass midiObserver to receive those
    ///     messages. Compression decoding and MT-32 synthesis are external responsibilities.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">sampleRate is nonpositive or either profile value is undefined.</exception>
    public AILRenderer(MusicProfile? musicProfile = MusicProfile.AdLib,
        DigitalProfile? digitalProfile = DigitalProfile.SoundBlasterPro,
        int sampleRate = 48000,
        IMidiOutput? midiObserver = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        _midiObserver = midiObserver;
        if (musicProfile is null or MusicProfile.Mt32)
        {
            _chips = [];
        }
        else
        {
            _chips = musicProfile.Value.IsDualOpl2() ? [new Opl3Chip(), new Opl3Chip()] : [new Opl3Chip()];
        }

        foreach (var chip in _chips)
        {
            chip.Reset((uint)sampleRate);
        }

        Session = new AILSession(musicProfile, digitalProfile, this, this, this);
    }

    /// <summary>Renders complete stereo frames without making the result depend on the caller's buffer size.</summary>
    /// <param name="interleavedStereo">The signed 16-bit left/right destination; its length must be even.</param>
    /// <remarks>
    ///     Advances the session at each frame timestamp, mixes source gains and MasterVolume, and clips to signed 16-bit
    ///     PCM. Call on the thread that created the renderer. Do not advance Session independently beyond the renderer's
    ///     position.
    /// </remarks>
    /// <exception cref="ArgumentException">The buffer does not contain complete stereo frames.</exception>
    /// <exception cref="InvalidOperationException">
    ///     MasterVolume is invalid, the call is recursive, or session ownership is
    ///     violated.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The renderer or its session has been disposed.</exception>
    /// <exception cref="InvalidDataException">An active resource encounters invalid playback data or a zero-time cycle.</exception>
    /// <exception cref="NotSupportedException">An active sample requires an unsupported encoding or profile.</exception>
    public void Render(Span<short> interleavedStereo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((interleavedStereo.Length & 1) != 0)
        {
            throw new ArgumentException("A stereo buffer must contain complete frames.", nameof(interleavedStereo));
        }

        if (!double.IsFinite(MasterVolume) || MasterVolume < 0 || MasterVolume > 1)
        {
            throw new InvalidOperationException("Master volume must be between zero and one.");
        }

        for (var i = 0; i < interleavedStereo.Length; i += 2)
        {
            var time = TimeSpan.FromTicks((long)((Int128)PositionFrames * TimeSpan.TicksPerSecond / SampleRate));
            Session.AdvanceTo(time);
            var left = 0;
            var right = 0;
            if (_chips.Length != 0)
            {
                _chips[0].GenerateResampled(_chipFrame);
                left = _chipFrame[0];
                right = _chipFrame[1];
                if (_chips.Length == 2)
                {
                    _chips[1].GenerateResampled(_otherFrame);
                    right = _otherFrame[1];
                }

                left = left * _oplLevels.Left / _oplLevels.Maximum;
                right = right * _oplLevels.Right / _oplLevels.Maximum;
            }

            if (_sampleActive && !_samplePaused && !_sampleBytes.IsEmpty)
            {
                var sourceFrame = (long)((Int128)(time - _sampleStart).Ticks * _sampleFormat.RateNumerator /
                                         (TimeSpan.TicksPerSecond * _sampleFormat.RateDenominator));
                var bytesPerSample = _sampleFormat.Encoding == SampleEncoding.Signed16LittleEndian ? 2 : 1;
                var index = sourceFrame * bytesPerSample * _sampleFormat.Channels;
                if (index >= 0 && index + (bytesPerSample * _sampleFormat.Channels) <= _sampleBytes.Length)
                {
                    var l = ReadSample((int)index);
                    var r = _sampleFormat.Channels == 2 ? ReadSample((int)index + bytesPerSample) : l;
                    left += l * _sampleLevels.Left / _sampleLevels.Maximum;
                    right += r * _sampleLevels.Right / _sampleLevels.Maximum;
                }
            }

            interleavedStereo[i] = (short)Math.Clamp((int)(left * MasterVolume), short.MinValue, short.MaxValue);
            interleavedStereo[i + 1] = (short)Math.Clamp((int)(right * MasterVolume), short.MinValue, short.MaxValue);
            PositionFrames++;
        }
    }

    /// <summary>Converts one source PCM sample to signed 16-bit amplitude.</summary>
    /// <param name="index">The byte offset of the source channel sample.</param>
    /// <returns>The signed amplitude.</returns>
    /// <exception cref="NotSupportedException">The source encoding requires external decoding.</exception>
    private int ReadSample(int index)
    {
        return _sampleFormat.Encoding switch
        {
            SampleEncoding.Unsigned8 => (_sampleBytes.Span[index] - 128) << 8,
            SampleEncoding.Signed8 => (sbyte)_sampleBytes.Span[index] << 8,
            SampleEncoding.Signed16LittleEndian => BinaryPrimitives.ReadInt16LittleEndian(_sampleBytes.Span[index..]),
            _ => throw new NotSupportedException("External decoding is required.")
        };
    }

    /// <inheritdoc />
    public bool Supports(SampleFormat format)
    {
        return format.Encoding is SampleEncoding.Unsigned8 or SampleEncoding.Signed8
            or SampleEncoding.Signed16LittleEndian;
    }

    /// <inheritdoc />
    public void BeginBlock(TimeSpan time,
        SamplePlayback playback,
        ReadOnlyMemory<byte> bytes,
        SampleFormat format,
        bool continuation)
    {
        _sample = playback;
        _sampleBytes = bytes;
        _sampleFormat = format;
        _sampleStart = time;
        _sampleActive = true;
        _samplePaused = false;
    }

    /// <inheritdoc />
    public void Control(TimeSpan time, SamplePlayback playback, SampleOperation operation)
    {
        if (!ReferenceEquals(playback, _sample))
        {
            return;
        }

        switch (operation)
        {
            case SampleOperation.Stop:
                _sampleActive = false;
                _sampleBytes = default;
                break;
            case SampleOperation.Pause:
                _samplePaused = true;
                _samplePause = time;
                break;
            case SampleOperation.Resume:
                _sampleStart += time - _samplePause;
                _samplePaused = false;
                break;
        }
    }

    /// <inheritdoc />
    public void SetSampleLevels(TimeSpan time, MixerLevels levels)
    {
        _sampleLevels = levels;
    }

    /// <inheritdoc />
    public void WriteRegister(TimeSpan time, int chip, ushort register, byte value)
    {
        _chips[chip].WriteRegisterBuffered(register, value);
    }

    /// <inheritdoc />
    public void SetOplLevels(TimeSpan time, MixerLevels levels)
    {
        _oplLevels = levels;
    }

    /// <inheritdoc />
    public void SendMessage(TimeSpan time, ReadOnlySpan<byte> message)
    {
        _midiObserver?.SendMessage(time, message);
        MidiMessage?.Invoke(time, message.ToArray());
    }

    /// <inheritdoc />
    public void Delay(TimeSpan time, TimeSpan duration)
    {
        _midiObserver?.Delay(time, duration);
        MidiDelay?.Invoke(time, duration);
    }

    /// <summary>Disposes the renderer's session and releases retained sample data.</summary>
    /// <remarks>Call on the thread that created the renderer. Repeated disposal does nothing; the MIDI sink remains open.</remarks>
    /// <exception cref="InvalidOperationException">The session is disposed from another thread.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Session.Dispose();
        _sampleBytes = default;
        _disposed = true;
    }
}
