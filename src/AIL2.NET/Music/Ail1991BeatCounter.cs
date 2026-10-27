namespace AIL2.Music;

/// <summary>Counts beats and measures as the 1991 release's XMIDI sequencer does.</summary>
/// <remarks>
///     Each interval adds its length in microseconds times 2^(denominator - 2) to a 32-bit accumulator, and a beat
///     completes when the accumulator reaches the tempo in microseconds. The driver compares the 16-bit halves signed:
///     once the high words are equal, a low word of 8000h or more counts as negative, so a beat can complete before or
///     after the accumulator reaches the tempo. At most one beat completes per interval. A time signature keeps the
///     counts, a zero tempo is accepted, and queries report the counts as they stand, starting at measure 0.
/// </remarks>
/// <param name="interval">The driver's QUANT_TIME in microseconds.</param>
internal sealed class Ail1991BeatCounter(int interval) : BeatCounter
{
    /// <summary>The accumulated time, the time each interval adds, and the tempo, as the driver's 32-bit words.</summary>
    private int _accumulator, _increment, _tempo;

    /// <summary>The beats per measure, the beat within the measure and the measure, at their original width.</summary>
    private ushort _numerator, _beat, _measure;

    /// <inheritdoc/>
    public override ushort Beat => _beat;

    /// <inheritdoc/>
    public override ushort Measure => _measure;

    /// <inheritdoc/>
    public override bool CountsBeforeEvents => true;

    /// <inheritdoc/>
    public override void Reset()
    {
        Clear();
        _numerator = 4;
        _increment = interval;
        _tempo = 500_000;
    }

    /// <inheritdoc/>
    public override void Clear()
    {
        _beat = _measure = 0;
        _accumulator = 0;
    }

    /// <inheritdoc/>
    public override void Advance()
    {
        var sum = unchecked(_accumulator + _increment);
        var high = (short)(sum >> 16) - (short)(_tempo >> 16);
        if (high > 0 || (high == 0 && (short)sum >= (short)_tempo))
        {
            sum -= _tempo;
            if (++_beat >= _numerator)
            {
                _beat = 0;
                _measure++;
            }
        }

        _accumulator = sum;
    }

    /// <inheritdoc/>
    /// <remarks>
    ///     The driver shifts in 16 bits, so denominators below 2 add nothing. C# masks the shift count to 5 bits, as a
    ///     286 or later CPU does.
    /// </remarks>
    public override void SetTimeSignature(byte numerator, byte denominator)
    {
        _numerator = numerator;
        _increment = (ushort)(1 << (denominator - 2)) * interval;
    }

    /// <inheritdoc/>
    public override void SetTempo(int microseconds)
    {
        _tempo = microseconds;
    }
}
