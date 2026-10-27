using System.Buffers.Binary;
using System.Text;
using AIL2.Formats;

namespace AIL2.Parity;

/// <summary>Runs an original real-mode AIL 2.x .ADV music driver; substitutes only external machine boundaries.</summary>
/// <remarks>
///     The driver is called the way AIL 2.14's call_driver calls it: through the image's function index, with the
///     caller's far return address and C arguments on the stack. Initialization uses the description table's
///     default I/O parameters, as Star Trek 25th Anniversary does. Detection is not run; the scenario has already
///     chosen the device.
/// </remarks>
public sealed class OriginalAdvMusicDriver : IOriginalMusicDriver
{
    /// <summary>The isolated real-mode guest executing original instructions.</summary>
    private readonly RealModeMachine _machine = new();

    /// <summary>The substituted OPL, MPU and retrace hardware feeding the comparison trace.</summary>
    private readonly MusicBoundary _boundary;

    /// <summary>The segment holding the driver image at offset zero.</summary>
    private readonly ushort _driver;

    /// <summary>AIL function numbers mapped to procedure offsets in the driver segment.</summary>
    private readonly Dictionary<int, ushort> _functions = [];

    /// <summary>The guest state segment for the most recently registered sequence.</summary>
    private ushort _state;

    /// <summary>Gets the minimum API version from the driver description table.</summary>
    public int MinimumApiVersion { get; }

    /// <summary>Gets the driver type from the description table; 3 is an XMIDI driver.</summary>
    public int DriverType { get; }

    /// <summary>Gets the global timbre file suffix the driver asks for; empty when it uses none.</summary>
    public string DataSuffix { get; }

    /// <summary>Gets the requested service calls per second.</summary>
    public int ServiceRate { get; }

    /// <inheritdoc/>
    /// <remarks>
    ///     AIL_init_driver registers the driver's service at ServiceRate, and AIL_set_timer_frequency turns the rate into
    ///     whole microseconds, 1,000,000 / ServiceRate.
    /// </remarks>
    public TimeSpan ServicePeriod => TimeSpan.FromTicks(1_000_000 / ServiceRate * 10L);

    /// <summary>Gets or sets the logical timestamp assigned by the comparison schedule.</summary>
    public TimeSpan Time
    {
        get => _boundary.Time;
        set => _boundary.Time = value;
    }

    /// <summary>Gets the most recently registered guest sequence handle.</summary>
    public ushort SequenceHandle { get; private set; }

    /// <summary>Gets the sequence status reported by the driver's status function.</summary>
    /// <remarks>The function returns a word in AX; DX holds whatever the driver last left there.</remarks>
    public SequenceStatus Status => (SequenceStatus)(ushort)Call(174, 0, SequenceHandle);

    /// <summary>Gets the original sequence's zero-based EVNT offset, excluding the chunk header.</summary>
    /// <remarks>Reads the EVNT and current-event far pointers at the start of the XMIDI.ASM state table.</remarks>
    public int EventOffset => checked((int)(Pointer(12) - Pointer(8) - 8));

    /// <summary>Loads, describes and initializes an original driver at its own default I/O parameters.</summary>
    /// <param name="path">The .ADV image path.</param>
    /// <param name="profile">The profile whose port layout the driver drives.</param>
    /// <param name="trace">The comparison trace receiving ordered output.</param>
    /// <exception cref="InvalidDataException">
    ///     The image lacks the AIL driver signature or a description table, or requests no periodic service.
    /// </exception>
    public OriginalAdvMusicDriver(string path, MusicProfile profile, Trace trace)
    {
        var image = File.ReadAllBytes(path);
        // AIL_register_driver accepts only images whose text after the index word starts with "Copy".
        if (image.Length < 6 || !image.AsSpan(2, 4).SequenceEqual("Copy"u8))
        {
            throw new InvalidDataException("Expected an AIL .ADV driver image.");
        }

        for (var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(image); ; at += 4)
        {
            var function = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at));
            if (function == 0xffff)
            {
                break;
            }

