using System.Globalization;
using System.Security.Cryptography;
using AIL2.Samples;

namespace AIL2.Parity;

/// <summary>A normalized output event used for ordered differential comparison.</summary>
/// <param name="Ticks">The logical timestamp in TimeSpan ticks.</param>
/// <param name="Kind">The output boundary name, such as opl, midi, speaker or sample.</param>
/// <param name="A">The first kind-specific integer: chip, encoding, operation, left gain, divisor or gate state.</param>
/// <param name="B">The second integer: register, channel count or right gain.</param>
/// <param name="C">The third integer: register value, continuation flag or gain denominator.</param>
/// <param name="Payload">Hexadecimal MIDI, decimal delay ticks, or sample rate/length/hash data.</param>
public sealed record TraceEvent(long Ticks, string Kind, int A = 0, int B = 0, int C = 0, string? Payload = null);

/// <summary>Captures Miles output. No synthesizer correctness checks exist in this harness.</summary>
public sealed class Trace : IOplOutput, IMidiOutput, ISpeakerOutput, ISampleOutput
{
    /// <summary>Gets the complete ordered event list, retained without deduplication.</summary>
    public List<TraceEvent> Events { get; } = [];

    /// <inheritdoc />
    public bool Supports(SampleFormat format)
    {
        return true;
    }

    /// <inheritdoc />
    public void WriteRegister(TimeSpan time, int chip, ushort register, byte value)
    {
        Events.Add(new TraceEvent(time.Ticks, "opl", chip, register, value));
    }

    /// <inheritdoc />
    public void SetOplLevels(TimeSpan time, MixerLevels levels)
    {
        Events.Add(new TraceEvent(time.Ticks, "opl-level", levels.Left, levels.Right, levels.Maximum));
    }

    /// <inheritdoc />
    public void SendMessage(TimeSpan time, ReadOnlySpan<byte> message)
    {
        Events.Add(new TraceEvent(time.Ticks, "midi", Payload: Convert.ToHexString(message)));
    }

    /// <inheritdoc />
    public void Delay(TimeSpan time, TimeSpan duration)
    {
        Events.Add(new TraceEvent(time.Ticks, "delay", Payload: duration.Ticks.ToString(CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    public void SetDivisor(TimeSpan time, ushort divisor)
    {
        Events.Add(new TraceEvent(time.Ticks, "speaker", divisor));
    }

    /// <inheritdoc />
    public void SetGate(TimeSpan time, bool on)
    {
        Events.Add(new TraceEvent(time.Ticks, "speaker-gate", on ? 1 : 0));
    }

    /// <inheritdoc />
    public void BeginBlock(TimeSpan time,
        SamplePlayback playback,
        ReadOnlyMemory<byte> bytes,
        SampleFormat format,
        bool continuation)
    {
        Events.Add(new TraceEvent(time.Ticks,
            "sample",
            (int)format.Encoding,
            format.Channels,
            continuation ? 1 : 0,
            $"{format.RateNumerator}/{format.RateDenominator}:{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes.Span))}"));
    }

    /// <inheritdoc />
    public void Control(TimeSpan time, SamplePlayback playback, SampleOperation operation)
    {
        Events.Add(new TraceEvent(time.Ticks, "sample-control", (int)operation));
    }

    /// <inheritdoc />
    public void SetSampleLevels(TimeSpan time, MixerLevels levels)
    {
        Events.Add(new TraceEvent(time.Ticks, "sample-level", levels.Left, levels.Right, levels.Maximum));
    }
}
