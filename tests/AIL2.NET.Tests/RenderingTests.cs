using System.Buffers.Binary;
using AIL2.Formats;
using AIL2.Parity;
using AIL2.Rendering;
using NukedOPL3Sharp;
using Xunit;

namespace AIL2.Tests;

/// <summary>Checks Miles scheduling/routing with the same trusted synthesizer on both paths.</summary>
public sealed class RenderingTests
{
    /// <summary>Every FM profile, excluding externally synthesized MT-32 music.</summary>
    public static TheoryData<MusicProfile> FmProfiles =>
        new(Enum.GetValues<MusicProfile>().Where(p => p is not (MusicProfile.Mt32 or MusicProfile.PcSpeaker)));

    /// <summary>An authored finite melody with opposite pan positions.</summary>
    private static byte[] Song =>
        Fixtures.Xmi(
            [0xc1, 68, 0xb1, 10, 0, 0x91, 60, 100, 30, 45, 0xb1, 10, 127, 0x91, 64, 100, 20, 30, 0xff, 0x2f, 0]);

    /// <summary>Compares managed PCM with original AIL output replayed through the same trusted synthesizer.</summary>
    /// <param name="profile">The FM interpretation and routing policy.</param>
    [Theory]
    [MemberData(nameof(FmProfiles))]
    public void AilSchedulingMatchesOriginalAilThroughTrustedBackend(MusicProfile profile)
    {
        var trace = new Trace();
        using var native = new OriginalMusicDriver(Fixtures.Root, profile, trace);
        var bank = Fixtures.Bank(profile);
        native.Load(Song, bank);
        native.Start();
        for (var tick = 1; tick <= 240; tick++)
        {
            native.Time = TimeSpan.FromTicks(tick * 83330L);
            native.Service();
        }

        var reference = Replay(trace.Events, profile, 96000);
        using var renderer = new AILRenderer(profile, null);
        renderer.Session.LoadTimbreBank(bank);
        using var sequence = renderer.Session.CreateSequence(XmidiFile.Load(Song));
        sequence.Start();
        var actual = new short[reference.Length];
        renderer.Render(actual);
        var first = Enumerable.Range(0, actual.Length).FirstOrDefault(i => actual[i] != reference[i], -1);
        Assert.True(first < 0, $"First differing Miles PCM sample: {first}");
        Assert.Contains(actual, sample => sample != 0);
    }

