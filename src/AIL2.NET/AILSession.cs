using AIL2.Formats;
using AIL2.Music;
using AIL2.Samples;

namespace AIL2;

/// <summary>
///     Coordinates AIL playback on a caller-supplied logical clock. Use all members and callbacks on the thread that
///     creates the session.
/// </summary>
public sealed class AILSession : IDisposable
{
    /// <summary>The managed thread ID on which the session was created and must be used.</summary>
    private readonly int _thread = Environment.CurrentManagedThreadId;

    /// <summary>Lifetime and recursive-advancement guards.</summary>
    private bool _disposed, _advancing;

    /// <summary>Next music and effect deadlines in TimeSpan ticks.</summary>
    private long _nextMusic = 83_330, _nextEffects = 100_000;

    /// <summary>The eight original sequence slots; null marks an available slot.</summary>
    internal readonly XmidiSequence?[] Sequences = new XmidiSequence?[8];

    /// <summary>Physical-channel lock flags and active-note counters with the original byte width.</summary>
    internal readonly byte[] Locks = new byte[16], ActiveNotes = new byte[16];

    /// <summary>Logged controller values by controller row and channel; 255 means unset.</summary>
    internal readonly byte[,] GlobalControls = new byte[9, 16];

    /// <summary>Logged program and pitch bytes per channel; 255 means unset.</summary>
    internal readonly byte[] Programs = Enumerable.Repeat((byte)255, 16).ToArray(),
        PitchLow = Enumerable.Repeat((byte)255, 16).ToArray(),
        PitchHigh = Enumerable.Repeat((byte)255, 16).ToArray();

    /// <summary>Controller numbers represented by the logged-control rows.</summary>
    internal static readonly byte[] LoggedControls = [7, 1, 10, 11, 64, 114, 110, 111, 112];

    /// <summary>Original AIL initialization values corresponding to LoggedControls.</summary>
    internal static readonly byte[] DefaultControls = [127, 0, 64, 127, 0, 0, 0, 0, 0];

    /// <summary>Original AIL program defaults for channels 1 through 9; 255 skips a channel.</summary>
    internal static readonly byte[] DefaultPrograms = [68, 48, 95, 78, 41, 3, 110, 122, 255];

    /// <summary>The selected music profile, or null when music is disabled.</summary>
    internal readonly MusicDriver? Driver;

    /// <summary>Sample handles owned by this session, including stopped handles.</summary>
    internal readonly List<SamplePlayback> Samples = [];

    /// <summary>Active MIDI effects serviced at the original 10 ms cadence.</summary>
    private readonly List<MidiEffect> _effects = [];

    /// <summary>Gets the music profile selected at construction, or null when music is disabled.</summary>
    public MusicProfile? MusicProfile { get; }

    /// <summary>Gets the equivalent device/chip selection, or null when music is disabled.</summary>
    public MusicConfiguration? MusicConfiguration =>
        MusicProfile is { } profile ? AIL2.MusicConfiguration.FromDriverProfile(profile) : null;

    /// <summary>Gets the sample profile selected at construction, or null when samples are disabled.</summary>
    public DigitalProfile? DigitalProfile { get; }

    /// <summary>The caller-owned sample output selected at construction.</summary>
    internal ISampleOutput? SampleOutput { get; }

    /// <summary>Gets the logical time, initially zero and advanced only by AdvanceTo.</summary>
    public TimeSpan Time { get; private set; }

    /// <summary>Gets or sets the bank used by XmidiSequence.PrepareTimbres.</summary>
    /// <remarks>
    ///     Assignment does not replace resident instruments. Use a fresh session to change definitions under existing
    ///     keys. Set this property on the owning thread.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The setter is called from a different thread.</exception>
    public TimbreBank? Timbres
    {
        get;
        set
        {
            CheckAccess();
            field = value;
        }
    }

