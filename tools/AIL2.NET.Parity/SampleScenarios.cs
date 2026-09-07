using AIL2.Formats;
using AIL2.Samples;

namespace AIL2.Parity;

/// <summary>Compares finite sampled playback against the original driver at logical block boundaries.</summary>
public static class SampleScenarios
{
    /// <summary>Runs identical VOC input through the original and managed sample policies.</summary>
    /// <param name="root">The workspace used to locate original drivers when no directory is supplied.</param>
    /// <param name="profile">The digital policy to compare.</param>
    /// <param name="data">The complete caller-supplied VOC resource.</param>
    /// <param name="marker">The selected VOC marker, or null for the beginning.</param>
    /// <param name="driverDirectory">An explicit original-driver directory, or null for the reference location.</param>
    /// <returns>The number of completed blocks compared.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Status, output, mixer levels or progress differ, or the finite scenario
    ///     exceeds its completion budget.
    /// </exception>
    public static int Compare(string root,
        DigitalProfile profile,
        byte[] data,
        ushort? marker = null,
        string? driverDirectory = null)
    {
        using var original = new OriginalSampleDriver(root, profile, driverDirectory);
        var output = new Capture();
        using var session = new AILSession(digitalProfile: profile, sampleOutput: output);
        using var sample = session.CreateSample(VocFile.Load(data));
        original.Load(data, marker);
        original.Start();
        sample.Play(marker);
        if (profile != DigitalProfile.SoundBlaster && original.Levels[^1] != output.Levels[^1])
        {
            throw new InvalidOperationException(
                $"Default sample mixer mismatch: {original.Levels[^1]}/{output.Levels[^1]}.");
        }

        var completed = 0;
        Int128 remainder = 0;
        SampleFormat previous = default;
        while (true)
        {
            if (original.Status != sample.Status)
            {
                throw new InvalidOperationException(
                    $"Sample status mismatch {original.Status}/{sample.Status} after block {completed}.");
            }

            if (original.Blocks.Count != output.Blocks.Count)
            {
                throw new InvalidOperationException(
                    $"Sample block count mismatch {original.Blocks.Count}/{output.Blocks.Count}.");
            }

            if (original.Status == SampleStatus.Done)
            {
                break;
            }

            if (completed >= original.Blocks.Count)
            {
                throw new InvalidOperationException("Original sample made no progress.");
            }

            var r = original.Blocks[completed];
            var m = output.Blocks[completed];
            if (r.Time != m.Time || r.Format != m.Format || r.Continuation != m.Continuation ||
                !r.Bytes.AsSpan().SequenceEqual(m.Bytes))
            {
                throw new InvalidOperationException(
                    $"Sample block {completed} mismatch: original {r.Time.Ticks}/{r.Format}/{r.Bytes.Length}/{r.Continuation}, managed {m.Time.Ticks}/{m.Format}/{m.Bytes.Length}/{m.Continuation}.");
            }

            if (r.Format != previous)
            {
                remainder = 0;
                previous = r.Format;
            }

            var total = ((Int128)r.Frames * TimeSpan.TicksPerSecond * r.Format.RateDenominator) + remainder;
            var duration = (long)(total / r.Format.RateNumerator);
            remainder = total % r.Format.RateNumerator;
            original.Time += TimeSpan.FromTicks(duration);
            original.CompleteBlock();
            session.AdvanceTo(original.Time);
            if (++completed > 100000)
            {
                throw new InvalidOperationException("Finite sample scenario exceeded its completion budget.");
            }
        }

        return completed;
    }

    /// <summary>Copies managed block payloads and mixer settings for comparison.</summary>
    private sealed class Capture : ISampleOutput
    {
        /// <summary>The ordered managed blocks with owned payload copies.</summary>
        public List<OriginalSampleBlock> Blocks { get; } = [];

        /// <summary>The ordered profile mixer updates.</summary>
        public List<MixerLevels> Levels { get; } = [];

        /// <inheritdoc />
        public bool Supports(SampleFormat format)
        {
            return true;
        }

        /// <inheritdoc />
        public void BeginBlock(TimeSpan time,
            SamplePlayback playback,
            ReadOnlyMemory<byte> bytes,
            SampleFormat format,
            bool continuation)
        {
            Blocks.Add(new OriginalSampleBlock(time, bytes.ToArray(), format, continuation, 0));
        }

        /// <inheritdoc />
        public void Control(TimeSpan time, SamplePlayback playback, SampleOperation operation)
        {
        }

        /// <inheritdoc />
        public void SetSampleLevels(TimeSpan time, MixerLevels levels)
        {
            Levels.Add(levels);
        }
    }
}
