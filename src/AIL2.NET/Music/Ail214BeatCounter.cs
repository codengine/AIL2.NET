namespace AIL2.Music;

/// <summary>Counts beats and measures as AIL/32 1.05's XMIDI32.ASM does, for DriverRevision.Ail214.</summary>
/// <remarks>
///     Time is kept in sixteenths of a microsecond: each interval adds 133,333 scaled by the time-signature denominator,
///     and a beat lasts the tempo times 16. Queries look one interval ahead, and counting starts before measure 0. The
///     16-bit AIL 2.14 XMIDI.ASM differs in two places: its measure query reports 0 while the count is negative (65535
///     here, before the first time signature), and it compares the beat time by signed 16-bit halves instead of as
///     one 32-bit value.
/// </remarks>
internal sealed class Ail214BeatCounter : BeatCounter
{
    /// <summary>The time into the current beat, the time each interval adds, and the length of a beat.</summary>
    private int _fraction, _timeFraction, _timePerBeat;

    /// <summary>The beats per measure.</summary>
    private int _numerator;

    /// <summary>The beat within the measure and the measure, at their original width.</summary>
    private ushort _beat, _measure;

    /// <inheritdoc/>
    public override ushort Beat => Ahead().Beat;

    /// <inheritdoc/>
    public override ushort Measure => Ahead().Measure;

    /// <inheritdoc/>
    public override bool CountsBeforeEvents => false;

    /// <inheritdoc/>
    public override void Reset()
    {
        _beat = 0;
        _measure = 65535;
        _fraction = _timeFraction = 0;
        _numerator = 4;
        _timePerBeat = 8_000_000;
    }

    /// <inheritdoc/>
    public override void Clear()
    {
        _beat = _measure = 0;
        _fraction = -_timeFraction;
    }

    /// <inheritdoc/>
    public override void Advance()
    {
        _fraction = unchecked(_fraction + _timeFraction);
        if (_fraction >= _timePerBeat)
        {
            _fraction -= _timePerBeat;
            if (++_beat >= _numerator)
            {
                _beat = 0;
                _measure++;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Also starts a new measure.</remarks>
    public override void SetTimeSignature(byte numerator, byte denominator)
    {
        _numerator = numerator;
        _timeFraction = denominator < 2 ? 133333 >> (2 - denominator) : 133333 << (denominator - 2);
        _fraction = -_timeFraction;
        _beat = 0;
        _measure++;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tempo is zero.</exception>
    public override void SetTempo(int microseconds)
    {
        _timePerBeat = microseconds << 4;
        if (_timePerBeat == 0)
        {
            throw new InvalidDataException("Zero XMIDI tempo.");
        }
    }

    /// <summary>Computes the original look-ahead query without changing the counts.</summary>
    /// <returns>The beat and measure counters at their original width at the query boundary.</returns>
    private (ushort Beat, ushort Measure) Ahead()
    {
        var beat = _beat;
        var measure = _measure;
        if (unchecked(_fraction + _timeFraction) >= _timePerBeat)
        {
            if (++beat >= _numerator)
            {
                beat = 0;
                measure++;
            }
        }

        return (beat, measure);
    }
}