    /// <summary>Checks distinct two-operator banks under explicit Sound Blaster OPL3 selection.</summary>
    /// <param name="variant">The authored bank variant.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SoundBlasterOpl3WithDifferentTwoOperatorBanksMatchesOriginalAudio(int variant)
    {
        var data = SyntheticResources.Song();
        var bank = TimbreBank.Load(SyntheticResources.Bank(MusicProfile.SoundBlasterPro2, variant));
        var trace = new Trace();
        using var native = new OriginalMusicDriver(Fixtures.Root, MusicProfile.SoundBlasterPro2, trace);
        native.Load(data, bank);
        native.Start();
        for (var tick = 1; tick <= 240; tick++)
        {
            native.Time = TimeSpan.FromTicks(tick * 83330L);
            native.Service();
        }

        var expected = Replay(trace.Events, MusicProfile.SoundBlasterPro2, 96000);
        using var renderer = new AILRenderer(new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl3));
        renderer.Session.LoadTimbreBank(bank);
        using var sequence = renderer.Session.CreateSequence(XmidiFile.Load(data));
        sequence.Start();
        var actual = new short[expected.Length];
        renderer.Render(actual);
        Assert.Equal(expected, actual);
    }

    /// <summary>Replays original ordered writes and mixer settings through the trusted backend.</summary>
    /// <param name="events">The original-code trace.</param>
    /// <param name="profile">The FM topology.</param>
    /// <param name="frames">The number of stereo frames at 48 kHz.</param>
    /// <returns>The interleaved signed stereo PCM.</returns>
    /// <exception cref="InvalidOperationException">The FM trace contains a non-FM event.</exception>
    private static short[] Replay(List<TraceEvent> events, MusicProfile profile, int frames)
    {
        var chips = profile.IsDualOpl2() ? new Opl3Chip[] { new(), new() } : [new Opl3Chip()];
        foreach (var chip in chips)
        {
            chip.Reset(48000);
        }

        var output = new short[frames * 2];
        var frame = new short[2];
        var other = new short[2];
        var next = 0;
        var levels = new MixerLevels(1, 1, 1);
        for (var i = 0; i < frames; i++)
        {
            var time = (long)((Int128)i * TimeSpan.TicksPerSecond / 48000);
            while (next < events.Count && events[next].Ticks <= time)
            {
                var e = events[next++];
                switch (e.Kind)
                {
                    case "opl":
                        chips[e.A].WriteRegisterBuffered((ushort)e.B, (byte)e.C);
                        break;
                    case "opl-level":
                        levels = new MixerLevels(e.A, e.B, e.C);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected FM replay event {e.Kind}.");
                }
            }

            chips[0].GenerateResampled(frame);
            if (chips.Length == 2)
            {
                chips[1].GenerateResampled(other);
                frame[1] = other[1];
            }

            output[i * 2] = (short)(frame[0] * levels.Left / levels.Maximum);
            output[(i * 2) + 1] = (short)(frame[1] * levels.Right / levels.Maximum);
        }

        return output;
    }

    /// <summary>Checks deterministic mixed FM/PCM output across repeated runs and different caller block sizes.</summary>
    /// <param name="profile">The FM interpretation and routing policy.</param>
    [Theory]
    [MemberData(nameof(FmProfiles))]
    public void MixedOutputIsIndependentOfBlockSizeAndRepeatable(MusicProfile profile)
    {
        var baseline = Render(512);
        Assert.Equal(baseline, Render(1));
        Assert.Equal(baseline, Render(997));
        Assert.Equal(baseline, Render(512));
        return;

        short[] Render(int blockFrames)
        {
            using var renderer = new AILRenderer(profile);
            renderer.Session.LoadTimbreBank(Fixtures.Bank(profile));
            using var sequence = renderer.Session.CreateSequence(XmidiFile.Load(Song));
            sequence.Start();
            using var sample = renderer.Session.CreateSample(
                VocFile.Load(Fixtures.Voc((1, [211, 0, .. Enumerable.Range(0, 16000).Select(i => (byte)i)]), (0, []))));
            sample.Play();
            var result = new short[96000];
            for (var i = 0; i < result.Length; i += blockFrames * 2)
            {
                renderer.Render(result.AsSpan(i, Math.Min(blockFrames * 2, result.Length - i)));
            }

            return result;
        }
    }

    /// <summary>Checks fixed-duration WAV headers, frame count and caller stream ownership.</summary>
    [Fact]
    public void WaveUsesTheLiveRenderPathAndLeavesStreamOpen()
    {
        using var renderer = new AILRenderer(null);
        using var stream = new MemoryStream();
        WaveExporter.Write(stream, renderer, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        var bytes = stream.ToArray();
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(4800, renderer.PositionFrames);
        Assert.True(stream.CanWrite);
    }

    /// <summary>Checks that an injected MIDI observer receives construction-time MT-32 initialization.</summary>
    [Fact]
    public void Mt32InitializationReachesInjectedObserver()
    {
        var trace = new Trace();
        using var renderer = new AILRenderer(MusicProfile.Mt32, midiObserver: trace);
        Assert.NotEmpty(trace.Events);
        Assert.StartsWith("F0411016127F0000", trace.Events[0].Payload);
    }

    /// <summary>Checks finite export completion and header finalization after stopping unbounded output.</summary>
    [Fact]
    public void ExportEndsWithFiniteSequenceAndFinalizesStoppedLoops()
    {
        using var renderer = new AILRenderer();
        renderer.Session.LoadTimbreBank(Fixtures.Bank(MusicProfile.AdLib));
        using var sequence = renderer.Session.CreateSequence(XmidiFile.Load(Song));
        sequence.Start();
        using var complete = new MemoryStream();
        var frames = WaveExporter.WriteToEnd(complete,
            renderer,
            () => sequence.Status == SequenceStatus.Playing,
            TestContext.Current.CancellationToken);
        Assert.Equal(SequenceStatus.Done, sequence.Status);
        Assert.Equal(80 + (frames * 4), complete.Length);
        var bytes = complete.ToArray();
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(frames * 4, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76)));
        using var stop = new CancellationTokenSource();
        using var loop = new MemoryStream();
        var initial = renderer.PositionFrames;
        var stoppedFrames = WaveExporter.WriteToEnd(loop,
            renderer,
            () =>
            {
                if (renderer.PositionFrames - initial >= 12345)
                {
                    stop.Cancel();
                }

                return true;
            },
            stop.Token);
        Assert.True(stoppedFrames >= 12345);
        Assert.Equal(80 + (stoppedFrames * 4), loop.Length);
        Assert.Equal("RIFF"u8.ToArray(), loop.ToArray()[..4]);
    }
}
