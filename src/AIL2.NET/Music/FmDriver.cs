namespace AIL2.Music;

/// <summary>Managed port of REL105 YAMAHA32.INC musical behavior and original register ordering.</summary>
internal sealed class FmDriver : MusicDriver
{
    /// <summary>Original dirty-bit masks controlling operator, level, envelope, waveform, feedback, and pitch writes.</summary>
    private const int All = 0xf9, Avekm = 0x80, Level = 0x40, Adsr = 0x20, Wave = 0x10, Feedback = 8, Frequency = 1;

    /// <summary>The selected Miles FM profile.</summary>
    private readonly MusicProfile _profile;

    /// <summary>The caller-owned register and mixer sink.</summary>
    private readonly IOplOutput _output;

    /// <summary>Whether the selected profile uses OPL3 or two independent OPL2 chips.</summary>
    private readonly bool _opl3, _dual;

    /// <summary>Controller and allocation state for all MIDI channels.</summary>
    private readonly Channel[] _channels = Enumerable.Range(0, 16).Select(_ => new Channel()).ToArray();

    /// <summary>Original note slots, including notes waiting for a synthesizer voice.</summary>
    private readonly Voice[] _slots;

    /// <summary>Channel owning each synthesizer voice, or -1 when free.</summary>
    private readonly int[] _owners;

    /// <summary>Resident instrument entries in original cache-slot order.</summary>
    private readonly Patch?[] _cache = new Patch[192];

    /// <summary>Percussion note-to-cache lookups, with -1 meaning unresolved.</summary>
    private readonly int[] _rhythm = Enumerable.Repeat(-1, 128).ToArray();

    /// <summary>Cache byte use and rotating two/four-operator allocation cursors.</summary>
    private int _usedBytes, _rover2 = -1, _rover4 = -1;

    /// <summary>The original-width event age used by cache replacement.</summary>
    private uint _noteEvent;

    /// <summary>Current OPL3 four-operator connection mask.</summary>
    private byte _connections;

    /// <inheritdoc />
    public override int CacheCapacity => 3584;

    /// <summary>Allocates the original FM state for the selected topology.</summary>
    /// <param name="session">The owning session.</param>
    /// <param name="profile">The Miles FM music profile.</param>
    /// <param name="output">The caller-owned output sink.</param>
    public FmDriver(AILSession session, MusicProfile profile, IOplOutput output) : base(session)
    {
        _profile = profile;
        _output = output;
        _opl3 = profile.IsOpl3();
        _dual = profile.IsDualOpl2();
        _slots = Enumerable.Range(0, _opl3 ? 20 : 16).Select(_ => new Voice()).ToArray();
        _owners = Enumerable.Repeat(-1, _opl3 ? 18 : 9).ToArray();
    }

    /// <summary>Emits a register write, duplicating it for dual OPL2 when requested.</summary>
    /// <param name="register">The chip-local register address.</param>
    /// <param name="value">The value narrowed to the byte width used by the original driver.</param>
    /// <param name="chip">The chip index to use, or -1 for the profile's default routing.</param>
    private void Write(int register, int value, int chip = -1)
    {
        if (_dual && chip < 0)
        {
            _output.WriteRegister(Session.Time, 0, (ushort)register, (byte)value);
            _output.WriteRegister(Session.Time, 1, (ushort)register, (byte)value);
        }
        else
        {
            _output.WriteRegister(Session.Time, chip < 0 ? 0 : chip, (ushort)register, (byte)value);
        }
    }

    /// <summary>Maps an original operator index to its register bank and offset.</summary>
    /// <param name="op">The operator index from the original table.</param>
    /// <param name="address">The register-group base address.</param>
    /// <param name="value">The register value.</param>
    /// <param name="chip">The chip index, or -1 for default routing.</param>
    private void Operator(int op, int address, int value, int chip = -1)
    {
        Write(address + FmTables.OpIndex[op] + (FmTables.OpArray[op] * 256), value, chip);
    }

