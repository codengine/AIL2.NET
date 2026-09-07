using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AIL2.Parity;

/// <summary>A complete transfer observed at the original sample driver's device boundary.</summary>
/// <param name="Time">The logical block start timestamp.</param>
/// <param name="Bytes">The copied payload, or an empty array for silence.</param>
/// <param name="Format">The format derived from original emitted device settings.</param>
/// <param name="Continuation">Whether this is a VOC continuation block.</param>
/// <param name="Frames">The PCM or silence frame count; compressed consumers supply completion separately.</param>
public sealed record OriginalSampleBlock(
    TimeSpan Time,
    byte[] Bytes,
    SampleFormat Format,
    bool Continuation,
    long Frames);

/// <summary>Executes DMASND32's VOC/control procedures; captures whole transfers at the device boundary.</summary>
public sealed class OriginalSampleDriver : IDisposable
{
    /// <summary>The loaded and relocated original module.</summary>
    private readonly LxImage _image;

    /// <summary>The isolated original-code guest.</summary>
    private readonly UnicornMachine _machine;

    /// <summary>The profile whose external device commands are observed.</summary>
    private readonly DigitalProfile _profile;

    /// <summary>Located original procedures and guest block/packing state addresses.</summary>
    private readonly uint _processBlock, _nextBlock, _packPointer;

    /// <summary>Values retained for modeled guest device registers.</summary>
    private readonly Dictionary<uint, byte> _ports = [];

    /// <summary>AdLib Gold MMA values indexed by channel and register.</summary>
    private readonly Dictionary<(int, int), byte> _mma = [];

    /// <summary>Decoded rate, channel, command-assembly and mixer/register-selection state.</summary>
    private int _tc,
        _channels = 1,
        _dspCommand,
        _dspCount,
        _dspValue,
        _pasDivisor,
        _pasBytes,
        _selectedMixer,
        _selectedControl,
        _selectedMma;

    /// <summary>Alternating response for original retrace wait loops.</summary>
    private bool _retrace;

    /// <summary>The latest original stereo mixer numerators.</summary>
    private int _left, _right;

    /// <summary>Gets complete transfers in original emission order.</summary>
    public List<OriginalSampleBlock> Blocks { get; } = [];

    /// <summary>Gets decoded source mixer settings in emission order.</summary>
    public List<MixerLevels> Levels { get; } = [];

    /// <summary>Gets or sets the logical timestamp selected by the comparison schedule.</summary>
    public TimeSpan Time { get; set; }

    /// <summary>Gets the sample status through the original driver query.</summary>
    public SampleStatus Status => (SampleStatus)Call(124, 0);