            _functions.Add(function, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at + 2)));
        }

        _boundary = new MusicBoundary(profile, trace);
        _driver = _machine.Allocate(image);
        _machine.ReadPort = _boundary.ReadPort;
        _machine.WritePort = _boundary.WritePort;
        var description = Call(100, 0, 0, _machine.Trap("AIL_interrupt_divisor"));
        if (description == 0)
        {
            throw new InvalidDataException("The driver returned no description table.");
        }

        var table = _machine.Read(RealModeMachine.Linear((ushort)(description >> 16), (ushort)description), 22);
        MinimumApiVersion = BinaryPrimitives.ReadUInt16LittleEndian(table);
        DriverType = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(2));
        DataSuffix = Encoding.ASCII.GetString(table.AsSpan(4, 4)).TrimEnd('\0');
        ServiceRate = BinaryPrimitives.ReadInt16LittleEndian(table.AsSpan(20));
        // AIL_init_driver registers no service timer for -1 (digital drivers); a music comparison needs one.
        if (ServiceRate <= 0)
        {
            throw new InvalidDataException($"The driver requests no periodic service (service rate {ServiceRate}).");
        }

        Call(102,
            0,
            BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(12)),
            BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(14)),
            BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(16)),
            BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(18)));
        var cache = (ushort)Call(153, 0);
        if (cache != 0)
        {
            Call(154, 0, 0, _machine.Allocate(new byte[cache]), cache);
        }
    }

    /// <summary>Reads a far pointer from the current sequence state table as a linear address.</summary>
    /// <param name="offset">The pointer's offset in the state table.</param>
    /// <returns>The linear guest address.</returns>
    private uint Pointer(ushort offset)
    {
        var pointer = _machine.Read(RealModeMachine.Linear(_state, offset), 4);
        return RealModeMachine.Linear(BinaryPrimitives.ReadUInt16LittleEndian(pointer.AsSpan(2)),
            BinaryPrimitives.ReadUInt16LittleEndian(pointer));
    }

    /// <summary>Calls an original AIL function with word arguments and flushes any resulting pacing request.</summary>
    /// <param name="function">The AIL function number.</param>
    /// <param name="arguments">The C argument words in call order; a far pointer is its offset, then its segment.</param>
    /// <returns>The DX:AX result.</returns>
    /// <exception cref="KeyNotFoundException">The driver does not export the function.</exception>
    public uint Call(int function, params ushort[] arguments)
    {
        var result = _machine.Call(_driver, _functions[function], arguments);
        _boundary.FlushDelay();
        return result;
    }

    /// <summary>Installs every bank entry in original index order, as Star Trek 25th Anniversary does at startup.</summary>
    /// <param name="bank">The application-selected instrument bank.</param>
    public void Install(TimbreBank bank)
    {
        foreach (var key in bank.Keys)
        {
            bank.TryGet((byte)(key >> 8), (byte)key, out var bytes);
            Call(156, 0, (ushort)(key >> 8), (ushort)(key & 255), 0, _machine.Allocate(bytes.Span));
        }
    }

    /// <summary>Allocates guest state and registers a sequence; EventOffset follows the latest registration.</summary>
    /// <param name="xmidi">The complete XMIDI resource, at most one segment long.</param>
    /// <param name="index">The zero-based resource sequence index.</param>
    /// <returns>The original sequence handle, also exposed by SequenceHandle.</returns>
    /// <exception cref="InvalidOperationException">Original sequence registration fails.</exception>
    public ushort Register(byte[] xmidi, int index = 0)
    {
        _state = _machine.Allocate(new byte[(ushort)Call(150, 0)]);
        var handle = (ushort)Call(151, 0, 0, _machine.Allocate(xmidi), (ushort)index, 0, _state, 0, 0);
        if (handle == ushort.MaxValue)
        {
            throw new InvalidOperationException("Original sequence registration failed.");
        }

        SequenceHandle = handle;
        return handle;
    }

    /// <summary>Installs the bank and registers the selected sequence.</summary>
    /// <param name="xmidi">The complete XMIDI resource, at most one segment long.</param>
    /// <param name="bank">The application-selected instrument bank.</param>
    /// <param name="index">The zero-based resource sequence index.</param>
    /// <exception cref="InvalidOperationException">Original sequence registration fails.</exception>
    public void Load(byte[] xmidi, TimbreBank bank, int index = 0)
    {
        Install(bank);
        Register(xmidi, index);
    }

    /// <summary>Starts the most recently registered original sequence.</summary>
    public void Start()
    {
        Call(170, 0, SequenceHandle);
    }

    /// <summary>Stops the most recently registered original sequence.</summary>
    public void Stop()
    {
        Call(171, 0, SequenceHandle);
    }

    /// <summary>Executes one original music-driver service call at the caller-selected Time.</summary>
    public void Service()
    {
        Call(103);
    }

    /// <summary>Executes original driver shutdown, retaining the resulting output in the trace.</summary>
    public void Shutdown()
    {
        Call(104, 0, 0, 0);
    }

    /// <summary>Releases the isolated guest; invoke Shutdown separately when comparing shutdown output.</summary>
    public void Dispose()
    {
        _machine.Dispose();
    }
}
