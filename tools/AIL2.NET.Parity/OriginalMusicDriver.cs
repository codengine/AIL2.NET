using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using AIL2.Formats;

namespace AIL2.Parity;

/// <summary>Runs original REL105 musical algorithms; substitutes only external machine boundaries.</summary>
public sealed class OriginalMusicDriver : IDisposable
{
    /// <summary>The copied and relocated original LX module.</summary>
    private readonly LxImage _image;

    /// <summary>The isolated guest executing original instructions.</summary>
    private readonly UnicornMachine _machine;

    /// <summary>The ordered output comparison trace.</summary>
    private readonly Trace _trace;

    /// <summary>The original driver profile used to interpret external boundaries.</summary>
    private readonly MusicProfile _profile;

    /// <summary>The last address selected on each guest OPL address port.</summary>
    private readonly Dictionary<uint, byte> _selected = [];

    /// <summary>Guest MIDI bytes awaiting a complete message.</summary>
    private readonly List<byte> _message = [];

    /// <summary>The alternating retrace response used only for original MT-32 wait loops.</summary>
    private bool _retrace;

    /// <summary>Pending wait ticks and the paced MIDI output timestamp.</summary>
    private long _pendingDelay, _outputTicks;

    /// <summary>The guest state address for the most recently registered sequence.</summary>
    private uint _state;

    /// <summary>Gets whether the original MT-32 pop advanced the stack by a width other than four bytes.</summary>
    public bool Mt32StackImbalanceObserved { get; private set; }

    /// <summary>Gets the original sequence's zero-based EVNT offset, excluding the chunk header.</summary>
    public int EventOffset => checked((int)(_machine.ReadUInt32(_state + 12) - _machine.ReadUInt32(_state + 8) - 8));

    /// <summary>Gets or sets the logical timestamp assigned by the comparison schedule.</summary>
    public TimeSpan Time { get; set; }

    /// <summary>Gets the most recently registered guest sequence handle.</summary>
    public uint SequenceHandle { get; private set; }

