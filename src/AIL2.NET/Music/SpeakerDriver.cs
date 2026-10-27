namespace AIL2.Music;

/// <summary>Managed port of SPKR.INC for the IBM PC internal speaker: one voice shared by two note slots on MIDI channel 2.</summary>
/// <remarks>
///     AIL 2.14 follows SPKR.INC 1.02 as A214 and REL105 build it: pitch bends interpolate between the divisors twelve
///     semitones apart, modulation is emulated at 20 Hz, and the timer is reloaded only when the divisor changes. The
///     1991 driver predates those options. It bends in semitone steps around 2000h, emulates no modulation, reloads
///     the timer on every pitch update, and its two lowest divisor table entries differ. It is also serviced at 480 Hz
///     instead of 120 Hz. Velocity, volume and expression only drive the Tandy build's attenuation, so the IBM PC build
///     ignores them.
/// </remarks>
/// <param name="session">The owning session.</param>
/// <param name="revision">The driver release whose behavior to follow.</param>
/// <param name="output">The caller-owned speaker sink.</param>
internal sealed class SpeakerDriver(AILSession session, DriverRevision revision, ISpeakerOutput output) : MusicDriver(session)
{
    /// <summary>SPKR.INC value_l/value_h: the timer divisor for each of the 96 notes, lowest first.</summary>
    private static readonly ushort[] Divisors =
    [
        36488, 34485, 64846, 61504, 57921, 54733, 51652, 48701, 45891, 43388, 41002, 38614,
        36488, 34485, 32511, 30673, 28960, 27303, 25826, 24350, 22990, 21694, 20466, 19338,
        18244, 17217, 16255, 15336, 14480, 13667, 12899, 12175, 11495, 10847, 10241, 9661,
        9122, 8608, 8127, 7668, 7240, 6833, 6449, 6087, 5744, 5423, 5118, 4832,
        4561, 4304, 4062, 3835, 3620, 3416, 3224, 3043, 2873, 2711, 2559, 2415,
        2280, 2152, 2031, 1917, 1809, 1708, 1612, 1521, 1436, 1355, 1279, 1207,
        1140, 1076, 1015, 958, 904, 854, 806, 760, 718, 677, 639, 603,
        570, 538, 507, 479, 452, 427, 403, 380, 359, 338, 319, 301
    ];

    /// <summary>The 1991 driver's table, which differs from SPKR.INC 1.02 in its first two entries.</summary>
    private static readonly ushort[] Divisors1991 = [7218, 3434, .. Divisors[2..]];

    /// <summary>The caller-owned speaker sink.</summary>
    private readonly ISpeakerOutput _output = output;

    /// <summary>The driver release whose behavior this driver follows.</summary>
    private readonly DriverRevision _revision = revision;

    /// <summary>The note slots; there are more than voices, so a new note can take the voice from an older one.</summary>
    private readonly Slot[] _slots = [new(), new()];

    /// <summary>Controller and allocation state for all MIDI channels.</summary>
    private readonly Channel[] _channels = Enumerable.Range(0, 16).Select(_ => new Channel()).ToArray();

    /// <summary>The channel owning the single voice, or -1 when it is free.</summary>
    private int _owner = -1;

    /// <summary>The AIL 2.14 modulation timer, in services times 20, and the sign of the current half-period.</summary>
    private int _modulationTime, _modulationSign;

    /// <summary>The divisor last loaded by AIL 2.14, which skips an identical reload; the original starts it at FFFFh.</summary>
    private ushort _lastDivisor = ushort.MaxValue;

    /// <inheritdoc />
    public override int CacheCapacity => 0;

    /// <inheritdoc />
    public override int LastChannel => 1;

    /// <inheritdoc />
    public override int LastLockableChannel => 1;

    /// <inheritdoc />
    /// <remarks>The 1991 driver declares 480 services per second and counts its beats and ramps in 2,083 µs intervals.</remarks>
    public override int Interval => _revision == DriverRevision.Ail1991 ? 2083 : base.Interval;

    /// <inheritdoc />
    public override void Initialize()
    {
        _output.SetGate(Session.Time, false);
    }

    /// <inheritdoc />
    public override void Shutdown()
    {
        _output.SetGate(Session.Time, false);
    }

    /// <inheritdoc />
    /// <remarks>The speaker uses no timbres, so every key counts as present.</remarks>
    public override bool HasTimbre(ushort key)
    {
        return true;
    }

    /// <inheritdoc />
    public override void Install(ushort key, ReadOnlyMemory<byte> bytes)
    {
    }

    /// <inheritdoc />
    public override void Protect(ushort? key, bool protect)
    {
    }

