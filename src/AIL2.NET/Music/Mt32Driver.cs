using System.Runtime.InteropServices;
using System.Text;

namespace AIL2.Music;

/// <summary>REL105 MT-32 timbre/patch interpretation. The host owns message transport and synthesis.</summary>
/// <param name="session">The session owning the logical clock and channel state.</param>
/// <param name="output">The caller-owned MIDI transport sink.</param>
internal sealed class Mt32Driver(AILSession session, IMidiOutput output) : MusicDriver(session)
{
    /// <summary>The 64 uploaded timbre slots in original allocation order.</summary>
    private readonly Entry?[] _cache = new Entry[64];

    /// <summary>Program/bank selection per channel and the bank mapped to each device patch.</summary>
    private readonly byte[] _programs = Enumerable.Repeat((byte)255, 16).ToArray(),
        _banks = new byte[16],
        _patchBanks = new byte[128];

    /// <summary>Resolved channel timbres and the three controller-upload queues' addresses and byte counts.</summary>
    private readonly int[] _channelTimbres = Enumerable.Repeat(-1, 16).ToArray(),
        _addresses = new int[3],
        _counts = new int[3];

    /// <summary>Original controller-upload buffers, each holding at most 32 parameter bytes.</summary>
    private readonly byte[][] _queues = [new byte[32], new byte[32], new byte[32]];

    /// <summary>Accumulated XMIDI SysEx fragments awaiting an end marker.</summary>
    private readonly List<byte> _incomingSysEx = [];

    /// <summary>The original event-age counter used for timbre replacement.</summary>
    private uint _noteEvent;

    /// <summary>The earliest output time after previously requested MT-32 pacing delays.</summary>
    private TimeSpan _outputTime;

    /// <inheritdoc />
    public override int CacheCapacity => 0;

    /// <summary>The later of session time and the accumulated transport deadline.</summary>
    private TimeSpan Timestamp => _outputTime > Session.Time ? _outputTime : Session.Time;

    /// <summary>Emits a complete MIDI message at the paced logical timestamp.</summary>
    /// <param name="bytes">The borrowed message bytes.</param>
    private void Message(ReadOnlySpan<byte> bytes)
    {
        output.SendMessage(Timestamp, bytes);
    }

    /// <summary>Converts the original pacing units to logical delay requests.</summary>
    /// <param name="units">The number of nominal 14 ms wait units.</param>
    internal void Wait(int units)
    {
        if (units == 0)
        {
            return;
        }

        var at = Timestamp;
        var duration = TimeSpan.FromMilliseconds(units * 14);
        output.Delay(at, duration);
        _outputTime = at + duration;
    }

    /// <summary>Resets the MT-32 and restores the original channel assignments.</summary>
    private void Reset()
    {
        SysEx(Address(127, 0, 0), [1], 12);
        SysEx(Address(0x10, 0, 13), [1, 2, 3, 4, 5, 6, 7, 8, 9], 4);
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        SysEx(Address(127, 0, 0), [1], 12);
        SysEx(Address(16, 0, 13), [1, 2, 3, 4, 5, 6, 7, 8, 9], 4);
        SysEx(Address(16, 0, 4), [3, 4, 3, 4, 3, 4, 3, 4, 4], 4);
        SysEx(Address(16, 0, 1), [0, 3, 2], 4);
    }

    /// <inheritdoc />
    public override void Shutdown()
    {
        Reset();
    }

    /// <summary>Packs three seven-bit MT-32 address components.</summary>
    /// <param name="a">The high address component.</param>
    /// <param name="b">The middle address component.</param>
    /// <param name="c">The low address component.</param>
    /// <returns>The linear 21-bit parameter address.</returns>
    private static int Address(int a, int b, int c)
    {
        return (a << 14) | (b << 7) | c;
    }

