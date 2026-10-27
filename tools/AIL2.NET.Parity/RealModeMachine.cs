namespace AIL2.Parity;

/// <summary>Owns a bounded 16-bit real-mode guest for original AIL 2.x drivers.</summary>
/// <remarks>
///     Fixtures receive whole paragraphs from segment 1000h upward, below the stack segment. Calls use the far frame
///     AIL's call_driver leaves for a driver: a far return address, then the C arguments as words.
/// </remarks>
internal sealed class RealModeMachine : UnicornEngine
{
    /// <summary>The linear far-return target that ends every call; nothing is executed there.</summary>
    private const uint ReturnAddress = 0x500;

    /// <summary>The segment holding the guest call stack.</summary>
    private const ushort StackSegment = 0x9000;

    /// <summary>The next free fixture paragraph.</summary>
    private ushort _nextSegment = 0x1000;

    /// <summary>Creates a real-mode guest with one megabyte plus the high memory area mapped.</summary>
    public RealModeMachine() : base(2, 0x110000)
    {
    }

    /// <summary>Converts a real-mode segment and offset to a linear guest address.</summary>
    /// <param name="segment">The segment value.</param>
    /// <param name="offset">The offset within the segment.</param>
    /// <returns>The linear address.</returns>
    public static uint Linear(ushort segment, ushort offset)
    {
        return (segment * 16u) + offset;
    }

    /// <summary>Copies fixture bytes to the start of fresh paragraphs.</summary>
    /// <param name="bytes">The source bytes, at most one segment long.</param>
    /// <returns>The segment of the copy, whose offset is zero.</returns>
    /// <exception cref="InvalidOperationException">The fixture region below the stack is exhausted.</exception>
    public ushort Allocate(ReadOnlySpan<byte> bytes)
    {
        var segment = _nextSegment;
        var next = segment + ((bytes.Length + 15) / 16);
        if (bytes.Length > 0x10000 || next > StackSegment)
        {
            throw new InvalidOperationException("Real-mode fixture memory exhausted.");
        }

        _nextSegment = (ushort)next;
        Write(Linear(segment, 0), bytes);
        return segment;
    }

    /// <summary>Places a far procedure that fails the current call when the guest invokes it.</summary>
    /// <param name="name">The procedure named in the failure.</param>
    /// <returns>The segment of the procedure, whose offset is zero.</returns>
    public ushort Trap(string name)
    {
        var segment = Allocate([0xcb]);
        Hook(Linear(segment, 0), () => throw new InvalidOperationException($"Unexpected guest call to {name}."));
        return segment;
    }

    /// <summary>Far-calls an original procedure with a fresh stack and a bounded instruction count.</summary>
    /// <param name="segment">The code segment of the procedure.</param>
    /// <param name="offset">The procedure offset.</param>
    /// <param name="arguments">The C argument words in call order; a far pointer is its offset, then its segment.</param>
    /// <returns>The DX:AX result, with DX in the high half.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A hook fails, Unicorn reports an error, or the procedure does not return within its instruction budget.
    /// </exception>
    public uint Call(ushort segment, ushort offset, params ushort[] arguments)
    {
        var frame = new byte[4 + (arguments.Length * 2)];
        frame[2] = (byte)(ReturnAddress >> 4);
        frame[3] = (byte)(ReturnAddress >> 12);
        for (var i = 0; i < arguments.Length; i++)
        {
            frame[4 + (i * 2)] = (byte)arguments[i];
            frame[5 + (i * 2)] = (byte)(arguments[i] >> 8);
        }

        var stack = (ushort)(0xfff0 - frame.Length);
        Write(Linear(StackSegment, stack), frame);
        SetRegister(49, StackSegment);
        SetRegister(30, stack);
        SetRegister(11, segment);
        SetRegister(25, 2);
        Run(Linear(segment, offset), ReturnAddress, 20_000_000);
        if (Linear((ushort)Register(11), (ushort)Register(26)) != ReturnAddress)
        {
            throw new InvalidOperationException(
                $"Oracle instruction budget exhausted at {Register(11):X4}:{Register(26) & 0xffff:X4}.");
        }

        return ((Register(24) & 0xffff) << 16) | (Register(19) & 0xffff);
    }
}
