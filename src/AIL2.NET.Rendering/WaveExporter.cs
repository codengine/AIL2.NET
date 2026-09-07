using System.Buffers.Binary;

namespace AIL2.Rendering;

/// <summary>Writes renderer output to a WAVE stream for a fixed duration or until playback ends.</summary>
public static class WaveExporter
{
    /// <summary>Writes until playback ends or the caller stops it. Cancellation finalizes a playable partial file.</summary>
    /// <remarks>The seekable output uses RIFF/WAVE and automatically becomes RF64 when necessary.</remarks>
    /// <param name="destination">The writable, seekable stream, left open after the exported data.</param>
    /// <param name="renderer">The renderer to advance on the thread that created it.</param>
    /// <param name="isPlaying">A synchronous predicate checked before each frame; false ends the export.</param>
    /// <param name="stop">Requests normal completion and finalizes a partial file instead of throwing for cancellation.</param>
    /// <returns>The number of complete stereo frames written.</returns>
    /// <exception cref="ArgumentNullException">destination, renderer or isPlaying is null.</exception>
    /// <exception cref="ArgumentException">destination is not writable and seekable.</exception>
    /// <exception cref="OverflowException">The output rate or length cannot be represented in the WAVE header.</exception>
    public static long WriteToEnd(Stream destination,
        AILRenderer renderer,
        Func<bool> isPlaying,
        CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(isPlaying);
        if (!destination.CanSeek || !destination.CanWrite)
        {
            throw new ArgumentException("An open-ended WAVE export requires a writable, seekable stream.",
                nameof(destination));
        }

        var start = destination.Position;
        long frames = 0;
        destination.Write(new byte[80]);
        var samples = new short[4096];
        var bytes = new byte[samples.Length * 2];
        Span<byte> header = stackalloc byte[80];
        try
        {
            while (!stop.IsCancellationRequested && isPlaying())
            {
                var count = 0;
                while (count < samples.Length / 2 && !stop.IsCancellationRequested && isPlaying())
                {
                    renderer.Render(samples.AsSpan(count * 2, 2));
                    count++;
                }

                for (var i = 0; i < count * 2; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
                }

                destination.Write(bytes, 0, count * 4);
                frames += count;
            }
        }
        finally
        {
            var end = destination.Position;
            var size = checked((ulong)frames * 4);
            var riffSize = size + 72;
            var large = riffSize > uint.MaxValue;
            header.Clear();
            (large ? "RF64"u8 : "RIFF"u8).CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], large ? uint.MaxValue : (uint)riffSize);
            "WAVE"u8.CopyTo(header[8..]);
            (large ? "ds64"u8 : "JUNK"u8).CopyTo(header[12..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 28);
            if (large)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(header[20..], riffSize);
                BinaryPrimitives.WriteUInt64LittleEndian(header[28..], size);
                BinaryPrimitives.WriteUInt64LittleEndian(header[36..], (ulong)frames);
            }

            "fmt "u8.CopyTo(header[48..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[56..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[58..], 2);
            BinaryPrimitives.WriteInt32LittleEndian(header[60..], renderer.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[64..], checked(renderer.SampleRate * 4));
            BinaryPrimitives.WriteUInt16LittleEndian(header[68..], 4);
            BinaryPrimitives.WriteUInt16LittleEndian(header[70..], 16);
            "data"u8.CopyTo(header[72..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[76..], large ? uint.MaxValue : (uint)size);
            destination.Position = start;
            destination.Write(header);
            destination.Position = end;
        }

        return frames;
    }

    /// <summary>Writes a fixed duration of signed 16-bit stereo PCM in classic RIFF/WAVE format.</summary>
    /// <param name="destination">The writable destination, left open; seeking is unnecessary.</param>
    /// <param name="renderer">The renderer to advance on the thread that created it.</param>
    /// <param name="duration">The positive duration, rounded down to complete output frames.</param>
    /// <param name="cancellationToken">Cancels between blocks by throwing OperationCanceledException.</param>
    /// <remarks>
    ///     The full-duration header is written first; cancellation or failure can leave an incomplete file. Use
    ///     WriteToEnd for finalized partial exports or RF64. This method renders the requested duration even after playback
    ///     ends.
    /// </remarks>
    /// <exception cref="ArgumentNullException">destination or renderer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">duration is nonpositive.</exception>
    /// <exception cref="OverflowException">The duration or sample rate exceeds classic RIFF capacity.</exception>
    /// <exception cref="OperationCanceledException">cancellationToken is canceled.</exception>
    public static void Write(Stream destination,
        AILRenderer renderer,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        var frames = checked((long)((Int128)duration.Ticks * renderer.SampleRate / TimeSpan.TicksPerSecond));
        var size = checked((uint)(frames * 4));
        Span<byte> header = stackalloc byte[44];
        header.Clear();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked(size + 36));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], renderer.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], checked(renderer.SampleRate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], size);
        destination.Write(header);
        var samples = new short[4096];
        var bytes = new byte[samples.Length * 2];
        while (frames > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(frames, samples.Length / 2);
            renderer.Render(samples.AsSpan(0, count * 2));
            for (var i = 0; i < count * 2; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
            }

            destination.Write(bytes, 0, count * 4);
            frames -= count;
        }
    }
}