    /// <summary>Maps a synthesizer voice to its channel-register bank and offset.</summary>
    /// <param name="voice">The zero-based synthesizer voice.</param>
    /// <param name="address">The channel-register base address.</param>
    /// <param name="value">The register value.</param>
    private void VoiceRegister(int voice, int address, int value)
    {
        Write(address + (voice % 9) + ((voice / 9) * 256), value);
    }

    /// <summary>Emits the original register initialization tables in their defined order.</summary>
    private void ResetRegisters()
    {
        if (_opl3)
        {
            Write(0x105, 1);
            Write(0x104, 0);
            _connections = 0;
        }

        for (var i = 0; i < FmTables.Array0Init.Length; i++)
        {
            Write(i + 1, FmTables.Array0Init[i]);
        }

        if (_opl3)
        {
            for (var i = 0; i < FmTables.Array1Init.Length; i++)
            {
                Write(i + 0x101, FmTables.Array1Init[i]);
            }
        }
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        ResetRegisters();
    }

    /// <inheritdoc />
    public override void Shutdown()
    {
        ResetRegisters();
        if (_opl3)
        {
            Write(0x105, 0);
        }
    }

    /// <summary>Finds a resident key in original cache-slot order.</summary>
    /// <param name="key">The packed bank/patch word.</param>
    /// <returns>The slot index, or -1 when absent.</returns>
    private int Index(ushort key)
    {
        for (var i = 0; i < _cache.Length; i++)
        {
            if (_cache[i]?.Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public override bool HasTimbre(ushort key)
    {
        return Index(key) >= 0;
    }

    /// <inheritdoc />
    public override void Protect(ushort? key, bool protect)
    {
        foreach (var patch in _cache)
        {
            if (patch is not null && (key is null || patch.Key == key))
            {
                patch.Protected = protect;
            }
        }
    }

    /// <inheritdoc />
    public override void Install(ushort key, ReadOnlyMemory<byte> bytes)
    {
        var index = Index(key);
        if (index < 0)
        {
            if (bytes.Length is not (14 or 25))
            {
                throw new NotSupportedException(
                    "Only original BNK and OPL3 timbres are supported; TVFX is outside this port.");
            }

            if (!_opl3 && bytes.Length == 25)
            {
                throw new NotSupportedException(
                    "This bank contains four-operator instruments. Select an OPL3 chip mode or a two-operator bank.");
            }

            while (_usedBytes + bytes.Length > CacheCapacity || Array.IndexOf(_cache, null) < 0)
            {
                Evict();
            }

            index = Array.IndexOf(_cache, null);
            _cache[index] = new Patch(key, bytes.ToArray(), _noteEvent++);
            _usedBytes += bytes.Length;
        }

        foreach (var channel in _channels)
        {
            if (channel.Program == (byte)key && channel.Bank == (byte)(key >> 8))
            {
                channel.Timbre = index;
            }
        }
    }

    /// <summary>Evicts the oldest eligible timbre and releases its voices and lookup references.</summary>
    /// <exception cref="InvalidOperationException">Every resident entry is protected.</exception>
    private void Evict()
    {
        var index = -1;
        var oldest = uint.MaxValue;
        for (var i = 0; i < _cache.Length; i++)
        {
            if (_cache[i] is { Protected: false } p && p.History <= oldest)
            {
                index = i;
                oldest = p.History;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException("Timbre cache is full and every entry is protected.");
        }

        var deleted = _cache[index]!;
        foreach (var channel in _channels)
        {
            if (channel.Timbre == index)
            {
                channel.Timbre = -1;
            }
        }

        for (var i = 0; i < _rhythm.Length; i++)
        {
            if (_rhythm[i] == index)
            {
                _rhythm[i] = -1;
            }
        }

        foreach (var slot in _slots)
        {
            if (slot.Active && ReferenceEquals(slot.Patch, deleted))
            {
                Release(slot);
            }
        }

        _cache[index] = null;
        _usedBytes -= deleted.Bytes.Length;
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
                if (ch is < 1 or > 9)
                {
                    break;
                }

                if (d2 == 0)
                {
                    NoteOff(ch, d1);
                }
                else
                {
                    NoteOn(ch, d1, d2);
                }

                break;
            case 0xc0:
                channel.Program = d1;
                channel.Timbre = Index((ushort)((channel.Bank << 8) | d1));
                break;
            case 0xe0:
                channel.PitchLow = d1;
                channel.PitchHigh = d2;
                UpdateChannel(ch, Frequency);
                break;
            case 0xb0:
                switch (d1)
                {
                    case 114: channel.Bank = d2; break;
                    case 112: channel.Protect = d2; break;
                    case 113:
                        if (channel.Timbre >= 0 && _cache[channel.Timbre] is { } patch)
                        {
                            patch.Protected = d2 >= 64;
                        }

                        break;
                    case 1:
                        channel.Modulation = d2;
                        UpdateChannel(ch, Avekm);
                        break;
                    case 7:
                        channel.Volume = d2;
                        UpdateChannel(ch, Level);
                        break;
                    case 11:
                        channel.Expression = d2;
                        UpdateChannel(ch, Level);
                        break;
                    case 10:
                        channel.Pan = d2;
                        UpdateChannel(ch, _opl3 ? Feedback : Level);
                        break;
                    case 64:
                        channel.Sustain = d2;
                        if (d2 < 64)
                        {
                            ReleaseSustain(ch);
                        }

                        break;
                    case 123:
                        foreach (var slot in _slots)
                        {
                            if (slot.Active && slot.Channel == ch)
                            {
                                NoteOff(ch, slot.Note);
                            }
                        }

                        break;
                    case 121:
                        channel.Sustain = 0;
                        ReleaseSustain(ch);
                        channel.Modulation = 0;
                        channel.Expression = 127;
                        channel.PitchLow = 0;
                        channel.PitchHigh = 64;
                        UpdateChannel(ch, Avekm | Level | Frequency);
                        break;
                }

                break;
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
                NoteOff(channel, slot.Note);
            }
        }
    }

    /// <summary>Marks and writes affected registers for all active notes on a channel.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    /// <param name="flags">The original register-group dirty mask.</param>
    private void UpdateChannel(int channel, int flags)
    {
        foreach (var slot in _slots)
        {
            if (slot.Active && slot.Channel == channel)
            {
                slot.Update |= flags;
                Update(slot);
            }
        }
    }

    /// <summary>Resolves melodic or percussion timbre state and allocates an original note slot.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    /// <param name="note">The original MIDI key.</param>
    /// <param name="velocity">The MIDI velocity mapped through the original curve.</param>
    private void NoteOn(int channel, byte note, byte velocity)
    {
        var index = _channels[channel].Timbre;
        if (channel == 9)
        {
            index = _rhythm[note];
            if (index < 0)
            {
                _rhythm[note] = index = Index((ushort)(0x7f00 | note));
            }
        }

        if (index < 0 || _cache[index] is not { } patch)
        {
            return;
        }

        patch.History = ++_noteEvent;
        var slot = Array.Find(_slots, s => !s.Active);
        if (slot is null)
        {
            return;
        }

        slot.Active = true;
        slot.Patch = patch;
        slot.Channel = channel;
        slot.Key = note;
        slot.Note = channel == 9 ? patch.Bytes[2] : note;
        slot.Transpose = channel == 9 ? (sbyte)0 : (sbyte)patch.Bytes[2];
        slot.Velocity = FmTables.VelGraph[velocity >> 3];
        slot.Sustained = false;
        slot.VoiceNumber = -1;
        slot.FourOperator = _opl3 && patch.Bytes.Length == 25;
        slot.KeyOn = true;
        slot.Update = All;
        Assign(slot);
    }

    /// <summary>Releases matching notes or retains them under sustain.</summary>
    /// <param name="channel">The zero-based MIDI channel.</param>
    /// <param name="note">The original MIDI key.</param>
    private void NoteOff(int channel, int note)
    {
        foreach (var slot in _slots)
        {
            if (!slot.Active || slot.Channel != channel || slot.Key != note)
            {
                continue;
            }

            if (_channels[channel].Sustain >= 64)
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

    /// <summary>Searches the rotating voice pool, then applies the original stealing priorities.</summary>
    /// <param name="slot">The active note awaiting a synthesizer voice.</param>
    private void Assign(Voice slot)
    {
        var limit = slot.FourOperator ? 6 : _owners.Length;
        for (var i = 0; i < limit; i++)
        {
            var voice = slot.FourOperator
                ? FmTables.Op4Voice[_rover4 = (_rover4 + 1) % 6]
                : _rover2 = (_rover2 + 1) % _owners.Length;
            if (_owners[voice] >= 0 || (slot.FourOperator && _owners[voice + 3] >= 0))
            {
                continue;
            }

            Attach(slot, voice);
            return;
        }

        UpdatePriority();
    }

    /// <summary>Associates a note with a synthesizer voice and writes its complete state.</summary>
    /// <param name="slot">The original note slot.</param>
    /// <param name="voice">The first synthesizer voice; four-operator notes also own its paired voice.</param>
    private void Attach(Voice slot, int voice)
    {
        slot.VoiceNumber = voice;
        _channels[slot.Channel].Voices++;
        _owners[voice] = slot.Channel;
        if (slot.FourOperator)
        {
            _owners[voice + 3] = slot.Channel;
        }

        slot.Update = All;
        Update(slot);
    }

    /// <summary>Clears key-on and returns an assigned synthesizer voice to the pool.</summary>
    /// <param name="slot">The original note slot to release.</param>
    private void Release(Voice slot)
    {
        if (slot.VoiceNumber < 0)
        {
            return;
        }

        slot.KeyOn = false;
        slot.Update |= Frequency;
        Update(slot);
        _channels[slot.Channel].Voices--;
        _owners[slot.VoiceNumber] = -1;
        if (slot.FourOperator)
        {
            _owners[slot.VoiceNumber + 3] = -1;
        }

        slot.VoiceNumber = -1;
        slot.Active = false;
    }

    /// <summary>Ranks waiting and assigned notes and performs original two/four-operator voice stealing.</summary>
    private void UpdatePriority()
    {
        foreach (var s in _slots)
        {
            if (s.Active)
            {
                s.Priority = Math.Max(0,
                    (_channels[s.Channel].Protect >= 64 ? 65535 : 32767) - _channels[s.Channel].Voices);
            }
        }

        for (var remaining = _slots.Count(s => s.Active); remaining > 0; remaining--)
        {
            Voice? high = null, low = null, lowFour = null;
            foreach (var s in _slots)
            {
                if (!s.Active)
                {
                    continue;
                }

                if (s.VoiceNumber < 0)
                {
                    if (high is null || s.Priority >= high.Priority)
                    {
                        high = s;
                    }
                }
                else
                {
                    if (low is null || s.Priority <= low.Priority)
                    {
                        low = s;
                    }

                    if (FmTables.Op4Base[s.VoiceNumber] != 0 && (lowFour is null || s.Priority <= lowFour.Priority))
                    {
                        lowFour = s;
                    }
                }
            }

            if (high is null || high.Priority == 0 || low is null || high.Priority < low.Priority)
            {
                return;
            }

            if (high.FourOperator)
            {
                if (lowFour is null)
                {
                    return;
                }

                low = lowFour;
                if (!low.FourOperator)
                {
                    // Original priority code indexes alt_voice by slot index, not assigned voice.
                    var alternate = FmTables.AltVoice[Array.IndexOf(_slots, low)];
                    foreach (var s in _slots)
                    {
                        if (s.Active && s.VoiceNumber == alternate)
                        {
                            Release(s);
                        }
                    }
                }
            }

            var voice = low.VoiceNumber;
            Release(low);
            Attach(high, voice);
        }
    }

    /// <summary>Applies the original 7-bit gain product and nonzero rounding rule.</summary>
    /// <param name="a">The first AIL gain.</param>
    /// <param name="b">The second AIL gain.</param>
    /// <returns>The scaled gain, preserving zero.</returns>
    private static int Scale127(int a, int b)
    {
        var value = a * b / 128;
        return value == 0 ? 0 : value + 1;
    }

    /// <summary>Writes dirty operator and channel state in the original register order.</summary>
    /// <param name="slot">The assigned note whose dirty flags are consumed.</param>
    private void Update(Voice slot)
    {
        if (slot.VoiceNumber < 0)
        {
            return;
        }

        var channel = _channels[slot.Channel];
        var patch = slot.Patch!.Bytes;
        var flags = slot.Update;
        var volume = Scale127(Scale127(channel.Volume, channel.Expression), slot.Velocity);
        var left = _dual ? Scale127(FmTables.PanGraph[channel.Pan], volume) : volume;
        var right = _dual ? Scale127(FmTables.PanGraph[127 - channel.Pan], volume) : volume;
        if (_opl3)
        {
            var mask = FmTables.ConnSel[slot.VoiceNumber];
            var next = (byte)(slot.FourOperator ? _connections | mask : _connections & ~mask);
            if (next != _connections)
            {
                _connections = next;
                Write(0x104, next);
                if (!slot.FourOperator)
                {
                    Operator(FmTables.AltOp0[slot.VoiceNumber], 0x80, 15);
                    Operator(FmTables.AltOp1[slot.VoiceNumber], 0x80, 15);
                    VoiceRegister(FmTables.AltVoice[slot.VoiceNumber], 0xb0, 0);
                }
            }
        }

        var connection = (patch[8] & 1) | (slot.FourOperator ? (patch[8] & 128) >> 6 : 0);
        for (var pair = slot.FourOperator ? 1 : 0; pair >= 0; pair--)
        {
            var voice = slot.VoiceNumber + (pair * 3);
            var op0 = FmTables.Op0[voice];
            var op1 = FmTables.Op1[voice];
            var mod = pair == 0 ? 3 : 14;
            var car = pair == 0 ? 9 : 20;
            var scaling = 2 | connection;
            if (slot.FourOperator)
            {
                scaling = pair == 0 ? FmTables.Carrier01[connection] : FmTables.Carrier23[connection];
            }

            if ((flags & Avekm) != 0)
            {
                var modulation = channel.Modulation >= 64 ? 64 : 0;
                Operator(op0, 0x20, patch[mod] | modulation);
                Operator(op1, 0x20, patch[car] | modulation);
            }

            if ((flags & Level) != 0)
            {
                WriteLevel(op0, patch[mod + 1], (scaling & 1) != 0, left, right);
                WriteLevel(op1, patch[car + 1], (scaling & 2) != 0, left, right);
            }

            if ((flags & Adsr) != 0)
            {
                Operator(op0, 0x60, patch[mod + 2]);
                Operator(op1, 0x60, patch[car + 2]);
                Operator(op0, 0x80, patch[mod + 3]);
                Operator(op1, 0x80, patch[car + 3]);
            }

            if ((flags & Wave) != 0)
            {
                Operator(op1, 0xe0, patch[car + 4]);
                Operator(op0, 0xe0, patch[mod + 4]);
            }

            if ((flags & Feedback) != 0)
            {
                var fb = pair == 0 ? patch[8] & 14 : 0;
                var value = fb | ((connection >> pair) & 1);
                if (_opl3)
                {
                    value |= 0x30;
                    switch (channel.Pan)
                    {
                        case <= 27:
                            value &= _profile == MusicProfile.AdLibGold ? 0xdf : 0xef;
                            break;
                        case >= 100:
                            value &= _profile == MusicProfile.AdLibGold ? 0xef : 0xdf;
                            break;
                    }
                }

                VoiceRegister(voice, 0xc0, value);
            }

            if ((flags & Frequency) != 0 && pair == 0)
            {
                if (!slot.KeyOn)
                {
                    VoiceRegister(voice, 0xb0, slot.FrequencyHigh & ~32);
                }
                else
                {
                    var frequency = CalculateFrequency(slot, channel);
                    VoiceRegister(voice, 0xa0, frequency & 255);
                    slot.FrequencyHigh = (byte)((frequency >> 8) | 32);
                    VoiceRegister(voice, 0xb0, slot.FrequencyHigh);
                }
            }
        }

        slot.Update = 0;
    }

    /// <summary>Writes operator attenuation, applying the AIL gain only to selected carriers.</summary>
    /// <param name="op">The operator index from the original table.</param>
    /// <param name="level">The original attenuation and key-scaling byte.</param>
    /// <param name="scale">Whether this operator follows output volume.</param>
    /// <param name="left">The left AIL gain.</param>
    /// <param name="right">The right AIL gain for dual OPL2.</param>
    private void WriteLevel(int op, byte level, bool scale, int left, int right)
    {
        var amplitude = (~level) & 63;

        if (_dual)
        {
            Operator(op, 0x40, Value(left), 0);
            Operator(op, 0x40, Value(right), 1);
        }
        else
        {
            Operator(op, 0x40, Value(left));
        }

        return;

        int Value(int volume)
        {
            return (63 - (scale ? amplitude * volume / 127 : amplitude)) | (level & 192);
        }
    }

    /// <summary>Translates note, transpose and pitch bend through the original frequency tables.</summary>
    /// <param name="slot">The original note state.</param>
    /// <param name="channel">The channel pitch state.</param>
    /// <returns>The packed OPL frequency number and octave block, without key-on.</returns>
    private static ushort CalculateFrequency(Voice slot, Channel channel)
    {
        var bend = (short)(((channel.PitchHigh << 7) | channel.PitchLow) - 0x2000);
        var pitch = (short)((bend >> 5) * 12);
        var note = slot.Note + slot.Transpose - 12;
        while (note < 0)
        {
            note += 12;
        }

        while (note > 95)
        {
            note -= 12;
        }

        pitch = (short)(pitch + (note << 8));
        pitch = (short)(pitch + 8);
        pitch >>= 4;
        while (pitch < 0)
        {
            pitch += 192;
        }

        while (pitch > 1535)
        {
            pitch -= 192;
        }

        var index = pitch >> 4;
        var frequency = (short)FmTables.FreqTable[(FmTables.NoteHalftone[index] * 16) + (pitch & 15)];
        var block = FmTables.NoteOctave[index] - 1;
        if (frequency < 0)
        {
            block++;
        }

        if (block < 0)
        {
            block++;
            frequency >>= 1;
        }

        return (ushort)((frequency & 1023) | (block << 10));
    }

    /// <summary>Owns one resident FM instrument and its eviction state.</summary>
    /// <param name="key">The bank/patch key.</param>
    /// <param name="bytes">The copied length-prefixed definition.</param>
    /// <param name="history">The original event age at installation.</param>
    private sealed class Patch(ushort key, byte[] bytes, uint history)
    {
        /// <summary>The bank/patch key.</summary>
        public readonly ushort Key = key;

        /// <summary>The copied original definition.</summary>
        public readonly byte[] Bytes = bytes;

        /// <summary>The last original event age associated with this timbre.</summary>
        public uint History = history;

        /// <summary>Whether the entry resists eviction.</summary>
        public bool Protected;
    }

    /// <summary>Tracks one MIDI channel's controller state and voice use.</summary>
    private sealed class Channel
    {
        /// <summary>Original program, bank, pitch and controller bytes.</summary>
        public byte Program = 255, Bank, PitchLow, PitchHigh, Volume, Expression, Modulation, Pan, Sustain, Protect;

        /// <summary>Resolved cache index (-1 when absent) and assigned voice count.</summary>
        public int Timbre = -1, Voices;
    }

    /// <summary>Tracks one active or waiting AIL note and its synthesizer assignment.</summary>
    private sealed class Voice
    {
        /// <summary>Allocation, topology, key-on and sustain flags.</summary>
        public bool Active, FourOperator, KeyOn, Sustained;

        /// <summary>The resident instrument retained by this note.</summary>
        public Patch? Patch;

        /// <summary>MIDI channel, assigned voice, dirty mask and stealing priority.</summary>
        public int Channel, VoiceNumber = -1, Update, Priority;

        /// <summary>Original key, translated note, curved velocity and last frequency-high register byte.</summary>
        public byte Key, Note, Velocity, FrequencyHigh;

        /// <summary>The signed semitone transpose from the timbre.</summary>
        public sbyte Transpose;
    }
}