    /// <summary>Maps a digital policy to its original REL105 WR module.</summary>
    /// <param name="profile">The original sample policy.</param>
    /// <returns>The original driver basename.</returns>
    /// <exception cref="ArgumentOutOfRangeException">profile is undefined.</exception>
    public static string FileName(DigitalProfile profile)
    {
        return profile switch
        {
            DigitalProfile.SoundBlaster => "A32SBDG.DLL", DigitalProfile.SoundBlasterPro => "A32SBPDG.DLL",
            DigitalProfile.ProAudioSpectrum => "A32PASDG.DLL", DigitalProfile.AdLibGold => "A32ALGDG.DLL",
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }

    /// <summary>Loads an isolated original sample driver and hooks documented external boundaries.</summary>
    /// <param name="root">The workspace containing the default original reference location.</param>
    /// <param name="profile">The original digital policy to execute.</param>
    /// <param name="driverDirectory">An explicit original-driver directory, or null for the default.</param>
    /// <exception cref="InvalidDataException">An original procedure signature is absent, ambiguous or unsupported.</exception>
    public OriginalSampleDriver(string root, DigitalProfile profile, string? driverDirectory = null)
    {
        _profile = profile;
        _image = new LxImage(File.ReadAllBytes(
            Path.Combine(driverDirectory ?? Path.Combine(root, "reference/REL105/RELEASE.105/WR"), FileName(profile))));
        _machine = new UnicornMachine(_image);
        // Each signature is a source procedure prologue/body. Ambiguous or absent signatures fail closed.
        _processBlock = Find("53 56 57 06 E8 ?? ?? ?? ?? 83 F8 00");
        _nextBlock = Find("53 56 57 06 C4 35 ?? ?? ?? ?? 26 8B 46 01 25 FF FF FF 00 83 C0 04");
        var blockPointer = _machine.ReadUInt32(_nextBlock + 6);
        _packPointer = _machine.ReadUInt32(Find("A3 ?? ?? ?? ?? A3 ?? ?? ?? ?? 83 25 ?? ?? ?? ?? 7F 25 80 00 00 00",
            _processBlock) + 1);
        var transfer = Find("55 8B EC 53 56 57 8B 45 08 A3 ?? ?? ?? ?? 8B 45 0C A3 ?? ?? ?? ?? E8");
        var rate = Find("55 8B EC 83 C4 FC 53 56 57 9C FA 8B 45 08 25 FF 00 00 00 BB 00 01 00 00 2B D8");
        _machine.Hook(_image.Functions[101],
            () => _machine.Return(1)); // Source detect_device: host capability already selected.
        _machine.Hook(OnlyCall(_image.Functions[127], _image.Functions[128]), () => _machine.Return()); // halt_DMA.
        _machine.Hook(OnlyCall(_image.Functions[128], _image.Functions[124]), () => _machine.Return()); // continue_DMA.
        _machine.Hook(rate,
            () =>
            {
                _tc = (int)(_machine.Argument(0) & 255);
                _channels = _machine.Argument(1) == 0 ? 1 : 2;
            });
        _machine.Hook(transfer,
            () =>
            {
                var pointer = _machine.Argument(0);
                var length = checked((int)_machine.Argument(1));
                var bytes = _machine.Read(((pointer >> 16) << 4) + (pointer & 65535), length);
                var type = _machine.Read(_machine.ReadUInt32(blockPointer), 1)[0];
                var format = Format();
                var frames = length /
                             (format.Channels * (format.Encoding == SampleEncoding.Signed16LittleEndian ? 2 : 1));
                Blocks.Add(new OriginalSampleBlock(Time, bytes, format, type == 2, frames));
                _machine.Return();
            });
        _machine.ReadPort = ReadPort;
        _machine.WritePort = WritePort;
        _machine.Interrupt = Interrupt;
        Call(102,
            0,
            profile is DigitalProfile.SoundBlaster or DigitalProfile.SoundBlasterPro ? 0x220u : 0x388u,
            5,
            1,
            0);
    }

    /// <summary>Locates an unambiguous original-source instruction signature.</summary>
    /// <param name="pattern">Space-separated hexadecimal bytes, with ?? for wildcards.</param>
    /// <param name="procedure">The guest procedure start, or zero to search the entire image.</param>
    /// <returns>The unique matching guest address.</returns>
    /// <exception cref="InvalidDataException">The signature is absent or has multiple matches.</exception>
    private uint Find(string pattern, uint procedure = 0)
    {
        var bytes = pattern.Split(' ').Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
        var found = -1;
        var start = procedure == 0 ? 0 : checked((int)(procedure - LxImage.BaseAddress));
        var end = procedure == 0
            ? _image.Bytes.Length
            : start + _image.Bytes.AsSpan(start).IndexOf(new byte[] { 7, 0x5f, 0x5e, 0x5b, 0xc3 });
        for (var i = start; i <= end - bytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (bytes[j] is { } b && b != _image.Bytes[i + j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            if (found >= 0)
            {
                throw new InvalidDataException($"Ambiguous source signature: {pattern}");
            }

            found = i;
        }

        if (found < 0)
        {
            throw new InvalidDataException($"Missing source signature: {pattern}");
        }

        return LxImage.BaseAddress + (uint)found;
    }

    /// <summary>Locates the single internal call in an original pause/resume wrapper.</summary>
    /// <param name="start">The first guest instruction address.</param>
    /// <param name="end">The exclusive procedure boundary.</param>
    /// <returns>The sole internal call target.</returns>
    /// <exception cref="InvalidDataException">The procedure does not contain exactly one supported internal call.</exception>
    private uint OnlyCall(uint start, uint end)
    {
        var bytes = _machine.Read(start, checked((int)(end - start)));
        var calls = new List<uint>();
        for (var i = 0; i < bytes.Length - 4; i++)
        {
            if (bytes[i] == 0xe8)
            {
                var target = (uint)(start + i + 5 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 1)));
                if (target >= LxImage.BaseAddress && target < LxImage.BaseAddress + _image.Bytes.Length)
                {
                    calls.Add(target);
                }
            }
        }

        return calls.Count == 1
            ? calls[0]
            : throw new InvalidDataException("Expected exactly one hardware-boundary call in sample pause/resume.");
    }

    /// <summary>Interprets settings emitted by original rate-selection code.</summary>
    /// <returns>The encoding, channel count and exact rational frame rate.</returns>
    /// <exception cref="InvalidDataException">The original driver emits an unknown packing code.</exception>
    private SampleFormat Format()
    {
        var packing = (byte)_machine.ReadUInt32(_packPointer);
        var encoding = (packing & 127) switch
        {
            0 => SampleEncoding.Unsigned8, 1 => SampleEncoding.CreativeAdpcm4, 2 => SampleEncoding.CreativeAdpcm26,
            3 => SampleEncoding.CreativeAdpcm2, 4 => SampleEncoding.Signed16LittleEndian,
            _ => throw new InvalidDataException("Unknown original packing command.")
        };
        switch (_profile)
        {
            // Interpret emitted hardware settings; selection arithmetic ran in original code.
            case DigitalProfile.ProAudioSpectrum:
                return new SampleFormat(encoding, _channels, 1_193_180, _pasDivisor * _channels);
            case DigitalProfile.AdLibGold:
            {
                var index = (_mma[(0, 9)] >> 3) & 3;
                int[] rates = encoding == SampleEncoding.CreativeAdpcm4
                    ? [22050, 11025, 7350, 5513]
                    : [44100, 22050, 11025, 7350];
                return new SampleFormat(encoding, _channels, rates[index]);
            }
            default:
                return new SampleFormat(encoding, _channels, 1_000_000, (256 - _tc) * _channels);
        }
    }

    /// <summary>Supplies documented external device status to the guest.</summary>
    /// <param name="port">The guest input port.</param>
    /// <param name="width">The access width in bytes.</param>
    /// <returns>The modeled status byte.</returns>
    /// <exception cref="InvalidOperationException">The port or width is outside the documented boundaries.</exception>
    private uint ReadPort(uint port, int width)
    {
        if (width != 1)
        {
            throw new InvalidOperationException("Unexpected digital input width.");
        }

        switch (port)
        {
            case 0x3da:
                _retrace = !_retrace;
                return _retrace ? 8u : 0u;
            case 0x21 or 0xa1:
                return _ports.GetValueOrDefault(port, (byte)255);
        }

        return _profile switch
        {
            DigitalProfile.SoundBlaster or DigitalProfile.SoundBlasterPro => port switch
            {
                0x22c => 0,
                0x22e => 128,
                0x22a => 170,
                _ => throw new InvalidOperationException($"Unexpected sample IN {port:X}.")
            },
            DigitalProfile.ProAudioSpectrum when port is 0xb89 or 0xb8b => _ports.GetValueOrDefault(port),
            DigitalProfile.AdLibGold when port == 0x38b => _selectedControl == 0x13 ? 0x11u : 0u,
            DigitalProfile.AdLibGold when port == 0x38a => 0,
            _ => throw new InvalidOperationException($"Unexpected sample IN {port:X}.")
        };
    }

    /// <summary>Captures device commands needed to recover source rate, silence and mixer output.</summary>
    /// <param name="port">The guest output port.</param>
    /// <param name="width">The access width in bytes.</param>
    /// <param name="value">The emitted command or data byte.</param>
    /// <exception cref="InvalidOperationException">The port, width or command is outside the documented boundaries.</exception>
    private void WritePort(uint port, int width, uint value)
    {
        if (width != 1)
        {
            throw new InvalidOperationException("Unexpected digital output width.");
        }

        var b = (byte)value;
        if (port is 0x21 or 0xa1 or 0x0a)
        {
            _ports[port] = b;
            return;
        }

        switch (_profile)
        {
            case DigitalProfile.SoundBlaster or DigitalProfile.SoundBlasterPro:
            {
                switch (port)
                {
                    case 0x226:
                        return;
                    case 0x224:
                        _selectedMixer = b;
                        return;
                    case 0x225 when _selectedMixer == 4:
                        Levels.Add(new MixerLevels(b >> 4, b & 15, 15));
                        return;
                    case 0x22c:
                    {
                        switch (_dspCommand)
                        {
                            case 0x40:
                                _tc = b;
                                _dspCommand = 0;
                                return;
                            case 0x80:
                            {
                                _dspValue |= b << (_dspCount++ * 8);
                                if (_dspCount == 2)
                                {
                                    Blocks.Add(new OriginalSampleBlock(Time, [], Format(), false, _dspValue + 1));
                                    _dspCommand = 0;
                                }

                                return;
                            }
                        }

                        switch (b)
                        {
                            case 0x40 or 0x80:
                                _dspCommand = b;
                                _dspCount = _dspValue = 0;
                                return;
                            case 0xd1 or 0xd3 or 0xd0 or 0xd4:
                                return;
                        }

                        break;
                    }
                }

                break;
            }
            case DigitalProfile.ProAudioSpectrum:
            {
                switch (port)
                {
                    case 0x1388:
                    {
                        if (_pasBytes++ % 2 == 0)
                        {
                            _pasDivisor = b;
                        }
                        else
                        {
                            _pasDivisor |= b << 8;
                        }

                        return;
                    }
                    case 0xf88 or 0xf8a or 0xb89 or 0xb8b or 0xb8a or 0x138b:
                        _ports[port] = b;
                        return;
                }

                break;
            }
            case DigitalProfile.AdLibGold:
            {
                switch (port)
                {
                    case 0x38a:
                        _selectedControl = b;
                        return;
                    case 0x38b:
                        return;
                    case 0x38c:
                        _selectedMma = b;
                        return;
                    case 0x38d or 0x38f:
                    {
                        var channel = port == 0x38d ? 0 : 1;
                        _mma[(channel, _selectedMma)] = b;
                        if (_selectedMma == 10)
                        {
                            if (channel == 0)
                            {
                                _right = b;
                            }
                            else
                            {
                                _left = b;
                                Levels.Add(new MixerLevels(_left, _right, 252));
                            }
                        }

                        return;
                    }
                }

                break;
            }
        }

        throw new InvalidOperationException($"Unexpected sample OUT {port:X}={value:X}.");
    }

    /// <summary>Substitutes documented guest services and captures original PAS mixer text.</summary>
    /// <param name="number">The guest interrupt number.</param>
    /// <exception cref="InvalidOperationException">The interrupt or service operation is unsupported.</exception>
    private void Interrupt(uint number)
    {
        var eax = _machine.Register(19);
        switch (number)
        {
            case 0x21:
                switch ((eax >> 8) & 255)
                {
                    case 0x35:
                        _machine.SetRegister(28, 16);
                        _machine.SetRegister(21, 0);
                        return;
                    case 0x25: return;
                    case 0x3d when _profile == DigitalProfile.ProAudioSpectrum:
                        _machine.SetRegister(19, 7);
                        _machine.SetRegister(25, _machine.Register(25) & ~1u);
                        return;
                    case 0x3e when _profile == DigitalProfile.ProAudioSpectrum: return;
                    case 0x40 when _profile == DigitalProfile.ProAudioSpectrum:
                        var command =
                            Encoding.ASCII.GetString(_machine.Read(_machine.Register(24), (int)_machine.Register(22)));
                        var digits = new string(command.Where(char.IsDigit).ToArray());
                        var level = int.Parse(digits, CultureInfo.InvariantCulture);
                        if (command.Contains("RIGHT", StringComparison.Ordinal))
                        {
                            _right = level;
                        }
                        else
                        {
                            _left = level;
                            Levels.Add(new MixerLevels(_left, _right, 100));
                        }

                        _machine.SetRegister(19, _machine.Register(22));
                        return;
                }

                break;
            case 0x31 when eax == 0x300 && (_machine.Register(21) & 255) == 0x2f &&
                           _profile == DigitalProfile.ProAudioSpectrum:
            {
                var address = _machine.Register(23);
                var registers = _machine.Read(address, 50);
                var operation = BinaryPrimitives.ReadUInt16LittleEndian(registers.AsSpan(28));
                if (operation is not (0xbc04 or 0xbc07))
                {
                    throw new InvalidOperationException($"Unexpected sample INT2F {operation:X}.");
                }

                BinaryPrimitives.WriteUInt32LittleEndian(registers.AsSpan(16), operation == 0xbc04 ? 1u : 0u);
                BinaryPrimitives.WriteUInt32LittleEndian(registers.AsSpan(24), operation == 0xbc04 ? 5u : 0u);
                _machine.Write(address, registers);
                return;
            }
        }

        throw new InvalidOperationException($"Unexpected sample interrupt {number:X}, EAX={eax:X}.");
    }

    /// <summary>Invokes an original AIL sample function by export number.</summary>
    /// <param name="function">The original AIL function number.</param>
    /// <param name="arguments">The original 32-bit argument words in call order.</param>
    /// <returns>The guest EAX result.</returns>
    public uint Call(int function, params uint[] arguments)
    {
        return _machine.Call(_image.Functions[function], arguments);
    }

    /// <summary>Copies a VOC into guest memory and selects its playback section.</summary>
    /// <param name="voc">The complete caller-supplied VOC resource.</param>
    /// <param name="marker">The selected marker, or null for the beginning.</param>
    public void Load(byte[] voc, ushort? marker = null)
    {
        var address = _machine.AllocateLow(voc);
        Call(123, 0, address, 16, ((address >> 4) << 16) | (address & 15), marker ?? uint.MaxValue);
    }

    /// <summary>Starts the original sample at the selected logical Time.</summary>
    public void Start()
    {
        Call(125, 0);
    }

    /// <summary>Executes the original next-block and process-block procedures at the caller-selected Time.</summary>
    public void CompleteBlock()
    {
        _machine.Call(_nextBlock);
        _machine.Call(_processBlock);
    }

    /// <summary>Releases the isolated guest and its retained fixture memory.</summary>
    public void Dispose()
    {
        _machine.Dispose();
    }
}
