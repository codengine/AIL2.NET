using AIL2.Formats;

namespace AIL2.Music;

/// <summary>Owns one original XMIDI sequence state. Channels in this managed API are zero-based.</summary>
public sealed class XmidiSequence : IDisposable
{
    /// <summary>The session owning this sequence and its shared channel state.</summary>
    private readonly AILSession _session;

    /// <summary>Validated event bytes and resource declarations retained by this handle.</summary>
    private readonly XmidiTrack _track;

    /// <summary>Index in the original eight-sequence table.</summary>
    private readonly int _slot;

    /// <summary>Copied lookup values consumed by controller 115.</summary>
    private readonly byte[] _indirectValues;

    /// <summary>Logical-channel mappings, program/pitch logs and pending indirect indices.</summary>
    private readonly byte[] _map = new byte[16],
        _program = new byte[16],
        _pitchLow = new byte[16],
        _pitchHigh = new byte[16],
        _indirect = new byte[16];

    /// <summary>Sequence-local values for the nine logged controllers; 255 means unset.</summary>
    private readonly byte[,] _controls = new byte[9, 16];

    /// <summary>The original 32 duration-tracked note slots.</summary>
    private readonly Note[] _notes = Enumerable.Range(0, 32).Select(_ => new Note()).ToArray();

    /// <summary>Four nested FOR/NEXT positions and counters; -1 is free and zero is unbounded.</summary>
    private readonly int[] _loopPosition = new int[4], _loopCount = new int[4];

    /// <summary>Event cursor, original delay counter, and tempo and volume ramp state.</summary>
    private int _interval,
        _tempoError,
        _tempoTarget = 100,
        _volumeTarget,
        _tempoPeriod,
        _volumePeriod,
        _tempoAccum,
        _volumeAccum;

    /// <summary>Original fixed-point beat timing and time-signature numerator.</summary>
    private int _fraction, _timeFraction, _timePerBeat = 8_000_000, _numerator = 4;

    /// <summary>Beat and measure counters with their original width and wraparound.</summary>
    private ushort _beat, _measure;

    /// <summary>Whether playback has started and whether the handle has been released.</summary>
    private bool _started, _disposed;

    /// <summary>Gets whether event progression is stopped, playing or done.</summary>
    public SequenceStatus Status { get; private set; }

    /// <summary>Gets the current nonnegative tempo percentage; 100 is nominal and zero suspends events.</summary>
    public int RelativeTempo { get; private set; } = 100;

    /// <summary>Gets the current nonnegative volume percentage; emitted MIDI volume saturates at 127.</summary>
    public int RelativeVolume { get; private set; }

    /// <summary>Gets the last controller-119 value, or -1 before the first trigger.</summary>
    public int LastTrigger { get; private set; } = -1;

    /// <summary>Gets the current zero-based EVNT byte offset.</summary>
    public int EventOffset { get; private set; }

    /// <summary>Counts completed iterations of original FOR loops whose repeat count is zero (unbounded).</summary>
    public long UnboundedLoopIterations { get; private set; }

    /// <summary>Gets the original look-ahead beat count within the measure, using 16-bit arithmetic.</summary>
    public ushort BeatCount => AdvancedCount().Beat;

    /// <summary>Gets the original look-ahead measure count, including its initial sentinel and 16-bit wraparound.</summary>
    public ushort MeasureCount => AdvancedCount().Measure;

    /// <summary>Initializes a stopped sequence in its allocated session slot.</summary>
    /// <param name="session">The owning session.</param>
    /// <param name="track">The validated track retained by this handle.</param>
    /// <param name="slot">The allocated zero-based session slot.</param>
    /// <param name="indirectValues">The owned controller-115 lookup table.</param>
    internal XmidiSequence(AILSession session, XmidiTrack track, int slot, byte[] indirectValues)
    {
        _session = session;
        _track = track;
        _slot = slot;
        _indirectValues = indirectValues;
        Rewind();
    }