    /// <summary>Occurs synchronously when a sequence reaches an XMIDI trigger. The handler may change sequence controls.</summary>
    /// <remarks>
    ///     The arguments are the sequence and controller-119 value. The callback runs before event processing finishes
    ///     and may stop or branch playback, but it must not call AdvanceTo recursively. Callback exceptions propagate.
    /// </remarks>
    public event Action<XmidiSequence, byte>? Trigger;

    /// <summary>Creates a session and sends initialization output before returning.</summary>
    /// <param name="music">The fixed device and chip selection.</param>
    /// <param name="digitalProfile">The sample profile, or null to disable samples.</param>
    /// <param name="oplOutput">The caller-owned OPL sink, required for FM music.</param>
    /// <param name="midiOutput">The caller-owned MIDI sink, required for MT-32.</param>
    /// <param name="sampleOutput">The caller-owned sample sink, required when samples are enabled.</param>
    /// <remarks>The session owns playback handles but does not dispose sinks.</remarks>
    /// <exception cref="NotSupportedException">The device and chip combination has no matching driver profile.</exception>
    /// <exception cref="ArgumentOutOfRangeException">digitalProfile is undefined.</exception>
    /// <exception cref="ArgumentNullException">A sink required by the selected profiles is null.</exception>
    public AILSession(MusicConfiguration music,
        DigitalProfile? digitalProfile = null,
        IOplOutput? oplOutput = null,
        IMidiOutput? midiOutput = null,
        ISampleOutput? sampleOutput = null) : this(music.DriverProfile,
        digitalProfile,
        oplOutput,
        midiOutput,
        sampleOutput)
    {
    }

    /// <summary>Creates a session with independently selected music and sample profiles.</summary>
    /// <param name="musicProfile">The music profile selected for this session, or null to disable music.</param>
    /// <param name="digitalProfile">The sample profile selected for this session, or null to disable samples.</param>
    /// <param name="oplOutput">The caller-owned OPL sink, required for FM music.</param>
    /// <param name="midiOutput">The caller-owned MIDI sink, required for MT-32.</param>
    /// <param name="sampleOutput">The caller-owned sample sink, required when samples are enabled.</param>
    /// <remarks>
    ///     Initialization invokes sinks before construction returns. All controls, advancement and disposal belong to
    ///     this thread. The session owns playback handles, not output sinks.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Either profile value is undefined.</exception>
    /// <exception cref="ArgumentNullException">A sink required by the selected profiles is null.</exception>
    public AILSession(MusicProfile? musicProfile = null,
        DigitalProfile? digitalProfile = null,
        IOplOutput? oplOutput = null,
        IMidiOutput? midiOutput = null,
        ISampleOutput? sampleOutput = null)
    {
        if (musicProfile is { } mp && !Enum.IsDefined(mp))
        {
            throw new ArgumentOutOfRangeException(nameof(musicProfile));
        }

        if (digitalProfile is { } dp && !Enum.IsDefined(dp))
        {
            throw new ArgumentOutOfRangeException(nameof(digitalProfile));
        }

        MusicProfile = musicProfile;
        DigitalProfile = digitalProfile;
        SampleOutput = sampleOutput;
        if (digitalProfile is not null && sampleOutput is null)
        {
            throw new ArgumentNullException(nameof(sampleOutput));
        }

        if (musicProfile is { } profile)
        {
            Driver = profile == AIL2.MusicProfile.Mt32
                ? new Mt32Driver(this, midiOutput ?? throw new ArgumentNullException(nameof(midiOutput)))
                : new FmDriver(this, profile, oplOutput ?? throw new ArgumentNullException(nameof(oplOutput)));
            for (var i = 0; i < 9; i++)
            {
                for (var ch = 0; ch < 16; ch++)
                {
                    GlobalControls[i, ch] = 255;
                }
            }

            Driver.Initialize();
            for (var i = 0; i < LoggedControls.Length; i++)
            {
                for (var ch = 1; ch <= 9; ch++)
                {
                    GlobalControls[i, ch] = DefaultControls[i];
                    Driver.Send((byte)(0xb0 | ch), LoggedControls[i], DefaultControls[i]);
                }
            }

            if (Driver is Mt32Driver mt)
            {
                mt.Wait(10);
            }

            for (var ch = 1; ch <= 9; ch++)
            {
                PitchLow[ch] = 0;
                PitchHigh[ch] = 64;
                Driver.Send((byte)(0xe0 | ch), 0, 64);
                if (DefaultPrograms[ch - 1] != 255)
                {
                    Programs[ch] = DefaultPrograms[ch - 1];
                    Driver.Send((byte)(0xc0 | ch), Programs[ch], 0);
                }
            }

            if (Driver is Mt32Driver mtEnd)
            {
                mtEnd.Wait(10);
            }
        }
    }

