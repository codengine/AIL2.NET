using AIL2.Formats;
using AIL2.Parity;
using AIL2.Rendering;
using AIL2.Samples;
using Xunit;

namespace AIL2.Tests;

/// <summary>Compares sampled-playback policies, controls and payload forwarding with original code.</summary>
public sealed class SampleTests
{
    /// <summary>Every supported original digital profile.</summary>
    public static TheoryData<DigitalProfile> Profiles => new(Enum.GetValues<DigitalProfile>());

    /// <summary>Compares marked sections containing repeats, silence and continuation blocks.</summary>
    /// <param name="profile">The original digital policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void SilenceRepeatMarkerAndContinuation(DigitalProfile profile)
    {
        var voc = Fixtures.Voc((4, [7, 0]),
            (1, [211, 0, 0, 64, 128, 255]),
            (6, [2, 0]),
            (2, [11, 22, 33]),
            (3, [9, 0, 200]),
            (7, []),
            (4, [8, 0]),
            (1, [233, 0, 100, 200]),
            (0, []));
        Assert.True(SampleScenarios.Compare(Fixtures.Root, profile, voc, 7) >= 4);
        Assert.Equal(1, SampleScenarios.Compare(Fixtures.Root, profile, voc, 8));
    }

    /// <summary>Compares extended stereo blocks across rate-quantization boundaries.</summary>
    /// <param name="profile">The digital policy; the original mono-only Sound Blaster case is excluded here.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void ExtendedStereoAndRateBoundaries(DigitalProfile profile)
    {
        if (profile == DigitalProfile.SoundBlaster)
        {
            return; // Original mono-only profile's capability is tested separately.
        }

        foreach (var rate in new byte[] { 0, 128, 165, 166, 200, 211, 233, 255 })
        {
            var data = Fixtures.Voc((8, [0, rate, 0, 1]), (1, [211, 0, 0, 255, 128, 64]), (2, [2, 4, 6, 8]), (0, []));
            Assert.Equal(2, SampleScenarios.Compare(Fixtures.Root, profile, data));
        }
    }

    /// <summary>Compares mixer requests and emitted gains at source volume/pan boundaries.</summary>
    /// <param name="profile">The original digital policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryVolumeAndPanBoundaryMatchesNativeMixer(DigitalProfile profile)
    {
        using var native = new OriginalSampleDriver(Fixtures.Root, profile);
        var capture = new Capture();
        using var session = new AILSession(digitalProfile: profile, sampleOutput: capture);
        using var sample = session.CreateSample(VocFile.Load(Fixtures.Voc((1, [211, 0, 1]), (0, []))));
        foreach (var volume in new[] { 0, 1, 63, 64, 80, 110, 127 })
        {
            foreach (var pan in new[] { 0, 1, 62, 63, 64, 65, 126, 127 })
            {
                native.Call(129, 0, (uint)volume);
                sample.SetVolume(volume);
                native.Call(131, 0, (uint)pan);
                sample.SetPan(pan);
                Assert.Equal(native.Call(130, 0), (uint)sample.Volume);
                Assert.Equal(native.Call(132, 0), (uint)sample.Pan);
                Assert.Equal(profile == DigitalProfile.SoundBlaster ? new MixerLevels(1, 1, 1) : native.Levels[^1],
                    capture.Levels[^1]);
            }
        }
    }

    /// <summary>Ensures compressed bytes remain untouched and only explicit consumer completion advances blocks.</summary>
    [Fact]
    public void CompressionIsForwardedAndCompletionBelongsToConsumer()
    {
        byte[] bytes = [211, 1, 0x80, 0x12, 0x34];
        var data = Fixtures.Voc((1, bytes), (2, "Vx"u8.ToArray()), (0, []));
        var capture = new Capture();
        using var session = new AILSession(digitalProfile: DigitalProfile.SoundBlaster, sampleOutput: capture);
        using var sample = session.CreateSample(VocFile.Load(data));
        sample.Play();
        session.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.Equal(SampleStatus.Playing, sample.Status);
        var item = Assert.Single(capture.Blocks);
        Assert.Equal(bytes[2..], item.Bytes);
        Assert.Equal(SampleEncoding.CreativeAdpcm4, capture.Blocks[0].Format.Encoding);
        sample.CompleteBlock();
        Assert.Equal("Vx"u8.ToArray(), capture.Blocks[1].Bytes);
        Assert.True(capture.Blocks[1].Continuation);
        sample.CompleteBlock();
        Assert.Equal(SampleStatus.Done, sample.Status);
    }

    /// <summary>Checks that the PCM renderer rejects compressed playback explicitly.</summary>
    [Fact]
    public void UnsupportedCompressedOutputIsExplicit()
    {
        using var renderer = new AILRenderer(null, DigitalProfile.SoundBlaster);
        using var sample = renderer.Session.CreateSample(VocFile.Load(Fixtures.Voc((1, [211, 1, 128, 42]), (0, []))));
        Assert.Throws<NotSupportedException>(() => sample.Play());
        Assert.Equal(SampleStatus.Stopped, sample.Status);
    }