    /// <summary>Checks session ownership and sequence lifetime.</summary>
    /// <exception cref="ObjectDisposedException">The session or sequence has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread.</exception>
    private void Check()
    {
        _session.CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    ///     Gets the first missing TIMB key, with bank in the high byte and patch in the low byte, or null when all are
    ///     available.
    /// </summary>
    public ushort? TimbreRequest =>
        _track.RequiredTimbres.Cast<ushort?>().FirstOrDefault(key => !_session.Driver!.HasTimbre(key!.Value));

    /// <summary>Installs every requested missing timbre from the session bank in the original request order.</summary>
    /// <remarks>Resident keys are preserved. Assign the desired bank to the session first. Call on the session thread.</remarks>
    /// <exception cref="InvalidOperationException">
    ///     The working set cannot fit, all eviction candidates are protected, or
    ///     thread ownership is violated.
    /// </exception>
    /// <exception cref="InvalidDataException">A required bank or key is missing, or an MT-32 definition is malformed.</exception>
    /// <exception cref="NotSupportedException">An FM instrument format is unsupported by the selected chip mode.</exception>
    /// <exception cref="ObjectDisposedException">The session or sequence has been disposed.</exception>
    public void PrepareTimbres()
    {
        Check();
        var requests = new HashSet<ushort>();
        while (TimbreRequest is { } key)
        {
            if (!requests.Add(key))
            {
                throw new InvalidOperationException("Timbre working set exceeds available unprotected cache.");
            }

            if (_session.Timbres is not { } bank || !bank.TryGet((byte)(key >> 8), (byte)key, out var bytes))
            {
                throw new InvalidDataException($"Missing timbre bank {key >> 8}, patch {key & 255}.");
            }

            _session.Driver!.Install(key, bytes);
        }
    }

    /// <summary>Restores the original defaults for controls, loops, notes, ramps, and beat counters.</summary>
    private void Rewind()
    {
        Array.Fill(_loopCount, -1);
        for (var ch = 0; ch < 16; ch++)
        {
            _map[ch] = (byte)ch;
            _program[ch] = _pitchLow[ch] = _pitchHigh[ch] = _indirect[ch] = 255;
            for (var c = 0; c < 9; c++)
            {
                _controls[c, ch] = 255;
            }
        }

        foreach (var note in _notes)
        {
            note.Channel = -1;
        }

        LastTrigger = -1;
        UnboundedLoopIterations = 0;
        _interval = 0;
        RelativeVolume = _volumeTarget = _session.MusicProfile == MusicProfile.Mt32 ? 90 : 100;
        RelativeTempo = _tempoTarget = 100;
        _tempoError = 0;
        _beat = 0;
        _measure = 65535;
        _fraction = _timeFraction = 0;
        _numerator = 4;
        _timePerBeat = 8_000_000;
    }

    /// <summary>Restarts at the first event and restores the original playback defaults.</summary>
    /// <remarks>Stops existing notes. Prepare timbres separately before starting. Call on the session thread.</remarks>
    public void Start()
    {
        Check();
        if (Status == SequenceStatus.Playing)
        {
            Pause();
        }

        Rewind();
        EventOffset = 0;
        _started = true;
        Status = SequenceStatus.Playing;
    }

    /// <summary>Stops active notes and suspends progression while preserving a resumable position.</summary>
    /// <remarks>Equivalent to Pause. Use Start to rewind. Call on the session thread.</remarks>
    public void Stop()
    {
        Pause();
    }

    /// <summary>Suspends a playing sequence and releases its notes and owned channel resources.</summary>
    /// <remarks>Stopped or completed sequences are unchanged. Call on the session thread.</remarks>
    public void Pause()
    {
        Check();
        if (Status != SequenceStatus.Playing)
        {
            return;
        }

        FlushNotes();
        ResetOwnedResources();
        Status = SequenceStatus.Stopped;
    }

    /// <summary>Restores logged controls and continues a previously started, stopped sequence.</summary>
    /// <remarks>
    ///     Released notes are not replayed. Never-started, playing or completed sequences are unchanged. Call on the
    ///     session thread.
    /// </remarks>
    public void Resume()
    {
        Check();
        if (Status != SequenceStatus.Stopped || !_started)
        {
            return;
        }

        for (var ch = 0; ch < 16; ch++)
        {
            if (_controls[6, ch] != 255 && _controls[6, ch] >= 64)
            {
                var locked = _session.LockChannel();
                _map[ch] = (byte)(locked < 0 ? ch : locked);
            }
        }

        for (var c = 0; c < 9; c++)
        {
            if (c != 6)
            {
                for (var ch = 0; ch < 16; ch++)
                {
                    if (_controls[c, ch] != 255)
                    {
                        Control(ch, AILSession.LoggedControls[c], _controls[c, ch]);
                    }
                }
            }
        }

        for (var ch = 0; ch < 16; ch++)
        {
            if (_pitchLow[ch] != 255 && _pitchHigh[ch] != 255)
            {
                _session.Driver!.Send((byte)(0xe0 | _map[ch]), _pitchLow[ch], _pitchHigh[ch]);
            }

            if (_program[ch] != 255)
            {
                _session.Driver!.Send((byte)(0xc0 | _map[ch]), _program[ch], 0);
            }
        }

        Status = SequenceStatus.Playing;
    }

    /// <summary>Changes relative tempo immediately or with the original ramp behavior.</summary>
    /// <param name="percent">The nonnegative target percentage; 100 is nominal and zero suspends events.</param>
    /// <param name="ramp">The nonnegative transition duration; zero applies immediately.</param>
    /// <exception cref="ArgumentOutOfRangeException">percent or ramp is negative.</exception>
    /// <exception cref="OverflowException">The ramp period exceeds the original counter range.</exception>
    public void SetTempo(int percent, TimeSpan ramp = default)
    {
        Check();
        ValidateRamp(percent, ramp);
        _tempoTarget = percent;
        if (ramp == TimeSpan.Zero)
        {
            RelativeTempo = percent;
        }
        else if (percent != RelativeTempo)
        {
            _tempoPeriod = Math.Max(1, checked((int)(ramp.Ticks / 1000 / Math.Abs(percent - RelativeTempo))));
            _tempoAccum = 0;
        }
    }

    /// <summary>Changes relative volume immediately or with the original ramp behavior.</summary>
    /// <param name="percent">The nonnegative target percentage; values above 100 are accepted.</param>
    /// <param name="ramp">The nonnegative transition duration; zero applies immediately.</param>
    /// <remarks>Emitted MIDI volume saturates at 127 using original arithmetic. Call on the session thread.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">percent or ramp is negative.</exception>
    /// <exception cref="OverflowException">The ramp period exceeds the original counter range.</exception>
    public void SetVolume(int percent, TimeSpan ramp = default)
    {
        Check();
        ValidateRamp(percent, ramp);
        _volumeTarget = percent;
        if (ramp == TimeSpan.Zero)
        {
            RelativeVolume = percent;
            UpdateVolume();
        }
        else if (percent != RelativeVolume)
        {
            _volumePeriod = Math.Max(1, checked((int)(ramp.Ticks / 1000 / Math.Abs(percent - RelativeVolume))));
            _volumeAccum = 0;
        }
    }

    /// <summary>Rejects negative ramp targets and durations.</summary>
    /// <param name="percent">The target percentage.</param>
    /// <param name="ramp">The transition duration.</param>
    /// <exception cref="ArgumentOutOfRangeException">percent or ramp is negative.</exception>
    private static void ValidateRamp(int percent, TimeSpan ramp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfLessThan(ramp, TimeSpan.Zero);
    }

    /// <summary>Reads the sequence-local controller log.</summary>
    /// <param name="channel">The zero-based logical channel, from 0 through 15.</param>
    /// <param name="controller">The controller number; 119 queries the last trigger.</param>
    /// <returns>The value, 255 when a logged controller is unset, or -1 for an unlogged controller or absent trigger.</returns>
    /// <exception cref="ArgumentOutOfRangeException">channel is outside 0 through 15.</exception>
    public int GetController(int channel, byte controller)
    {
        CheckChannel(channel);
        if (controller == 119)
        {
            return LastTrigger;
        }

        var row = Array.IndexOf(AILSession.LoggedControls, controller);
        return row < 0 ? -1 : _controls[row, channel];
    }

    /// <summary>Applies a controller through sequence bookkeeping and XMIDI special-controller handling.</summary>
    /// <param name="channel">The zero-based logical channel, from 0 through 15.</param>
    /// <param name="controller">The controller number, from 0 through 127.</param>
    /// <param name="value">The controller value, from 0 through 127.</param>
    /// <exception cref="ArgumentOutOfRangeException">channel is invalid, or controller or value exceeds 127.</exception>
    /// <exception cref="InvalidDataException">A pending indirect controller exceeds the lookup table.</exception>
    public void SetController(int channel, byte controller, byte value)
    {
        Check();
        CheckChannel(channel);
        if (controller > 127 || value > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(controller));
        }

        Control(channel, controller, value);
    }