    /// <summary>Frames a Roland data-set message with its original checksum and pacing.</summary>
    /// <param name="address">The linear 21-bit destination address.</param>
    /// <param name="data">The parameter bytes.</param>
    /// <param name="wait">The original wait units requested after transmission.</param>
    private void SysEx(int address, ReadOnlySpan<byte> data, int wait = 0)
    {
        var message = new byte[data.Length + 10];
        message[0] = 0xf0;
        message[1] = 0x41;
        message[2] = 0x10;
        message[3] = 0x16;
        message[4] = 0x12;
        message[5] = (byte)((address >> 14) & 127);
        message[6] = (byte)((address >> 7) & 127);
        message[7] = (byte)(address & 127);
        data.CopyTo(message.AsSpan(8));
        var sum = message[5] + message[6] + message[7];
        foreach (var b in data)
        {
            sum += b;
        }

        message[^2] = (byte)((-sum) & 127);
        message[^1] = 0xf7;
        Message(message);
        Wait(wait);
    }

    /// <inheritdoc />
    public override void SendSysEx(ReadOnlySpan<byte> message, byte status = 0xf0)
    {
        if (status == 0xf0)
        {
            _incomingSysEx.Clear();
            _incomingSysEx.Add(0xf0);
        }

        foreach (var b in message)
        {
            _incomingSysEx.Add(b);
            if (b == 0xf7)
            {
                Message(CollectionsMarshal.AsSpan(_incomingSysEx));
                _incomingSysEx.Clear();
            }
        }
    }

