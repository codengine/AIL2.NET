using System.Buffers.Binary;
using AIL2.Formats;

namespace AIL2.Samples;

/// <summary>Plays VOC blocks through a managed output consumer while preserving profile-specific Miles behavior.</summary>
public sealed class SamplePlayback : IDisposable
{
    /// <summary>The session owning scheduling and callbacks.</summary>
    private readonly AILSession _session;

    /// <summary>The retained VOC resource, absent for an initial raw buffer.</summary>
    private readonly VocFile? _file;

    /// <summary>The raw buffer, which takes precedence after replacement.</summary>
    private SampleBuffer? _buffer;

    /// <summary>The Miles sample profile selected for this handle.</summary>
    private readonly DigitalProfile _profile;

    /// <summary>The caller-owned sample consumer.</summary>
    private readonly ISampleOutput _output;

    /// <summary>Current VOC block, repeat destination, and the original signed repeat counter.</summary>
    private int _repeatStart, _repeatCount;

    /// <summary>Current VOC rate constant and packed encoding/channel flags.</summary>
    private byte _timeConstant, _packing;

    /// <summary>Pending type-eight fields applied to the next sound block.</summary>
    private byte? _extendedTime, _extendedPacking;

    /// <summary>Whether this handle has been released.</summary>
    private bool _disposed;

    /// <summary>Remaining completion ticks at pause, or long.MaxValue for host-completed blocks.</summary>
    private long _pausedTicks;

    /// <summary>Fractional ticks carried across consecutive blocks of the same format.</summary>
    private Int128 _timeRemainder;

    /// <summary>The format associated with the carried timing remainder.</summary>
    private SampleFormat _previousFormat;

    /// <summary>Gets the current playback state.</summary>
    public SampleStatus Status { get; private set; }

    /// <summary>Gets the resolved current or last block format; default before the first block.</summary>
    public SampleFormat Format { get; private set; }

    /// <summary>Gets the requested volume from 0 through 127, before profile quantization.</summary>
    public int Volume { get; private set; }

    /// <summary>Gets the requested pan from 0 through 127, initially 64.</summary>
    public int Pan { get; private set; } = 64;

    /// <summary>Gets the zero-based VOC block index, or zero for the first raw-buffer block.</summary>
    public int BlockIndex { get; private set; }

    /// <summary>The logical completion deadline in ticks, or long.MaxValue when inactive or host-completed.</summary>
    internal long NextBoundary { get; private set; } = long.MaxValue;

    /// <summary>Creates a stopped VOC handle with the original mixer defaults.</summary>
    /// <param name="session">The owning session.</param>
    /// <param name="file">The VOC resource retained for playback.</param>
    /// <param name="profile">The selected sample profile.</param>
    /// <param name="output">The caller-owned sample consumer.</param>
    internal SamplePlayback(AILSession session, VocFile file, DigitalProfile profile, ISampleOutput output) : this(
        session,
        profile,
        output)
    {
        _file = file;
    }

    /// <summary>Creates a stopped raw-buffer handle with the original mixer defaults.</summary>
    /// <param name="session">The owning session.</param>
    /// <param name="buffer">The sample buffer retained for playback.</param>
    /// <param name="profile">The selected sample profile.</param>
    /// <param name="output">The caller-owned sample consumer.</param>
    internal SamplePlayback(AILSession session, SampleBuffer buffer, DigitalProfile profile, ISampleOutput output) :
        this(session, profile, output)
    {
        _buffer = buffer;
    }

    /// <summary>Initializes the common sample state and profile volume default.</summary>
    /// <param name="session">The owning session.</param>
    /// <param name="profile">The selected sample profile.</param>
    /// <param name="output">The caller-owned sample consumer.</param>
    private SamplePlayback(AILSession session, DigitalProfile profile, ISampleOutput output)
    {
        _session = session;
        _profile = profile;
        _output = output;
        Volume = profile switch
        {
            DigitalProfile.SoundBlasterPro => 110, DigitalProfile.ProAudioSpectrum => 80, _ => 127
        };
    }