    /// <summary>Validates a zero-based MIDI channel.</summary>
    /// <param name="channel">The channel number.</param>
    /// <exception cref="ArgumentOutOfRangeException">channel is outside 0 through 15.</exception>
    private static void CheckChannel(int channel)
    {
        if ((uint)channel >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    /// <summary>Counts active duration-tracked notes on a logical channel.</summary>
    /// <param name="channel">The zero-based logical channel, from 0 through 15.</param>
    /// <returns>The number of note slots assigned to the channel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">channel is outside 0 through 15.</exception>
    public int ChannelNotes(int channel)
    {
        CheckChannel(channel);
        return _notes.Count(n => n.Channel == channel);
    }

    /// <summary>Changes the output channel used by subsequent events without acquiring a lock.</summary>
    /// <param name="logicalChannel">The zero-based sequence channel, from 0 through 15.</param>
    /// <param name="physicalChannel">The zero-based output channel, from 0 through 15.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either channel is outside 0 through 15.</exception>
    public void MapChannel(int logicalChannel, int physicalChannel)
    {
        Check();
        CheckChannel(logicalChannel);
        CheckChannel(physicalChannel);
        _map[logicalChannel] = (byte)physicalChannel;
    }

    /// <summary>Looks up the output channel for a logical sequence channel.</summary>
    /// <param name="logicalChannel">The zero-based sequence channel, from 0 through 15.</param>
    /// <returns>The mapped zero-based output channel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">logicalChannel is outside 0 through 15.</exception>
    public int TrueChannel(int logicalChannel)
    {
        CheckChannel(logicalChannel);
        return _map[logicalChannel];
    }

    /// <summary>Selects a declared RBRN destination, flushing notes and loop state.</summary>
    /// <param name="marker">The branch identifier.</param>
    /// <returns>True when the cursor changed; false leaves the sequence unchanged.</returns>
    public bool Branch(byte marker)
    {
        Check();
        if (!_track.Branches.TryGetValue(marker, out var offset))
        {
            return false;
        }

        EventOffset = offset;
        _interval = 0;
        FlushNotes();
        Array.Fill(_loopCount, -1);
        return true;
    }

    /// <summary>Emits note-off messages for tracked notes and frees their slots.</summary>
    /// <param name="channel">The logical channel to flush, or null for all channels.</param>
    internal void FlushNotes(int? channel = null)
    {
        foreach (var note in _notes)
        {
            if (note.Channel >= 0 && (channel is null || note.Channel == channel))
            {
                var physical = _map[note.Channel];
                note.Channel = -1;
                _session.ActiveNotes[physical]--;
                _session.Driver!.Send((byte)(0x80 | physical), note.Key, 0);
            }
        }
    }

    /// <summary>Releases sustain, locks and protection established by this sequence.</summary>
    private void ResetOwnedResources()
    {
        for (var ch = 0; ch < 16; ch++)
        {
            if ((sbyte)_controls[4, ch] >= 64)
            {
                _session.GlobalControls[4, ch] = 0;
                _session.Driver!.Send((byte)(0xb0 | ch), 64, 0);
            }

            if ((sbyte)_controls[6, ch] >= 64)
            {
                _session.FlushChannel(ch);
                _session.ReleaseChannel(_map[ch]);
                _map[ch] = (byte)ch;
            }

            if ((sbyte)_controls[7, ch] >= 64)
            {
                _session.Locks[ch] &= 191;
            }

            if ((sbyte)_controls[8, ch] >= 64)
            {
                _session.Driver!.Send((byte)(0xb0 | ch), 112, 0);
            }
        }
    }

    /// <summary>Re-emits logged volume controls using the current relative-volume arithmetic.</summary>
    private void UpdateVolume()
    {
        for (var ch = 0; ch < 16; ch++)
        {
            if (_controls[0, ch] != 255)
            {
                var volume = (byte)Math.Min(127u, (uint)(_controls[0, ch] * (ushort)RelativeVolume) / 100);
                _session.GlobalControls[0, ch] = volume;
                if ((_session.Locks[ch] & 128) == 0)
                {
                    _session.Driver!.Send((byte)(0xb0 | _map[ch]), 7, volume);
                }
            }
        }
    }

    /// <summary>Interprets a controller, updates logs and forwards ordinary messages.</summary>
    /// <param name="ch">The logical channel.</param>
    /// <param name="controller">The MIDI or XMIDI controller number.</param>
    /// <param name="value">The direct value, subject to a pending indirect lookup.</param>
    /// <exception cref="InvalidDataException">The pending indirect index exceeds the lookup table.</exception>
    private void Control(int ch, byte controller, byte value)
    {
        if (_indirect[ch] != 255)
        {
            var index = _indirect[ch];
            _indirect[ch] = 255;
            if (index >= _indirectValues.Length)
            {
                throw new InvalidDataException("Indirect controller table is missing or too short.");
            }

            value = _indirectValues[index];
        }

        var row = Array.IndexOf(AILSession.LoggedControls, controller);
        if (row >= 0)
        {
            _session.GlobalControls[row, ch] = _controls[row, ch] = value;
        }

        switch (controller)
        {
            case 7:
                if (RelativeVolume != 100)
                {
                    value = (byte)Math.Min(127u, unchecked((uint)(value * RelativeVolume)) / 100);
                    _session.GlobalControls[0, ch] = value;
                }

                break;
            case 115:
                _indirect[ch] = value;
                return;
            case 118:
                _beat = _measure = 0;
                _fraction = -_timeFraction;
                return;
            case 119:
                LastTrigger = value;
                _session.EmitTrigger(this, value);
                return;
            case 116:
                var available = Array.IndexOf(_loopCount, -1);
                if (available >= 0)
                {
                    _loopCount[available] = value;
                    _loopPosition[available] = EventOffset;
                }

                return;
            case 117:
                if (value < 64)
                {
                    return;
                }

                for (var i = 3; i >= 0; i--)
                {
                    if (_loopCount[i] != -1)
                    {
                        if (_loopCount[i] == 0)
                        {
                            UnboundedLoopIterations++;
                        }

                        if (_loopCount[i] == 0 || --_loopCount[i] != 0)
                        {
                            EventOffset = _loopPosition[i];
                        }
                        else
                        {
                            _loopCount[i] = -1;
                        }

                        return;
                    }
                }

                return;
            case 111:
                if (value >= 64)
                {
                    _session.Locks[ch] |= 64;
                }
                else
                {
                    _session.Locks[ch] &= 191;
                }

                return;
            case 110:
                if (value >= 64)
                {
                    var locked = _session.LockChannel();
                    _map[ch] = (byte)(locked < 0 ? ch : locked);
                }
                else
                {
                    _session.FlushChannel(ch);
                    _session.ReleaseChannel(_map[ch]);
                    _map[ch] = (byte)ch;
                }

                return;
        }

        if ((_session.Locks[ch] & 128) == 0)
        {
            _session.Driver!.Send((byte)(0xb0 | _map[ch]), controller, value);
        }
    }

    /// <summary>Advances notes, events and ramps for one 8,333-microsecond service.</summary>
    internal void Service()
    {
        if (Status != SequenceStatus.Playing)
        {
            return;
        }

        _tempoError = unchecked(_tempoError + RelativeTempo);
        while (_tempoError >= 100 && Status == SequenceStatus.Playing)
        {
            _tempoError -= 100;
            foreach (var note in _notes)
            {
                if (note.Channel >= 0 && --note.Duration < 0)
                {
                    var physical = _map[note.Channel];
                    note.Channel = -1;
                    _session.ActiveNotes[physical]--;
                    _session.Driver!.Send((byte)(0x80 | physical), note.Key, 0);
                }
            }

            _interval = (short)(_interval - 1);
            if (_interval <= 0)
            {
                ProcessEvents();
            }

            if (Status != SequenceStatus.Playing)
            {
                return;
            }

            _fraction = unchecked(_fraction + _timeFraction);
            if (_fraction >= _timePerBeat)
            {
                _fraction -= _timePerBeat;
                if (++_beat >= _numerator)
                {
                    _beat = 0;
                    _measure++;
                }
            }
        }

        if (RelativeTempo != _tempoTarget)
        {
            RelativeTempo = StepRamp(RelativeTempo, _tempoTarget, _tempoPeriod, ref _tempoAccum);
        }

        if (RelativeVolume != _volumeTarget)
        {
            var next = StepRamp(RelativeVolume, _volumeTarget, _volumePeriod, ref _volumeAccum);
            if (next != RelativeVolume)
            {
                RelativeVolume = next;
                UpdateVolume();
            }
        }
    }

    /// <summary>Applies whole ramp steps while retaining the original timing remainder.</summary>
    /// <param name="value">The current percentage.</param>
    /// <param name="target">The target percentage.</param>
    /// <param name="period">The original period per percentage point.</param>
    /// <param name="accumulator">The accumulated AIL time, updated with its remainder.</param>
    /// <returns>The next percentage without overshooting the target.</returns>
    private static int StepRamp(int value, int target, int period, ref int accumulator)
    {
        accumulator += 83;
        var steps = accumulator / period;
        accumulator %= period;
        return value < target ? Math.Min(target, value + steps) : Math.Max(target, value - steps);
    }

    /// <summary>Processes simultaneous events until a delay, stop or end-of-track boundary.</summary>
    /// <exception cref="InvalidDataException">The event stream cycles without advancing time or specifies zero tempo.</exception>
    private void ProcessEvents()
    {
        var events = _track.Events.Span;
        var progress = new HashSet<(int, int, int, int, int)>();
        while (Status == SequenceStatus.Playing)
        {
            if (!progress.Add((EventOffset, _loopCount[0], _loopCount[1], _loopCount[2], _loopCount[3])))
            {
                throw new InvalidDataException("XMIDI has a zero-time event cycle.");
            }

            var start = EventOffset;
            var at = start;
            var status = XmidiFile.ReadByte(events, ref at);
            if (status < 128)
            {
                EventOffset = at;
                _interval = status;
                return;
            }

            var kind = status & 0xf0;
            var ch = status & 15;
            if (kind == 0xf0)
            {
                var meta = status == 255 ? XmidiFile.ReadByte(events, ref at) : -1;
                var length = checked((int)XmidiFile.ReadVariable(events, ref at));
                var data = events.Slice(at, length);
                at += length;
                switch (meta)
                {
                    case 0x2f:
                        ResetOwnedResources();
                        Status = SequenceStatus.Done;
                        break;
                    case 0x58:
                        _numerator = data[0];
                        _timeFraction = data[1] < 2 ? 133333 >> (2 - data[1]) : 133333 << (data[1] - 2);
                        _fraction = -_timeFraction;
                        _beat = 0;
                        _measure++;
                        break;
                    case 0x51:
                        _timePerBeat = ((data[0] << 16) | (data[1] << 8) | data[2]) << 4;
                        if (_timePerBeat == 0)
                        {
                            throw new InvalidDataException("Zero XMIDI tempo.");
                        }

                        break;
                    case -1: _session.Driver!.SendSysEx(data, status); break;
                }
            }
            else
            {
                var d1 = XmidiFile.ReadByte(events, ref at);
                var d2 = kind is 0xc0 or 0xd0 ? (byte)0 : XmidiFile.ReadByte(events, ref at);
                switch (kind)
                {
                    case 0x80 or 0x90:
                    {
                        var duration = unchecked((int)XmidiFile.ReadVariable(events, ref at));
                        if ((_session.Locks[ch] & 128) == 0)
                        {
                            var note = Array.Find(_notes, n => n.Channel < 0) ?? _notes[0];
                            note.Channel = ch;
                            note.Key = d1;
                            note.Duration = unchecked(duration - 1);
                            var physical = _map[ch];
                            _session.ActiveNotes[physical]++;
                            _session.Driver!.Send((byte)(0x90 | physical), d1, d2);
                        }

                        break;
                    }
                    case 0xb0:
                        Control(ch, d1, d2);
                        break;
                    default:
                    {
                        switch (kind)
                        {
                            case 0xc0:
                                _session.Programs[ch] = _program[ch] = d1;
                                break;
                            case 0xe0:
                                _session.PitchLow[ch] = _pitchLow[ch] = d1;
                                _session.PitchHigh[ch] = _pitchHigh[ch] = d2;
                                break;
                        }

                        if ((_session.Locks[ch] & 128) == 0)
                        {
                            _session.Driver!.Send((byte)(kind | _map[ch]), d1, d2);
                        }

                        break;
                    }
                }
            }

            EventOffset += at - start;
        }
    }

    /// <summary>Computes the original look-ahead query without changing playback state.</summary>
    /// <returns>The beat and measure counters at their original width at the query boundary.</returns>
    private (ushort Beat, ushort Measure) AdvancedCount()
    {
        var beat = _beat;
        var measure = _measure;
        if (unchecked(_fraction + _timeFraction) >= _timePerBeat)
        {
            if (++beat >= _numerator)
            {
                beat = 0;
                measure++;
            }
        }

        return (beat, measure);
    }

    /// <summary>Stops this sequence and returns its slot to the session.</summary>
    /// <remarks>Repeated disposal does nothing. Dispose on the session thread.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Check();
        Pause();
        _session.Sequences[_slot] = null;
        _disposed = true;
    }

    /// <summary>Tracks an XMIDI note duration independently of FM voice allocation.</summary>
    private sealed class Note
    {
        /// <summary>Logical channel (-1 when free) and remaining event ticks.</summary>
        public int Channel = -1, Duration;

        /// <summary>The MIDI key used for note-off.</summary>
        public byte Key;
    }
}
