namespace AIL2.Formats;

/// <summary>An owned sample payload with its requested encoding, channel layout and frame rate.</summary>
/// <remarks>Playback applies the selected original profile's rate quantization. No compressed bytes are decoded.</remarks>
public sealed class SampleBuffer
{
    /// <summary>Gets the copied, immutable payload; no caller buffer is retained.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Gets the requested format before profile rate quantization.</summary>
    public SampleFormat Format { get; }

    /// <summary>Copies and validates a sample payload for later playback.</summary>
    /// <param name="bytes">The source bytes, which the caller may reuse after construction.</param>
    /// <param name="format">The encoding, channel count and rational frame rate.</param>
    /// <exception cref="ArgumentException">
    ///     The encoding is undefined, the channel count is outside one or two, or a rate
    ///     component is nonpositive.
    /// </exception>
    /// <exception cref="InvalidDataException">An uncompressed payload ends with an incomplete interleaved frame.</exception>
    public SampleBuffer(ReadOnlySpan<byte> bytes, SampleFormat format)
    {
        if (!Enum.IsDefined(format.Encoding) || format.Channels is < 1 or > 2 || format.RateNumerator <= 0 ||
            format.RateDenominator <= 0)
        {
            throw new ArgumentException("Invalid sample format.", nameof(format));
        }

        if (format.Encoding is SampleEncoding.Unsigned8 or SampleEncoding.Signed8
            or SampleEncoding.Signed16LittleEndian)
        {
            var frameSize = format.Channels * (format.Encoding == SampleEncoding.Signed16LittleEndian ? 2 : 1);
            if (bytes.Length % frameSize != 0)
            {
                throw new InvalidDataException("Sample buffer contains an incomplete PCM frame.");
            }
        }

        Bytes = bytes.ToArray();
        Format = format;
    }
}