    /// <inheritdoc />
    public override void WriteDisplay(string text)
    {
        Span<byte> bytes = stackalloc byte[20];
        bytes.Fill(32);
        var end = text.IndexOf('\0');
        if (end >= 0)
        {
            text = text[..end];
        }

        Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(20, text.Length)), bytes);
        SysEx(Address(32, 0, 0), bytes, 4);
    }

    /// <summary>Finds an uploaded timbre in the original slot order.</summary>
    /// <param name="key">The bank/patch word.</param>
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
        return key >> 8 is 0 or 127 || Index(key) >= 0;
    }

    /// <inheritdoc />
    public override void Protect(ushort? key, bool protect)
    {
        foreach (var entry in _cache)
        {
            if (entry is not null && (key is null || entry.Key == key))
            {
                entry.Protected = protect;
            }
        }
    }

    /// <inheritdoc />
    public override void Install(ushort key, ReadOnlyMemory<byte> bytes)
    {
        var bank = (byte)(key >> 8);
        var patch = (byte)key;
        if (bank == 127)
        {
            return;
        }

        if (bank != 0 && Index(key) < 0)
        {
            if (bytes.Length < 248)
            {
                throw new InvalidDataException("MT-32 timbre requires a length prefix and 246 parameter bytes.");
            }

            var index = Array.IndexOf(_cache, null);
            if (index < 0)
            {
                var oldest = uint.MaxValue;
                for (var i = 0; i < _cache.Length; i++)
                {
                    if (_cache[i] is { Protected: false } entry && entry.History <= oldest)
                    {
                        index = i;
                        oldest = entry.History;
                    }
                }

                if (index < 0)
                {
                    throw new InvalidOperationException("All MT-32 timbres are protected.");
                }
            }

            _cache[index] = new Entry(key, _noteEvent++);
            var data = bytes.Span[2..];
            var address = Address(8, index * 2, 0);
            SysEx(address, data[..14], 3);
            for (var partial = 0; partial < 4; partial++)
            {
                SysEx(address + 14 + (partial * 58), data.Slice(14 + (partial * 58), 58), 3);
            }
        }

        SetupPatch(patch, bank);
    }

    /// <summary>Writes part of a device patch using the original two-unit pacing.</summary>
    /// <param name="patch">The zero-based device patch number.</param>
    /// <param name="offset">The byte offset within its eight-byte parameters.</param>
    /// <param name="bytes">The replacement parameter bytes.</param>
    private void WritePatch(byte patch, int offset, ReadOnlySpan<byte> bytes)
    {
        SysEx(Address(5, 0, 0) + (patch * 8) + offset, bytes, 2);
    }

    /// <summary>Maps a device patch to a built-in or uploaded timbre.</summary>
    /// <param name="patch">The MIDI patch number.</param>
    /// <param name="bank">The bank selected by the original driver.</param>
    private void SetupPatch(byte patch, byte bank)
    {
        _patchBanks[patch] = bank;
        var index = bank == 0 ? -1 : Index((ushort)((bank << 8) | patch));
        if (index < 0)
        {
            WritePatch(patch, 0, [(byte)(patch < 64 ? 0 : 1), (byte)(patch & 63)]);
        }
        else
        {
            WritePatch(patch, 0, [2, (byte)index]);
        }
    }

    /// <inheritdoc />
    public override void Send(byte status, byte d1, byte d2)
    {
        var channel = status & 15;
        var kind = status & 0xf0;
        switch (kind)
        {
            case 0xc0:
            {
                _programs[channel] = d1;
                if (_banks[channel] != _patchBanks[d1])
                {
                    SetupPatch(d1, _banks[channel]);
                }

                _channelTimbres[channel] = Index((ushort)((_banks[channel] << 8) | d1));
                break;
            }
            case 0x90:
            {
                _noteEvent++;
                if (_channelTimbres[channel] >= 0 && _cache[_channelTimbres[channel]] is { } entry)
                {
                    entry.History = _noteEvent;
                }

                break;
            }
            case 0xb0:
            {
                if (d1 is >= 32 and <= 46)
                {
                    var queue = (d1 - 32) / 5;
                    var operation = (d1 - 32) % 5;
                    if (operation < 3)
                    {
                        var shift = (2 - operation) * 7;
                        _addresses[queue] = (_addresses[queue] & ~(127 << shift)) | (d2 << shift);
                    }
                    else
                    {
                        _queues[queue][_counts[queue]] = d2;
                        if (operation == 4 || _counts[queue] == 31)
                        {
                            var length = _counts[queue] + 1;
                            SysEx(_addresses[queue], _queues[queue].AsSpan(0, length));
                            if (operation == 3)
                            {
                                Wait(4);
                            }

                            _addresses[queue] += operation == 3 ? length : length - 1;
                            _counts[queue] = 0;
                        }
                        else
                        {
                            _counts[queue]++;
                        }
                    }

                    return;
                }

                switch (d1)
                {
                    case 114:
                        _banks[channel] = d2;
                        return;
                    case 113:
                        if (_channelTimbres[channel] >= 0 && _cache[_channelTimbres[channel]] is { } entry)
                        {
                            entry.Protected = d2 >= 64;
                        }

                        return;
                    case 59:
                    case 60:
                        if (_programs[channel] != 255)
                        {
                            WritePatch(_programs[channel], d1 == 59 ? 6 : 4, [d2]);
                            Message([(byte)(0xc0 | channel), _programs[channel]]);
                        }

                        return;
                    case 61:
                    case 62:
                    case 63:
                        SysEx(Address(16, 0, d1 - 60), [d2]);
                        return;
                    case 58:
                        if (_channelTimbres[channel] >= 0)
                        {
                            SysEx(Address(3, 1, 16) + ((d2 - 24) * 4), [(byte)_channelTimbres[channel]], 4);
                        }

                        return;
                }

                if (d1 is >= 110 and <= 120)
                {
                    return;
                }

                break;
            }
        }

        if (kind is 0xc0 or 0xd0)
        {
            Message([status, d1]);
        }
        else
        {
            Message([status, d1, d2]);
        }
    }

    /// <summary>Tracks one uploaded timbre's key, age and eviction protection.</summary>
    /// <param name="key">The bank/patch word.</param>
    /// <param name="history">The original event age at installation.</param>
    private sealed class Entry(ushort key, uint history)
    {
        /// <summary>The original bank/patch word.</summary>
        public readonly ushort Key = key;

        /// <summary>The original event age of the most recent use.</summary>
        public uint History = history;

        /// <summary>Whether this entry resists eviction.</summary>
        public bool Protected;
    }
}