    /// <summary>Compares encoded payload and continuation state at host-selected completion timestamps.</summary>
    /// <param name="profile">An original profile supporting the authored encoding.</param>
    [Theory]
    [InlineData(DigitalProfile.SoundBlaster)]
    [InlineData(DigitalProfile.SoundBlasterPro)]
    [InlineData(DigitalProfile.AdLibGold)]
    public void CompressedPayloadAndContinuationMatchOriginalAtHostCompletionBoundaries(DigitalProfile profile)
    {
        var data = Fixtures.Voc((1, [211, 1, 128, 0x12, 0x34]), (2, "Vx"u8.ToArray()), (0, []));
        using var native = new OriginalSampleDriver(Fixtures.Root, profile);
        var output = new Capture();
        using var session = new AILSession(digitalProfile: profile, sampleOutput: output);
        using var sample = session.CreateSample(VocFile.Load(data));
        native.Load(data);
        native.Start();
        sample.Play();
        for (var block = 0; block < 2; block++)
        {
            Assert.Equal(native.Status, sample.Status);
            Assert.Equal(native.Blocks[block].Bytes, output.Blocks[block].Bytes);
            Assert.Equal(native.Blocks[block].Format, output.Blocks[block].Format);
            Assert.Equal(native.Blocks[block].Continuation, output.Blocks[block].Continuation);
            // A capable external consumer defines completion time; no encoded-size-to-duration estimate or decoding.
            native.Time = TimeSpan.FromMilliseconds((block + 1) * 100);
            session.AdvanceTo(native.Time);
            native.CompleteBlock();
            sample.CompleteBlock();
        }

        Assert.Equal(SampleStatus.Done, native.Status);
        Assert.Equal(native.Status, sample.Status);
    }

    /// <summary>Checks copied resource ownership, paused timing and single-active-sample replacement.</summary>
    [Fact]
    public void SampleReplacementPauseResumeAndOwnedBytes()
    {
        var bytes = Fixtures.Voc((1, [211, 0, 1, 2, 3, 4]), (0, []));
        var file = VocFile.Load(bytes);
        Array.Fill(bytes, (byte)0);
        var capture = new Capture();
        using var session = new AILSession(digitalProfile: DigitalProfile.SoundBlaster, sampleOutput: capture);
        using var first = session.CreateSample(file);
        using var next = session.CreateSample(file);
        first.Play();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, capture.Blocks[0].Bytes);
        first.Pause();
        session.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.Equal(SampleStatus.Paused, first.Status);
        first.Resume();
        next.Play();
        Assert.Equal(SampleStatus.Stopped, first.Status);
        Assert.Equal(SampleStatus.Playing, next.Status);
        session.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.Equal(SampleStatus.Done, next.Status);
    }

    /// <summary>Compares raw-buffer playback and replacement with equivalent original VOC stop/start calls.</summary>
    /// <param name="profile">The original digital policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void RawBufferAndReplacementFollowOriginalStopStartSchedule(DigitalProfile profile)
    {
        var capture = new Capture();
        using var session = new AILSession(digitalProfile: profile, sampleOutput: capture);
        using var native = new OriginalSampleDriver(Fixtures.Root, profile);
        byte[] first = [0, 64, 128, 255], second = [10, 20, 30];
        var buffer = new SampleBuffer(first, new SampleFormat(SampleEncoding.Unsigned8, 1, 1000000, 45));
        using var sample = session.CreateSample(buffer);
        native.Load(Fixtures.Voc((1, [211, 0, .. first]), (0, [])));
        native.Start();
        sample.Play();
        Array.Fill(first, (byte)0);
        Assert.Equal(native.Blocks[0].Bytes, capture.Blocks[0].Bytes);
        Assert.Equal(native.Blocks[0].Format, sample.Format);
        native.Call(126, 0);
        native.Load(Fixtures.Voc((1, [233, 0, .. second]), (0, [])));
        native.Start();
        sample.ReplaceBuffer(new SampleBuffer(second, new SampleFormat(SampleEncoding.Unsigned8, 1, 1000000, 23)));
        Assert.Equal(native.Blocks[1].Bytes, capture.Blocks[1].Bytes);
        Assert.Equal(native.Blocks[1].Format, sample.Format);
        native.Call(127, 0);
        sample.Pause();
        Assert.Equal(native.Status, sample.Status);
        native.Call(128, 0);
        sample.Resume();
        Assert.Equal(native.Status, sample.Status);
    }

    /// <summary>Captures owned block copies and mixer updates without decoding.</summary>
    private sealed class Capture : ISampleOutput
    {
        /// <summary>Ordered output blocks with copied payloads.</summary>
        public List<OriginalSampleBlock> Blocks { get; } = [];

        /// <summary>Ordered profile-derived mixer settings.</summary>
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
