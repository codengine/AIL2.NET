using System.Runtime.InteropServices;

namespace AIL2.Parity;

/// <summary>
///     Substitutes the external hardware a music driver talks to: OPL register ports, the MPU-401 data and command
///     ports, the retrace status the MT-32 driver waits on, and the speaker's PIT channel 2 and port 61h. Output reaches
///     the comparison trace in guest order.
/// </summary>
/// <param name="profile">The driver profile whose port layout and transport replies apply.</param>
/// <param name="trace">The trace receiving register writes, MIDI messages, speaker writes and transport delays.</param>
internal sealed class MusicBoundary(MusicProfile profile, Trace trace)
{
    /// <summary>The last address selected on each guest OPL address port.</summary>
    private readonly Dictionary<uint, byte> _selected = [];

    /// <summary>Port 61h as last written; the speaker driver reads it back and changes only bits 0 and 1.</summary>
    private byte _speaker;

    /// <summary>PIT channel 2 count bytes received since the last control word, or -1 before a control word.</summary>
    private int _countBytes = -1;

    /// <summary>The low count byte awaiting its high byte.</summary>
    private byte _countLow;

    /// <summary>Guest MIDI bytes awaiting a complete message.</summary>
    private readonly List<byte> _message = [];

    /// <summary>The alternating retrace response used only for original MT-32 wait loops.</summary>
    private bool _retrace;

    /// <summary>Pending wait ticks and the paced MIDI output timestamp.</summary>
    private long _pendingDelay, _outputTicks;

    /// <summary>Gets or sets the logical timestamp assigned by the comparison schedule.</summary>
    public TimeSpan Time { get; set; }

    /// <summary>Supplies only the documented external status responses required by this music driver.</summary>
    /// <param name="port">The guest input port.</param>
    /// <param name="size">The access width in bytes.</param>
    /// <returns>The modeled status byte.</returns>
    /// <exception cref="InvalidOperationException">The access width or port is outside the documented oracle boundaries.</exception>
    public uint ReadPort(uint port, int size)
    {
        if (size != 1)
        {
            throw new InvalidOperationException($"Unexpected input width {size}.");
        }

        if (port == 0x3da && profile == MusicProfile.Mt32)
        {
            _retrace = !_retrace;
            if (!_retrace)
            {
                _pendingDelay += 140_000;
            }

            return _retrace ? 8u : 0u;
        }

        if (profile == MusicProfile.Mt32 && port is 0x330 or 0x331)
        {
            return port == 0x330 ? 0xfeu : 0u;
        }

        if (profile == MusicProfile.PcSpeaker && port == 0x61)
        {
            return _speaker;
        }

        return OplPort(port) is { Data: false } ? (uint)0 : throw new InvalidOperationException($"Unexpected IN {port:X}.");
    }

    /// <summary>Translates profile-specific guest OPL ports to independent chip/register-bank identities.</summary>
    /// <param name="port">The guest port address.</param>
    /// <returns>The chip, register bank and data/address flag, or null for a non-OPL port.</returns>
    private (int Chip, int Bank, bool Data)? OplPort(uint port)
    {
        uint first = profile switch
        {
            MusicProfile.SoundBlaster => 0x228,
            MusicProfile.SoundBlasterPro1 or MusicProfile.SoundBlasterPro2 => 0x220, _ => 0x388
        };
        if (profile is MusicProfile.Mt32 or MusicProfile.PcSpeaker)
        {
            return null;
        }

        if (port < first || port > first + (profile.IsOpl3() || profile.IsDualOpl2() ? 3 : 1))
        {
            return null;
        }

        var bank = (int)((port - first) / 2);
        return (profile.IsDualOpl2() ? bank : 0, profile.IsOpl3() ? bank : 0, (port & 1) != 0);
    }

    /// <summary>Emits accumulated original MT-32 waits as one logical delay boundary.</summary>
    public void FlushDelay()
    {
        if (_pendingDelay == 0)
        {
            return;
        }

        _outputTicks = Math.Max(_outputTicks, Time.Ticks);
        trace.Delay(TimeSpan.FromTicks(_outputTicks), TimeSpan.FromTicks(_pendingDelay));
        _outputTicks += _pendingDelay;
        _pendingDelay = 0;
    }

    /// <summary>Captures original OPL, MIDI and speaker output and permits only documented transport commands.</summary>
    /// <param name="port">The guest output port.</param>
    /// <param name="size">The access width in bytes.</param>
    /// <param name="value">The guest output value.</param>
    /// <exception cref="InvalidOperationException">The width, command or port is outside the documented oracle boundaries.</exception>
    public void WritePort(uint port, int size, uint value)
    {
        if (size != 1)
        {
            throw new InvalidOperationException($"Unexpected output width {size}.");
        }

        switch (profile)
        {
            case MusicProfile.Mt32 when port == 0x331:
            {
                if (value is not (0xff or 0x3f))
                {
                    throw new InvalidOperationException($"Unexpected MPU command {value:X}.");
                }

                return;
            }
            case MusicProfile.Mt32 when port == 0x330:
            {
                FlushDelay();
                _message.Add((byte)value);
                var status = _message[0];
                if (status == 0xf0 ? value == 0xf7 : _message.Count == ((status & 0xf0) is 0xc0 or 0xd0 ? 2 : 3))
                {
                    trace.SendMessage(TimeSpan.FromTicks(Math.Max(_outputTicks, Time.Ticks)),
                        CollectionsMarshal.AsSpan(_message));
                    _message.Clear();
                }

                return;
            }
            case MusicProfile.PcSpeaker when port == 0x43:
            {
                // SPKR.INC writes only channel 2, low byte then high byte, square wave, binary.
                if (value != 0xb6)
                {
                    throw new InvalidOperationException($"Unexpected PIT control word {value:X}.");
                }

                _countBytes = 0;
                return;
            }
            case MusicProfile.PcSpeaker when port == 0x42:
            {
                switch (_countBytes)
                {
                    case < 0: throw new InvalidOperationException("PIT channel 2 count without a control word.");
                    case 0:
                        _countLow = (byte)value;
                        _countBytes = 1;
                        return;
                    default:
                        trace.SetDivisor(Time, (ushort)(_countLow | (value << 8)));
                        _countBytes = -1;
                        return;
                }
            }
            case MusicProfile.PcSpeaker when port == 0x61:
            {
                if (((value ^ _speaker) & ~3u) != 0 || (value & 3) is 1 or 2)
                {
                    throw new InvalidOperationException($"Unexpected port 61h value {value:X} after {_speaker:X}.");
                }

                _speaker = (byte)value;
                trace.SetGate(Time, (value & 3) == 3);
                return;
            }
        }

        if (OplPort(port) is { } opl)
        {
            if (!opl.Data)
            {
                _selected[port] = (byte)value;
            }
            else
            {
                trace.WriteRegister(Time, opl.Chip, (ushort)((opl.Bank * 256) + _selected[port - 1]), (byte)value);
            }

            return;
        }

        if (port == 0xb88 && profile == MusicProfile.ProAudioSpectrum && value == 128)
        {
            return; // Restore PAS single-chip routing at shutdown.
        }

        throw new InvalidOperationException($"Unexpected OUT {port:X}={value:X}.");
    }
}