    /// <inheritdoc />
    public override void Send(byte status, byte d1, byte d2)
    {
        var ch = status & 15;
        var channel = _channels[ch];
        switch (status & 0xf0)
        {
            case 0x80: NoteOff(ch, d1); break;
            case 0x90:
                if (ch < 1 || ch > LastChannel)
                {
                    break;
                }

                if (d2 == 0)
                {
                    NoteOff(ch, d1);
                }
                else
                {
                    NoteOn(ch, d1);
                }

                break;
            case 0xe0:
                channel.PitchLow = d1;
                channel.PitchHigh = d2;
                UpdateChannel(ch);
                break;
            case 0xb0:
                switch (d1)
                {
                    case 112: channel.Protect = d2; break;
                    case 1: channel.Modulation = d2; break;
                    case 64:
                        channel.Sustain = d2;
                        if ((sbyte)d2 < 64)
                        {
                            ReleaseSustain(ch);
                        }

                        break;
                    case 121:
                        channel.Sustain = 0;
                        ReleaseSustain(ch);
                        channel.Modulation = 0;
                        channel.PitchLow = 0;
                        channel.PitchHigh = 64;
                        UpdateChannel(ch);
                        break;
                    case 123:
                        foreach (var slot in _slots)
                        {
                            if (slot.Active && slot.Channel == ch)
                            {
                                NoteOff(ch, slot.Key);
                            }
                        }

                        break;
                }

                break;
        }
    }

    /// <summary>Runs AIL 2.14's modulation emulation; the 1991 driver has no service work.</summary>
    /// <remarks>
    ///     Every sixth service at 120 Hz the sign flips and each note's pitch moves by 5 bend units per modulation step,
    ///     which is then written through the normal pitch update.
    /// </remarks>
    public override void Service()
    {
        if (_revision != DriverRevision.Ail214)
        {
            return;
        }

        _modulationTime += 20;
        if (_modulationTime < 120)
        {
            return;
        }

        _modulationTime -= 120;
        _modulationSign ^= 1;
        foreach (var slot in _slots)
        {
            if (!slot.Active)
            {
                continue;
            }

            var depth = 5 * _channels[slot.Channel].Modulation;
            slot.Modulation = (short)(_modulationSign != 0 ? -depth : depth);
            Update(slot);
        }
    }

    /// <summary>Takes the first free slot, then the voice if it is free or the priorities give it up.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    /// <param name="key">The MIDI key.</param>
    private void NoteOn(int channel, byte key)
    {
        var slot = Array.Find(_slots, s => !s.Active);
        if (slot is null)
        {
            return;
        }

        slot.Active = true;
        slot.Channel = channel;
        slot.Key = key;
        slot.Sustained = false;
        slot.Voiced = false;
        if (_owner < 0)
        {
            Attach(slot);
        }
        else
        {
            UpdatePriority();
        }
    }

    /// <summary>Releases matching notes or retains them under sustain.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    /// <param name="key">The MIDI key.</param>
    private void NoteOff(int channel, int key)
    {
        foreach (var slot in _slots)
        {
            if (!slot.Active || slot.Key != key || slot.Channel != channel)
            {
                continue;
            }

            if ((sbyte)_channels[channel].Sustain >= 64)
            {
                slot.Sustained = true;
            }
            else
            {
                Release(slot);
                slot.Active = false;
            }
        }
    }

    /// <summary>Releases notes retained by the channel's sustain controller.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    private void ReleaseSustain(int channel)
    {
        foreach (var slot in _slots)
        {
            if (slot.Active && slot.Channel == channel && slot.Sustained)
            {
                NoteOff(channel, slot.Key);
            }
        }
    }

    /// <summary>Gives a slot the voice and sounds it.</summary>
    /// <param name="slot">The slot taking the free voice.</param>
    private void Attach(Slot slot)
    {
        slot.Voiced = true;
        _channels[slot.Channel].Voices++;
        _owner = slot.Channel;
        Update(slot);
    }

    /// <summary>Turns the speaker off and frees the voice of a voiced slot.</summary>
    /// <param name="slot">The slot to release; an unvoiced slot is unchanged.</param>
    private void Release(Slot slot)
    {
        if (!slot.Voiced)
        {
            return;
        }

        _output.SetGate(Session.Time, false);
        _channels[slot.Channel].Voices--;
        _owner = -1;
        slot.Voiced = false;
    }

    /// <summary>Moves the voice from the lowest-priority voiced slot to the highest-priority waiting slot.</summary>
    /// <remarks>
    ///     Priorities are computed once: 32767, or FFFFh under voice protection, less the channel's voice count. Ties
    ///     go to the waiting slot, and the slot that loses the voice is freed, not queued.
    /// </remarks>
    private void UpdatePriority()
    {
        var priorities = new int[_slots.Length];
        var active = 0;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Active)
            {
                continue;
            }