    /// <summary>Checks session ownership and sample lifetime.</summary>
    /// <exception cref="ObjectDisposedException">The session or sample has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread.</exception>
    private void Check()
    {
        _session.CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>Restarts this sample and stops other active or paused samples in the session.</summary>
    /// <param name="marker">The VOC marker whose following section should play, or null to start at the beginning.</param>
    /// <remarks>
    ///     Playback ends at the next marker or terminator. Mixer levels and the first playable block are emitted
    ///     synchronously on the session thread.
    /// </remarks>
    /// <exception cref="ArgumentException">A marker was requested for a raw buffer or is absent from the VOC resource.</exception>
    /// <exception cref="NotSupportedException">The profile or output does not support a block's encoding or layout.</exception>
    /// <exception cref="InvalidDataException">Playback encounters an incomplete PCM frame or zero-time repeat cycle.</exception>
    public void Play(ushort? marker = null)
    {
        Check();
        if (_buffer is not null && marker is not null)
        {
            throw new ArgumentException("A raw sample has no VOC markers.", nameof(marker));
        }

        foreach (var other in _session.Samples.ToArray())
        {
            if (other.Status is SampleStatus.Playing or SampleStatus.Paused)
            {
                other.Stop();
            }
        }

        BlockIndex = 0;
        _extendedTime = _extendedPacking = null;
        if (marker is { } id)
        {
            var found = false;
            for (var i = 0; i < _file!.Blocks.Count; i++)
            {
                var block = _file.Blocks[i];
                if (block.Type == 8)
                {
                    SetExtended(block.Payload.Span);
                }

                if (block.Type == 4 && BinaryPrimitives.ReadUInt16LittleEndian(block.Payload.Span) == id)
                {
                    BlockIndex = i + 1;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException("VOC marker was not found.", nameof(marker));
            }
        }

        _repeatCount = 0;
        _timeRemainder = 0;
        _previousFormat = default;
        Status = SampleStatus.Playing;
        SetLevels();
        ProcessBlocks();
    }

    /// <summary>Stops the current resource, replaces its buffer, and starts the replacement immediately.</summary>
    /// <param name="buffer">The owned buffer to retain and start immediately.</param>
    /// <remarks>The digital profile and mixer requests are retained. Call on the session thread.</remarks>
    /// <exception cref="ArgumentNullException">buffer is null.</exception>
    /// <exception cref="NotSupportedException">The profile or output cannot play the buffer's encoding or layout.</exception>
    public void ReplaceBuffer(SampleBuffer buffer)
    {
        Check();
        ArgumentNullException.ThrowIfNull(buffer);
        Stop();
        _buffer = buffer;
        Play();
    }

    /// <summary>Suspends the active block and retains its remaining logical duration.</summary>
    /// <remarks>Has no effect unless playing. Call on the session thread.</remarks>
    public void Pause()
    {
        Check();
        if (Status != SampleStatus.Playing)
        {
            return;
        }

        _pausedTicks = NextBoundary == long.MaxValue ? long.MaxValue : NextBoundary - _session.Time.Ticks;
        NextBoundary = long.MaxValue;
        Status = SampleStatus.Paused;
        _output.Control(_session.Time, this, SampleOperation.Pause);
    }

    /// <summary>Resumes a paused block relative to the current session time.</summary>
    /// <remarks>Has no effect unless paused. Call on the session thread.</remarks>
    public void Resume()
    {
        Check();
        if (Status != SampleStatus.Paused)
        {
            return;
        }

        Status = SampleStatus.Playing;
        NextBoundary = _pausedTicks == long.MaxValue ? long.MaxValue : checked(_session.Time.Ticks + _pausedTicks);
        _output.Control(_session.Time, this, SampleOperation.Resume);
    }

    /// <summary>Stops the active block and cancels its completion deadline.</summary>
    /// <remarks>The resource remains available for Play; stopped samples cannot be resumed. Call on the session thread.</remarks>
    public void Stop()
    {
        Check();
        if (Status is SampleStatus.Playing or SampleStatus.Paused)
        {
            _output.Control(_session.Time, this, SampleOperation.Stop);
        }

        Status = SampleStatus.Stopped;
        NextBoundary = long.MaxValue;
    }

    /// <summary>Updates the requested volume and emits profile-quantized mixer levels.</summary>
    /// <param name="volume">The requested volume from 0 through 127.</param>
    /// <remarks>The original Sound Blaster profile retains this request but uses fixed output gain.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">volume is outside 0 through 127.</exception>
    public void SetVolume(int volume)
    {
        Check();
        if ((uint)volume > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        Volume = volume;
        SetLevels();
    }

    /// <summary>Updates the requested pan and emits profile-quantized mixer levels.</summary>
    /// <param name="pan">The requested pan from 0 through 127; increasing values raise the left-channel gain.</param>
    /// <remarks>Actual stereo behavior and quantization depend on the digital profile.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">pan is outside 0 through 127.</exception>
    public void SetPan(int pan)
    {
        Check();
        if ((uint)pan > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(pan));
        }

        Pan = pan;
        SetLevels();
    }

    /// <summary>Translates volume and pan through the selected original mixer curve.</summary>
    private void SetLevels()
    {
        var right = Curve(127 - Pan) * Volume;
        var left = Curve(Pan) * Volume;
        var levels = _profile switch
        {
            DigitalProfile.SoundBlaster => new MixerLevels(1, 1, 1),
            DigitalProfile.SoundBlasterPro => new MixerLevels(left >> 10, right >> 10, 15),
            DigitalProfile.ProAudioSpectrum => new MixerLevels(left / 161, right / 161, 100),
            DigitalProfile.AdLibGold when Format.Channels != 2 => new MixerLevels(((left + right) / 2) >> 6,
                ((left + right) / 2) >> 6,
                252),
            _ => new MixerLevels(left >> 6, right >> 6, 252)
        };
        _output.SetSampleLevels(_session.Time, levels);
        return;

        static int Curve(int n)
        {
            return n < 63 ? n * 2 : 127;
        }
    }

    /// <summary>
    ///     Reports a block's logical completion. Compressed consumers call this on the session thread after advancing
    ///     session time.
    /// </summary>
    /// <remarks>
    ///     PCM and silence complete automatically. An external compressed consumer calls this once per block after
    ///     advancing session time. It may synchronously receive the next block. Stopped and paused samples are unchanged.
    /// </remarks>
    /// <exception cref="NotSupportedException">The next block requires an unsupported profile or output format.</exception>
    /// <exception cref="InvalidDataException">The next block has an incomplete PCM frame or a zero-time repeat cycle.</exception>
    public void CompleteBlock()
    {
        Check();
        if (Status != SampleStatus.Playing)
        {
            return;
        }

        NextBoundary = long.MaxValue;
        BlockIndex++;
        ProcessBlocks();
    }

    /// <summary>Interprets VOC controls until audio, silence or a terminal boundary is reached.</summary>
    /// <exception cref="NotSupportedException">The raw buffer has an unknown encoding.</exception>
    /// <exception cref="InvalidDataException">VOC control blocks repeat without advancing time.</exception>
    private void ProcessBlocks()
    {
        if (_buffer is { } buffer)
        {
            if (BlockIndex != 0)
            {
                Finish();
                return;
            }

            var period = (Int128)1_000_000 * buffer.Format.RateDenominator /
                         ((Int128)buffer.Format.RateNumerator * buffer.Format.Channels);
            _timeConstant = (byte)(256 - (int)Int128.Clamp(period, 1, 256));
            _packing = (byte)((buffer.Format.Channels == 2 ? 128 : 0) | buffer.Format.Encoding switch
            {
                SampleEncoding.Unsigned8 or SampleEncoding.Signed8 => 0, SampleEncoding.CreativeAdpcm4 => 1,
                SampleEncoding.CreativeAdpcm26 => 2, SampleEncoding.CreativeAdpcm2 => 3,
                SampleEncoding.Signed16LittleEndian => 4,
                _ => throw new NotSupportedException("Unknown sample encoding.")
            });
            Begin(buffer.Bytes, false);
            return;
        }

        var visited = new HashSet<(int, int)>();
        while (Status == SampleStatus.Playing)
        {
            if (BlockIndex >= _file!.Blocks.Count)
            {
                Finish();
                return;
            }

            if (!visited.Add((BlockIndex, _repeatCount)))
            {
                throw new InvalidDataException("VOC contains a zero-time repeat cycle.");
            }

            var block = _file.Blocks[BlockIndex];
            var bytes = block.Payload.Span;
            switch (block.Type)
            {
                case 0:
                case 4:
                    Finish();
                    return;
                case 8: SetExtended(bytes); break;
                case 6:
                    _repeatCount = BinaryPrimitives.ReadInt16LittleEndian(bytes);
                    _repeatStart = BlockIndex + 1;
                    break;
                case 7:
                    if (_repeatCount != 0)
                    {
                        if (_repeatCount != -1)
                        {
                            _repeatCount--;
                        }

                        BlockIndex = _repeatStart;
                        continue;
                    }

                    break;
                case 3:
                    if (_profile is DigitalProfile.SoundBlaster or DigitalProfile.SoundBlasterPro)
                    {
                        Format = ResolveFormat(bytes[2], _packing);
                        var frames = BinaryPrimitives.ReadUInt16LittleEndian(bytes) + 1;
                        _output.BeginBlock(_session.Time, this, ReadOnlyMemory<byte>.Empty, Format, false);
                        Schedule(frames);
                        return;
                    }

                    break;
                case 1:
                    _timeConstant = _extendedTime ?? bytes[0];
                    _packing = _extendedPacking ?? bytes[1];
                    _extendedTime = _extendedPacking = null;
                    Begin(block.Payload[2..], false);
                    return;
                case 2:
                    Begin(block.Payload, true);
                    return;
            }

            BlockIndex++;
        }
    }

    /// <summary>Resolves capabilities, schedules PCM completion and submits a payload.</summary>
    /// <param name="bytes">The immutable owned audio bytes.</param>
    /// <param name="continuation">Whether encoded data continues the previous stream.</param>
    /// <exception cref="NotSupportedException">The consumer does not support the resolved format.</exception>
    /// <exception cref="InvalidDataException">The payload contains an incomplete PCM frame.</exception>
    private void Begin(ReadOnlyMemory<byte> bytes, bool continuation)
    {
        Format = ResolveFormat(_timeConstant, _packing);
        if (_buffer?.Format.Encoding == SampleEncoding.Signed8)
        {
            Format = Format with { Encoding = SampleEncoding.Signed8 };
        }

        if (!_output.Supports(Format))
        {
            Status = SampleStatus.Stopped;
            NextBoundary = long.MaxValue;
            throw new NotSupportedException(
                $"The output does not support {Format.Encoding}; attach an external decoder for compressed playback.");
        }

        if (Format.Encoding is SampleEncoding.Unsigned8 or SampleEncoding.Signed8
            or SampleEncoding.Signed16LittleEndian)
        {
            var frameSize = Format.Channels * (Format.Encoding == SampleEncoding.Signed16LittleEndian ? 2 : 1);
            if (bytes.Length % frameSize != 0)
            {
                Status = SampleStatus.Stopped;
                throw new InvalidDataException("Sample payload contains an incomplete PCM frame.");
            }

            Schedule(bytes.Length / frameSize);
        }
        else
        {
            NextBoundary = long.MaxValue;
        }

        _output.BeginBlock(_session.Time, this, bytes, Format, continuation);
    }

    /// <summary>Retains the VOC type-eight rate and packing fields used by the original driver.</summary>
    /// <param name="bytes">The validated type-eight payload.</param>
    private void SetExtended(ReadOnlySpan<byte> bytes)
    {
        _extendedTime = bytes[1];
        _extendedPacking = (byte)(bytes[2] | (bytes[3] == 1 ? 128 : 0));
    }

    /// <summary>Schedules rational block completion while carrying sub-tick remainders.</summary>
    /// <param name="frames">The number of interleaved frames in this block.</param>
    private void Schedule(long frames)
    {
        if (_previousFormat != Format)
        {
            _timeRemainder = 0;
            _previousFormat = Format;
        }

        var total = ((Int128)frames * TimeSpan.TicksPerSecond * Format.RateDenominator) + _timeRemainder;
        var ticks = total / Format.RateNumerator;
        _timeRemainder = total % Format.RateNumerator;
        NextBoundary = checked(_session.Time.Ticks + (long)ticks);
    }

    /// <summary>Applies profile encoding restrictions and rate quantization.</summary>
    /// <param name="timeConstant">The original eight-bit rate constant.</param>
    /// <param name="packing">The encoding code, with bit 7 indicating stereo.</param>
    /// <returns>The supported format with an exact rational frame rate.</returns>
    /// <exception cref="NotSupportedException">The packing code is unknown or the profile rejects its encoding or layout.</exception>
    private SampleFormat ResolveFormat(byte timeConstant, byte packing)
    {
        var channels = (packing & 128) != 0 ? 2 : 1;
        var encoding = (packing & 127) switch
        {
            0 => SampleEncoding.Unsigned8, 1 => SampleEncoding.CreativeAdpcm4, 2 => SampleEncoding.CreativeAdpcm26,
            3 => SampleEncoding.CreativeAdpcm2, 4 => SampleEncoding.Signed16LittleEndian,
            _ => throw new NotSupportedException($"Unsupported original packing type {packing}.")
        };
        if ((_profile == DigitalProfile.SoundBlaster && channels != 1) ||
            (_profile == DigitalProfile.ProAudioSpectrum && encoding != SampleEncoding.Unsigned8) ||
            (_profile == DigitalProfile.AdLibGold &&
             encoding is SampleEncoding.CreativeAdpcm26 or SampleEncoding.CreativeAdpcm2) ||
            (_profile is DigitalProfile.SoundBlaster or DigitalProfile.SoundBlasterPro &&
             encoding == SampleEncoding.Signed16LittleEndian))
        {
            throw new NotSupportedException($"{_profile} does not support {encoding} with {channels} channels.");
        }

        long numerator = 1_000_000, denominator = (256 - timeConstant) * channels;
        switch (_profile)
        {
            case DigitalProfile.ProAudioSpectrum:
            {
                var frequency = 1_000_000 / (256 - timeConstant);
                numerator = 1_193_180;
                denominator = (1_193_180 / frequency) * channels;
                break;
            }
            case DigitalProfile.AdLibGold:
            {
                var frequency = 1_000_000 / (256 - timeConstant) / channels;
                int[] rates = encoding == SampleEncoding.CreativeAdpcm4
                    ? [22050, 11025, 7350, 5513]
                    : [44100, 22050, 11025, 7350];
                numerator = rates.MinBy(rate => Math.Abs(rate - frequency));
                denominator = 1;
                break;
            }
        }

        return new SampleFormat(encoding, channels, numerator, denominator);
    }

    /// <summary>Marks the selected section complete and stops its output block.</summary>
    private void Finish()
    {
        NextBoundary = long.MaxValue;
        Status = SampleStatus.Done;
        _output.Control(_session.Time, this, SampleOperation.Stop);
    }

    /// <summary>Stops this sample and removes its handle from the session.</summary>
    /// <remarks>Repeated disposal does nothing. Dispose on the session thread.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Check();
        Stop();
        _session.Samples.Remove(this);
        _disposed = true;
    }
}