    /// <summary>Maps a profile to its supplied REL105 WR module filename.</summary>
    /// <param name="profile">The original music policy.</param>
    /// <returns>The original driver basename.</returns>
    /// <exception cref="ArgumentOutOfRangeException">profile is undefined.</exception>
    public static string FileName(MusicProfile profile)
    {
        return profile switch
        {
            MusicProfile.AdLib => "A32ADLIB.DLL", MusicProfile.SoundBlaster => "A32SBFM.DLL",
            MusicProfile.SoundBlasterPro1 => "A32SP1FM.DLL",
            MusicProfile.SoundBlasterPro2 => "A32SP2FM.DLL", MusicProfile.ProAudioSpectrum => "A32PASFM.DLL",
            MusicProfile.ProAudioSpectrumOpl3 => "A32PASOP.DLL",
            MusicProfile.AdLibGold => "A32ALGFM.DLL", MusicProfile.Mt32 => "A32MT32.DLL",
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }

    /// <summary>Loads and initializes an isolated original music driver.</summary>
    /// <param name="root">The workspace containing the default original reference directory.</param>
    /// <param name="profile">The original music policy to execute.</param>
    /// <param name="trace">The comparison trace receiving ordered output.</param>
    /// <param name="repairMt32Stack">Whether to apply the documented, authorized POP EAX; NOP oracle variant.</param>
    /// <param name="driverDirectory">An explicit original-driver directory, or null for the reference default.</param>
    /// <exception cref="InvalidDataException">
    ///     The requested MT-32 repair signature does not match the supplied original
    ///     binary.
    /// </exception>
    public OriginalMusicDriver(string root,
        MusicProfile profile,
        Trace trace,
        bool repairMt32Stack = true,
        string? driverDirectory = null)
    {
        _profile = profile;
        _trace = trace;
        _image = new LxImage(File.ReadAllBytes(
            Path.Combine(driverDirectory ?? Path.Combine(root, "reference/REL105/RELEASE.105/WR"), FileName(profile))));
        _machine = new UnicornMachine(_image);
        if (profile == MusicProfile.Mt32)
        {
            uint beforePop = 0;
            _machine.Hook(LxImage.BaseAddress + 0x989, () => beforePop = _machine.Register(30));
            _machine.Hook(LxImage.BaseAddress + 0x98b,
                () => Mt32StackImbalanceObserved |= _machine.Register(30) - beforePop != 4);
        }

        if (profile == MusicProfile.Mt32 && repairMt32Stack)
        {
            // User-authorized oracle variant only. MT3232.INC:381 has POP AX after PUSH EAX.
            // Preserve instruction footprint and every musical instruction: POP EAX; NOP.
            const uint address = LxImage.BaseAddress + 0x989;
            if (!_machine.Read(address, 2).AsSpan().SequenceEqual("fX"u8))
            {
                throw new InvalidDataException(
                    "MT-32 stack repair signature does not match the supplied REL105 WR binary.");
            }

            _machine.Write(address, [0x58, 0x90]);
        }

        _machine.ReadPort = ReadPort;
        _machine.WritePort = WritePort;
        _machine.Interrupt = Interrupt;
        var io = profile switch
        {
            MusicProfile.Mt32 => 0x330u,
            MusicProfile.SoundBlaster or MusicProfile.SoundBlasterPro1 or MusicProfile.SoundBlasterPro2 => 0x220u,
            _ => 0x388u
        };
        Call(102, 0, io, 0, 0, 0);
        var cache = Call(153, 0);
        if (cache != 0)
        {
            Call(154, 0, _machine.Allocate((int)cache), cache);
        }
    }

    /// <summary>Supplies only the documented external status responses required by this music driver.</summary>
    /// <param name="port">The guest input port.</param>
    /// <param name="size">The access width in bytes.</param>
    /// <returns>The modeled status byte.</returns>
    /// <exception cref="InvalidOperationException">The access width or port is outside the documented oracle boundaries.</exception>
    private uint ReadPort(uint port, int size)
    {
        if (size != 1)
        {
            throw new InvalidOperationException($"Unexpected input width {size}.");
        }

        if (port == 0x3da && _profile == MusicProfile.Mt32)
        {
            _retrace = !_retrace;
            if (!_retrace)
            {
                _pendingDelay += 140_000;
            }

            return _retrace ? 8u : 0u;
        }

        if (_profile == MusicProfile.Mt32 && port is 0x330 or 0x331)
        {
            return port == 0x330 ? 0xfeu : 0u;
        }

        return OplPort(port) is { Data: false } ? (uint)0 : throw new InvalidOperationException($"Unexpected IN {port:X}.");
    }

    /// <summary>Translates profile-specific guest OPL ports to independent chip/register-bank identities.</summary>
    /// <param name="port">The guest port address.</param>
    /// <returns>The chip, register bank and data/address flag, or null for a non-OPL port.</returns>
    private (int Chip, int Bank, bool Data)? OplPort(uint port)
    {
        uint first = _profile switch
        {
            MusicProfile.SoundBlaster => 0x228,
            MusicProfile.SoundBlasterPro1 or MusicProfile.SoundBlasterPro2 => 0x220, _ => 0x388
        };
        if (_profile == MusicProfile.Mt32)
        {
            return null;
        }

        if (port < first || port > first + (_profile.IsOpl3() || _profile.IsDualOpl2() ? 3 : 1))
        {
            return null;
        }

        var bank = (int)((port - first) / 2);
        return (_profile.IsDualOpl2() ? bank : 0, _profile.IsOpl3() ? bank : 0, (port & 1) != 0);
    }

    /// <summary>Emits accumulated original MT-32 waits as one logical delay boundary.</summary>
    private void FlushDelay()
    {
        if (_pendingDelay == 0)
        {
            return;
        }

        _outputTicks = Math.Max(_outputTicks, Time.Ticks);
        _trace.Delay(TimeSpan.FromTicks(_outputTicks), TimeSpan.FromTicks(_pendingDelay));
        _outputTicks += _pendingDelay;
        _pendingDelay = 0;
    }

    /// <summary>Captures original OPL/MIDI output and permits only documented transport commands.</summary>
    /// <param name="port">The guest output port.</param>
    /// <param name="size">The access width in bytes.</param>
    /// <param name="value">The guest output value.</param>
    /// <exception cref="InvalidOperationException">The width, command or port is outside the documented oracle boundaries.</exception>
    private void WritePort(uint port, int size, uint value)
    {
        if (size != 1)
        {
            throw new InvalidOperationException($"Unexpected output width {size}.");
        }

        switch (_profile)
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
                    _trace.SendMessage(TimeSpan.FromTicks(Math.Max(_outputTicks, Time.Ticks)),
                        CollectionsMarshal.AsSpan(_message));
                    _message.Clear();
                }

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
                _trace.WriteRegister(Time, opl.Chip, (ushort)((opl.Bank * 256) + _selected[port - 1]), (byte)value);
            }