    /// <summary>Throws if the session is disposed or the caller is on the wrong thread.</summary>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread.</exception>
    internal void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("AIL session accessed from a different thread.");
        }
    }

    /// <summary>Advances every service due by the target logical time without reading the system clock.</summary>
    /// <param name="time">The absolute logical target, greater than or equal to Time.</param>
    /// <remarks>
    ///     At a shared deadline, sample completions precede sequences in slot order, then music-driver service, then MIDI
    ///     effects. Output and trigger callbacks run synchronously; their exceptions propagate.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">time precedes the current session time.</exception>
    /// <exception cref="InvalidOperationException">The call is recursive or comes from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="InvalidDataException">An active resource encounters malformed playback data or a zero-time cycle.</exception>
    /// <exception cref="NotSupportedException">A sample block requires an unsupported profile or output format.</exception>
    public void AdvanceTo(TimeSpan time)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfLessThan(time, Time);

        if (_advancing)
        {
            throw new InvalidOperationException("Recursive session advancement is not supported.");
        }

        _advancing = true;
        try
        {
            while (true)
            {
                var due = Math.Min(Driver is null ? long.MaxValue : _nextMusic,
                    _effects.Count == 0 ? long.MaxValue : _nextEffects);
                foreach (var sample in Samples)
                {
                    due = Math.Min(due, sample.NextBoundary);
                }

                if (due == long.MaxValue || due > time.Ticks)
                {
                    break;
                }

                Time = TimeSpan.FromTicks(due);
                foreach (var sample in Samples.ToArray())
                {
                    if (sample.NextBoundary == due)
                    {
                        sample.CompleteBlock();
                    }
                }

                if (Driver is not null && _nextMusic == due)
                {
                    _nextMusic = checked(_nextMusic + 83_330);
                    foreach (var sequence in Sequences)
                    {
                        sequence?.Service();
                    }

                    Driver.Service();
                }

                if (_effects.Count != 0 && _nextEffects == due)
                {
                    _nextEffects = checked(_nextEffects + 100_000);
                    foreach (var effect in _effects.ToArray())
                    {
                        effect.Service();
                    }
                }
            }

            Time = time;
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>Allocates a stopped sequence from the original eight slots.</summary>
    /// <param name="file">The parsed resource retained by the handle.</param>
    /// <param name="index">The zero-based index into file.Sequences.</param>
    /// <param name="indirectControllers">Optional controller-115 lookup bytes, copied into the handle.</param>
    /// <returns>The sequence handle. Dispose it to free its slot.</returns>
    /// <exception cref="ArgumentNullException">file is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">index is outside the sequence list.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Music is disabled, all slots are occupied, or the call comes from another
    ///     thread.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public XmidiSequence CreateSequence(XmidiFile file,
        int index = 0,
        ReadOnlyMemory<byte> indirectControllers = default)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(file);
        if (Driver is null)
        {
            throw new InvalidOperationException("No music profile was selected.");
        }

        if ((uint)index >= file.Sequences.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var slot = Array.IndexOf(Sequences, null);
        if (slot < 0)
        {
            throw new InvalidOperationException("All eight original sequence slots are in use.");
        }

        var sequence = new XmidiSequence(this, file.Sequences[index], slot, indirectControllers.ToArray());
        Sequences[slot] = sequence;
        return sequence;
    }

    /// <summary>Invokes the host's trigger handler synchronously.</summary>
    /// <param name="sequence">The sequence processing controller 119.</param>
    /// <param name="value">The trigger value.</param>
    internal void EmitTrigger(XmidiSequence sequence, byte value)
    {
        Trigger?.Invoke(sequence, value);
    }

    /// <summary>Installs a missing instrument in the selected driver cache.</summary>
    /// <param name="bank">The bank byte of the original key.</param>
    /// <param name="patch">The patch byte of the original key.</param>
    /// <param name="bytes">The original length-prefixed definition; retained bytes are copied.</param>
    /// <remarks>
    ///     A resident key keeps its existing definition. FM entries are 14 or 25 bytes; 25-byte entries require OPL3.
    ///     MT-32 uploaded entries require at least 248 bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Music is disabled, the cache cannot evict a protected entry, or thread
    ///     ownership is violated.
    /// </exception>
    /// <exception cref="NotSupportedException">An FM instrument format is unsupported.</exception>
    /// <exception cref="InvalidDataException">An MT-32 instrument is too short.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void InstallTimbre(byte bank, byte patch, ReadOnlyMemory<byte> bytes)
    {
        CheckAccess();
        (Driver ?? throw new InvalidOperationException("No music profile.")).Install((ushort)((bank << 8) | patch),
            bytes);
    }

    /// <summary>Preloads bank entries in original index order.</summary>
    /// <param name="bank">The bank to install and retain as Timbres.</param>
    /// <remarks>
    ///     Normal eviction applies and resident keys retain their definitions. For large banks, assign Timbres and
    ///     prepare only the sequence's required instruments.
    /// </remarks>
    /// <exception cref="ArgumentNullException">bank is null.</exception>
    /// <exception cref="InvalidOperationException">Music is disabled, cache allocation fails, or thread ownership is violated.</exception>
    /// <exception cref="NotSupportedException">An FM instrument format is unsupported.</exception>
    /// <exception cref="InvalidDataException">An MT-32 instrument is too short.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void LoadTimbreBank(TimbreBank bank)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(bank);
        if (Driver is null)
        {
            throw new InvalidOperationException("No music profile.");
        }

        foreach (var key in bank.Keys)
        {
            bank.TryGet((byte)(key >> 8), (byte)key, out var bytes);
            Driver.Install(key, bytes);
        }

        Timbres = bank;
    }

    /// <summary>Changes eviction protection for a resident instrument.</summary>
    /// <param name="bank">The bank byte of the original key.</param>
    /// <param name="patch">The patch byte of the original key.</param>
    /// <param name="protect">True to protect the entry; false to make it evictable.</param>
    /// <remarks>Missing keys and sessions without music are unaffected. Call on the owning thread.</remarks>
    public void ProtectTimbre(byte bank, byte patch, bool protect = true)
    {
        CheckAccess();
        Driver?.Protect((ushort)((bank << 8) | patch), protect);
    }

    /// <summary>Changes eviction protection for all currently resident instruments.</summary>
    /// <param name="protect">True to protect entries; false to clear protection.</param>
    /// <remarks>Call on the owning thread before disposing the session.</remarks>
    public void ProtectAllTimbres(bool protect = true)
    {
        CheckAccess();
        Driver?.Protect(null, protect);
    }

    /// <summary>Checks whether the driver can use an instrument without loading it.</summary>
    /// <param name="bank">The bank byte of the original key.</param>
    /// <param name="patch">The patch byte of the original key.</param>
    /// <returns>True for a resident or built-in instrument; false when absent or music is disabled.</returns>
    /// <remarks>Call on the owning thread before disposing the session.</remarks>
    public bool HasTimbre(byte bank, byte patch)
    {
        CheckAccess();
        return Driver?.HasTimbre((ushort)((bank << 8) | patch)) ?? false;
    }

    /// <summary>Sends a MIDI channel message directly through the selected music driver.</summary>
    /// <param name="status">The status from 0x80 through 0xEF, including the zero-based channel.</param>
    /// <param name="data1">The first data byte, from 0 through 127.</param>
    /// <param name="data2">The second data byte, from 0 through 127; defaults to zero.</param>
    /// <remarks>Direct messages bypass sequence controller bookkeeping. Call on the owning thread.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">status is not a channel status, or a data byte exceeds 127.</exception>
    /// <exception cref="InvalidOperationException">Music is disabled or the call comes from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void SendChannelMessage(byte status, byte data1, byte data2 = 0)
    {
        CheckAccess();
        if (status is < 0x80 or >= 0xf0 || data1 > 127 || data2 > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        (Driver ?? throw new InvalidOperationException("No music profile.")).Send(status, data1, data2);
    }

    /// <summary>Writes the MT-32 display; other profiles leave it unchanged.</summary>
    /// <param name="text">Text truncated at the first null character and to 20 ASCII characters, then space-padded.</param>
    /// <remarks>Call on the owning thread before disposing the session.</remarks>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    public void WriteDisplay(string text)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(text);
        Driver?.WriteDisplay(text);
    }

    /// <summary>Locks the least-active eligible channel chosen by the original allocator.</summary>
    /// <returns>The reserved channel, or -1 when no eligible channel exists.</returns>
    /// <remarks>
    ///     The original allocation considers channels 1 through 8 and flushes their sequence notes. ReleaseChannel
    ///     restores logged state. Call on the owning thread.
    /// </remarks>
    public int LockChannel()
    {
        CheckAccess();
        for (var mask = 192; mask >= 128; mask -= 64)
        {
            var best = -1;
            var notes = 255;
            for (var ch = 8; ch >= 1; ch--)
            {
                if ((Locks[ch] & mask) == 0 && ActiveNotes[ch] < notes)
                {
                    best = ch;
                    notes = ActiveNotes[ch];
                }
            }

            if (best < 0)
            {
                continue;
            }

            Driver?.Send((byte)(0xb0 | best), 64, 0);
            FlushChannel(best);
            ActiveNotes[best] = 0;
            Locks[best] |= 128;
            return best;
        }

        return -1;
    }

    /// <summary>Releases the sequence notes selected by the original channel-flush rules.</summary>
    /// <param name="channel">The zero-based channel to flush.</param>
    internal void FlushChannel(int channel)
    {
        foreach (var sequence in Sequences)
        {
            sequence?.FlushNotes(channel);
        }
    }

    /// <summary>Releases a lock and restores logged controllers, program and pitch.</summary>
    /// <param name="channel">The zero-based channel, from 0 through 15.</param>
    /// <remarks>An unlocked channel is unchanged. Call on the owning thread.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">channel is outside 0 through 15.</exception>
    public void ReleaseChannel(int channel)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 15);
        if ((Locks[channel] & 128) == 0)
        {
            return;
        }

        Locks[channel] &= 127;
        ActiveNotes[channel] = 0;
        Driver?.Send((byte)(0xb0 | channel), 64, 0);
        Driver?.Send((byte)(0xb0 | channel), 123, 0);
        for (var i = 0; i < LoggedControls.Length; i++)
        {
            if (GlobalControls[i, channel] != 255)
            {
                Driver?.Send((byte)(0xb0 | channel), LoggedControls[i], GlobalControls[i, channel]);
            }
        }

        if (Programs[channel] != 255)
        {
            Driver?.Send((byte)(0xc0 | channel), Programs[channel], 0);
        }

        if (PitchLow[channel] != 255 && PitchHigh[channel] != 255)
        {
            Driver?.Send((byte)(0xe0 | channel), PitchLow[channel], PitchHigh[channel]);
        }
    }

    /// <summary>Creates a stopped VOC playback handle with the selected sample profile.</summary>
    /// <param name="file">The VOC resource retained by the handle.</param>
    /// <returns>A session-owned handle that can be disposed independently.</returns>
    /// <exception cref="ArgumentNullException">file is null.</exception>
    /// <exception cref="InvalidOperationException">Samples are disabled or the call comes from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public SamplePlayback CreateSample(VocFile file)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(file);
        if (DigitalProfile is null)
        {
            throw new InvalidOperationException("No digital profile.");
        }

        var sample = new SamplePlayback(this, file, DigitalProfile.Value, SampleOutput!);
        Samples.Add(sample);
        return sample;
    }

    /// <summary>Creates a stopped raw-sample handle with the selected sample profile.</summary>
    /// <param name="buffer">The sample buffer retained by the handle.</param>
    /// <returns>A session-owned handle that can be disposed independently.</returns>
    /// <exception cref="ArgumentNullException">buffer is null.</exception>
    /// <exception cref="InvalidOperationException">Samples are disabled or the call comes from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public SamplePlayback CreateSample(SampleBuffer buffer)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(buffer);
        if (DigitalProfile is null)
        {
            throw new InvalidOperationException("No digital profile.");
        }

        var sample = new SamplePlayback(this, buffer, DigitalProfile.Value, SampleOutput!);
        Samples.Add(sample);
        return sample;
    }

    /// <summary>Starts a MIDI effect using the original helper's message order and channel allocation.</summary>
    /// <param name="program">The MIDI program, from 0 through 127.</param>
    /// <param name="note">The MIDI note, from 0 through 127.</param>
    /// <param name="volume">The channel volume, from 0 through 127.</param>
    /// <param name="pan">The MIDI pan value expected by the original helper, from 0 through 127.</param>
    /// <param name="duration">
    ///     Duration truncated to milliseconds and serviced every 10 ms; nonpositive values expire at the
    ///     next service.
    /// </param>
    /// <returns>The session-owned effect; dispose it to release its channel early.</returns>
    /// <remarks>
    ///     The original helper's one-based message-channel behavior is preserved. Channel exposes the zero-based reserved
    ///     channel.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A program, note, volume or pan byte exceeds 127.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Music is disabled, no effect slot or channel is available, or thread
    ///     ownership is violated.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public MidiEffect PlayEffect(byte program, byte note, byte volume, byte pan, TimeSpan duration)
    {
        CheckAccess();
        if (program > 127 || note > 127 || volume > 127 || pan > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(program));
        }

        if (Driver is null)
        {
            throw new InvalidOperationException("No music profile.");
        }

        if (_effects.Count == 8)
        {
            throw new InvalidOperationException("All eight MIDI effect slots are in use.");
        }

        var channel = LockChannel();
        if (channel < 0)
        {
            throw new InvalidOperationException("No channel available for MIDI effect.");
        }

        var effect = new MidiEffect(this, channel, duration);
        _effects.Add(effect);
        if (_nextEffects <= Time.Ticks)
        {
            _nextEffects = ((Time.Ticks / 100_000) + 1) * 100_000;
        }

        effect.Start(program, note, volume, pan);
        return effect;
    }

    /// <summary>Removes a completed or disposed effect from the service list.</summary>
    /// <param name="effect">The effect whose channel has been released.</param>
    internal void RemoveEffect(MidiEffect effect)
    {
        _effects.Remove(effect);
    }

    /// <summary>Stops every playback handle owned by the session and shuts down the music driver.</summary>
    /// <remarks>Call on the thread that created the session. Repeated disposal does nothing. Caller-owned sinks remain open.</remarks>
    /// <exception cref="InvalidOperationException">An undisposed session is disposed from another thread.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CheckAccess();
        foreach (var effect in _effects.ToArray())
        {
            effect.Dispose();
        }

        foreach (var sample in Samples.ToArray())
        {
            sample.Dispose();
        }

        foreach (var sequence in Sequences.ToArray())
        {
            sequence?.Dispose();
        }

        Driver?.Shutdown();
        _disposed = true;
    }
}