            active++;
            var channel = _channels[_slots[i].Channel];
            priorities[i] = Math.Max(0, ((sbyte)channel.Protect >= 64 ? 0xffff : 32767) - channel.Voices);
        }

        if (active <= 1)
        {
            return;
        }

        for (var remaining = active; remaining > 0; remaining--)
        {
            int high = -1, low = -1, highPriority = 0, lowPriority = 0xffff;
            for (var i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].Active)
                {
                    continue;
                }

                if (!_slots[i].Voiced)
                {
                    if (priorities[i] >= highPriority)
                    {
                        highPriority = priorities[i];
                        high = i;
                    }
                }
                else if (priorities[i] <= lowPriority)
                {
                    lowPriority = priorities[i];
                    low = i;
                }
            }

            if (high < 0 || low < 0 || highPriority < lowPriority)
            {
                return;
            }

            Release(_slots[low]);
            _slots[low].Active = false;
            Attach(_slots[high]);
        }
    }

    /// <summary>Rewrites the pitch of every note on a channel.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    private void UpdateChannel(int channel)
    {
        foreach (var slot in _slots)
        {
            if (slot.Active && slot.Channel == channel)
            {
                Update(slot);
            }
        }
    }

    /// <summary>Loads the timer with a voiced slot's divisor and turns the speaker on.</summary>
    /// <param name="slot">The slot to sound; an unvoiced slot writes nothing.</param>
    private void Update(Slot slot)
    {
        if (!slot.Voiced)
        {
            return;
        }

        var channel = _channels[slot.Channel];
        var pitch = (channel.PitchHigh << 7) | channel.PitchLow;
        var note = Note(slot.Key);
        if (_revision == DriverRevision.Ail1991)
        {
            _output.SetDivisor(Session.Time, Semitone(note, pitch));
        }
        else
        {
            var divisor = Interpolate(note, pitch + slot.Modulation);
            if (divisor != _lastDivisor)
            {
                _output.SetDivisor(Session.Time, divisor);
                _lastDivisor = divisor;
            }
        }

        _output.SetGate(Session.Time, true);
    }

    /// <summary>Maps a key to its table note, folding by octaves into 0 through 95 exactly as SPKR.INC does.</summary>
    /// <param name="key">The MIDI key.</param>
    /// <returns>The table index; keys 12 through 107 map to key - 12.</returns>
    private static int Note(int key)
    {
        var note = key - 24;
        do
        {
            note += 12;
        }
        while (note < 0);

        note += 12;
        do
        {
            note -= 12;
        }
        while (note > 95);

        return note;
    }

    /// <summary>Applies a bend in semitone steps, as the 1991 driver does.</summary>
    /// <param name="note">The table note.</param>
    /// <param name="pitch">The 14-bit bend value; 2000h is centered.</param>
    /// <returns>The divisor of the nearest table note after the bend.</returns>
    /// <remarks>The bend becomes sixteenths of a semitone over a 12-semitone range, rounded and folded into the table.</remarks>
    private static ushort Semitone(int note, int pitch)
    {
        var position = (short)(pitch - 0x2000) >> 5;
        position = (short)(position * 12);
        position = (short)(position + (note << 8));
        position = (short)(position + 8) >> 4;
        position -= 0xc0;
        do
        {
            position += 0xc0;
        }
        while (position < 0);

        position += 0xc0;
        do
        {
            position -= 0xc0;
        }
        while (position > 0x5ff);

        return Divisors1991[position >> 4];
    }

    /// <summary>Interpolates between the divisors twelve semitones apart, as AIL 2.14 does.</summary>
    /// <param name="note">The table note.</param>
    /// <param name="pitch">The 14-bit bend value plus the modulation offset; 2000h is centered.</param>
    /// <returns>The divisor in the original 16-bit arithmetic.</returns>
    private static ushort Interpolate(int note, int pitch)
    {
        var low = Divisors[Math.Max(note - 12, 0)];
        var high = Divisors[Math.Min(note + 12, 95)];
        var middle = Divisors[note];
        var bend = (short)(pitch - 0x2000);
        var range = (short)(bend < 0 ? middle - low : high - middle);
        return (ushort)((bend * range / 0x2000) + middle);
    }

    /// <summary>Per-channel controller and voice-count state.</summary>
    private sealed class Channel
    {
        /// <summary>Gets or sets the pitch-bend low byte.</summary>
        public byte PitchLow { get; set; }

        /// <summary>Gets or sets the pitch-bend high byte.</summary>
        public byte PitchHigh { get; set; }

        /// <summary>Gets or sets the sustain pedal value.</summary>
        public byte Sustain { get; set; }

        /// <summary>Gets or sets the modulation depth.</summary>
        public byte Modulation { get; set; }

        /// <summary>Gets or sets the voice protection value; 64 or more protects the channel's notes.</summary>
        public byte Protect { get; set; }

        /// <summary>Gets or sets the number of the channel's notes holding the voice.</summary>
        public int Voices { get; set; }
    }

    /// <summary>One note slot.</summary>
    private sealed class Slot
    {
        /// <summary>Gets or sets whether the slot holds a note.</summary>
        public bool Active { get; set; }

        /// <summary>Gets or sets whether the note holds the voice.</summary>
        public bool Voiced { get; set; }

        /// <summary>Gets or sets the zero-based MIDI channel.</summary>
        public int Channel { get; set; }

        /// <summary>Gets or sets the MIDI key.</summary>
        public int Key { get; set; }

        /// <summary>Gets or sets whether a note-off arrived under sustain.</summary>
        public bool Sustained { get; set; }

        /// <summary>Gets or sets AIL 2.14's modulation offset in bend units; it stays with the slot across notes.</summary>
        public short Modulation { get; set; }
    }
}