            return;
        }

        if (port == 0xb88 && _profile == MusicProfile.ProAudioSpectrum && value == 128)
        {
            return; // Restore PAS single-chip routing at shutdown.
        }

        throw new InvalidOperationException($"Unexpected OUT {port:X}={value:X}.");
    }

    /// <summary>Substitutes the documented PAS service and rejects other guest interrupts.</summary>
    /// <param name="number">The guest interrupt number.</param>
    /// <exception cref="InvalidOperationException">The interrupt or PAS operation is unsupported.</exception>
    private void Interrupt(uint number)
    {
        if (number == 0x31 && _machine.Register(19) == 0x300 && (_machine.Register(21) & 255) == 0x2f)
        {
            var address = _machine.Register(23);
            var registers = _machine.Read(address, 50);
            var operation = BinaryPrimitives.ReadUInt16LittleEndian(registers.AsSpan(28));
            if (operation != 0xbc07)
            {
                throw new InvalidOperationException($"Unexpected PAS INT 2F operation {operation:X}.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(registers.AsSpan(16), 0);
            _machine.Write(address, registers);
            return;
        }

        throw new InvalidOperationException($"Unexpected interrupt {number:X}, EAX={_machine.Register(19):X}.");
    }

    /// <summary>Calls an original AIL function and flushes any resulting pacing request.</summary>
    /// <param name="function">The original exported AIL function number.</param>
    /// <param name="arguments">The original 32-bit argument words in call order.</param>
    /// <returns>The guest EAX return value.</returns>
    public uint Call(int function, params uint[] arguments)
    {
        var result = _machine.Call(_image.Functions[function], arguments);
        FlushDelay();
        return result;
    }

    /// <summary>Copies a timbre into guest memory and invokes original installation.</summary>
    /// <param name="bank">The original bank byte.</param>
    /// <param name="patch">The original patch byte.</param>
    /// <param name="bytes">The length-prefixed timbre definition.</param>
    public void Install(byte bank, byte patch, ReadOnlySpan<byte> bytes)
    {
        Call(156, 0, bank, patch, _machine.Allocate(bytes));
    }

    /// <summary>Invokes the original display function with null-terminated ASCII text.</summary>
    /// <param name="text">The display text copied into guest memory.</param>
    public void WriteDisplay(string text)
    {
        Call(188, 0, _machine.Allocate(Encoding.ASCII.GetBytes(text + '\0')));
    }

    /// <summary>Queries original timbre availability.</summary>
    /// <param name="bank">The original bank byte.</param>
    /// <param name="patch">The original patch byte.</param>
    /// <returns>True when the original driver reports the timbre available.</returns>
    public bool HasTimbre(byte bank, byte patch)
    {
        return Call(159, 0, bank, patch) != 0;
    }

    /// <summary>Installs a guest trigger callback and optional original playback-control action.</summary>
    /// <param name="observe">Receives the guest sequence handle and trigger value synchronously.</param>
    /// <param name="branch">An optional marker to branch to inside the guest callback.</param>
    /// <param name="stop">Whether to invoke original stop inside the callback; takes precedence over branching.</param>
    public void SetCallback(Action<uint, byte> observe, int? branch = null, bool stop = false)
    {
        var body = new List<byte> { 0x55, 0x8b, 0xec };
        if (branch is { } marker)
        {
            body.Add(0x68);
            body.AddRange(BitConverter.GetBytes(marker));
        }

        if (branch is not null || stop)
        {
            body.AddRange([0xff, 0x75, 8, 0x6a, 0, 0xb8]);
            body.AddRange(BitConverter.GetBytes(_image.Functions[stop ? 171 : 181]));
            body.AddRange([0xff, 0xd0, 0x83, 0xc4, (byte)(stop ? 8 : 12)]);
        }

        body.AddRange([0xc9, 0xc3]);
        var address = _machine.Allocate(CollectionsMarshal.AsSpan(body));
        _machine.Hook(address, () => observe(_machine.Argument(0), (byte)_machine.Argument(1)));
        Call(189, 0, address);
    }

    /// <summary>Allocates guest state and registers a sequence without preparing its timbres.</summary>
    /// <param name="xmidi">The complete resource copied into guest memory.</param>
    /// <param name="index">The zero-based resource sequence index.</param>
    /// <param name="indirect">Optional controller lookup bytes copied into guest memory.</param>
    /// <returns>The original sequence handle, also exposed by SequenceHandle.</returns>
    /// <exception cref="InvalidOperationException">Original sequence registration fails.</exception>
    public uint Register(byte[] xmidi, int index = 0, byte[]? indirect = null)
    {
        _state = _machine.Allocate((int)Call(150, 0));
        SequenceHandle = Call(151,
            0,
            _machine.Allocate(xmidi),
            (uint)index,
            _state,
            indirect is null ? 0 : _machine.Allocate(indirect));
        return SequenceHandle == uint.MaxValue ? throw new InvalidOperationException("Original sequence registration failed.") : SequenceHandle;
    }

    /// <summary>Preloads bank entries in original order and registers the selected sequence.</summary>
    /// <param name="xmidi">The complete XMIDI resource.</param>
    /// <param name="bank">The application-selected instrument bank.</param>
    /// <param name="index">The zero-based resource sequence index.</param>
    public void Load(byte[] xmidi, TimbreBank bank, int index = 0)
    {
        foreach (var key in bank.Keys)
        {
            bank.TryGet((byte)(key >> 8), (byte)key, out var bytes);
            Call(156, 0, (uint)(key >> 8), (uint)(key & 255), _machine.Allocate(bytes.Span));
        }

        Register(xmidi, index);
    }

    /// <summary>Starts the most recently registered original sequence.</summary>
    public void Start()
    {
        Call(170, 0, SequenceHandle);
    }

    /// <summary>Gets status directly from the most recently registered guest sequence state.</summary>
    public SequenceStatus Status =>
        (SequenceStatus)BinaryPrimitives.ReadUInt16LittleEndian(_machine.Read(_state + 30, 2));

    /// <summary>Executes one original music-driver service call at the caller-selected Time.</summary>
    public void Service()
    {
        Call(103);
    }

    /// <summary>Executes original driver shutdown, retaining the resulting output in the trace.</summary>
    public void Shutdown()
    {
        Call(104, 0, 0);
    }

    /// <summary>Releases the isolated guest; invoke Shutdown separately when comparing shutdown output.</summary>
    public void Dispose()
    {
        _machine.Dispose();
    }
}
