namespace AIL2.Music;

/// <summary>Counts a sequence's beats and measures from its tempo and time signature, as one XMIDI.ASM release does.</summary>
internal abstract class BeatCounter
{
    /// <summary>Gets the beat within the measure that AIL_beat_count reports.</summary>
    public abstract ushort Beat { get; }

    /// <summary>Gets the measure that AIL_measure_count reports, with 16-bit wraparound.</summary>
    public abstract ushort Measure { get; }

    /// <summary>
    ///     Gets whether each interval is counted before its note durations and events, even when it ends the sequence,
    ///     rather than after them and only while the sequence still plays.
    /// </summary>
    public abstract bool CountsBeforeEvents { get; }

    /// <summary>Restores the defaults of a rewound sequence: 4/4 at 500,000 microseconds per beat.</summary>
    public abstract void Reset();

    /// <summary>Applies controller 118: restarts counting at beat 0 of measure 0.</summary>
    public abstract void Clear();

    /// <summary>Counts one driver service interval.</summary>
    public abstract void Advance();

    /// <summary>Applies a time-signature meta event.</summary>
    /// <param name="numerator">The beats per measure.</param>
    /// <param name="denominator">The beat unit as a power of two; 2 is a quarter note.</param>
    public abstract void SetTimeSignature(byte numerator, byte denominator);

    /// <summary>Applies a tempo meta event.</summary>
    /// <param name="microseconds">The 24-bit beat length in microseconds.</param>
    public abstract void SetTempo(int microseconds);
}
