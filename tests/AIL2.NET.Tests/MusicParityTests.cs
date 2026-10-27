using AIL2.Formats;
using AIL2.Music;
using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>Compares musical interpretation and sequence state against the original AIL drivers.</summary>
public sealed class MusicParityTests
{
    /// <summary>All supplied original music profiles.</summary>
    public static TheoryData<MusicProfile> Profiles => new(Enum.GetValues<MusicProfile>());

    /// <summary>Compares TIMB request order, duplicate handling and built-in or resident definitions.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void TimbreRequestsFollowOriginalOrderAndSkipResidentDefinitions(MusicProfile profile)
    {
        using var pair = new MusicPair(profile, false);
        var bank = Fixtures.Bank(profile);
        var data = SyntheticResources.Xmi([0xff, 0x2f, 0], timbres: [0x0144, 10, 0x0144, 0x7f35]);
        pair.Native.Register(data);
        using var sequence = pair.Managed.CreateSequence(XmidiFile.Load(data));
        var requests = 0;
        while (true)
        {
            var native = pair.Native.Call(155, 0, pair.Native.SequenceHandle);
            // MT-32 reads a DWORD from the packed WORD list; only AL/AH are the public patch/bank result.
            Assert.Equal(native == uint.MaxValue ? null : (ushort?)native, sequence.TimbreRequest);
            if (sequence.TimbreRequest is not { } key)
            {
                break;
            }

            Assert.True(++requests <= 3);
            Assert.True(bank.TryGet((byte)(key >> 8), (byte)key, out var bytes));
            pair.Install((byte)(key >> 8), (byte)key, bytes);
        }

        // SPKR.INC's get_request always answers -1: the speaker uses no timbres.
        Assert.Equal(profile switch { MusicProfile.Mt32 => 1, MusicProfile.PcSpeaker => 0, _ => 3 }, requests);
        pair.Check();
    }

    /// <summary>Compares nested loops, sustain, ramps, pause/resume and original beat queries.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void NestedLoopsSustainTempoVolumeAndStopResume(MusicProfile profile)
    {
        using var pair = new MusicPair(profile);
        byte[] events =
        [
            0xc1, 68, 0xb1, 7, 101, 0xb1, 64, 127, 0xb1, 116, 2, 0xb1, 116, 3,
            0x91, 60, 112, 4, 5, 0xe1, 127, 70, 0xb1, 117, 127, 0xb1, 117, 127, 0xb1, 64, 0, 20, 0xff, 0x2f, 0
        ];
        var data = Fixtures.Xmi(events);
        pair.Native.Register(data);
        using var sequence = pair.Managed.CreateSequence(XmidiFile.Load(data));
        pair.Native.Start();
        sequence.Start();
        pair.Native.Call(178, 0, pair.Native.SequenceHandle, 170, 250);
        sequence.SetTempo(170, TimeSpan.FromMilliseconds(250));
        pair.Native.Call(177, 0, pair.Native.SequenceHandle, 65, 300);
        sequence.SetVolume(65, TimeSpan.FromMilliseconds(300));
        for (var tick = 1; tick <= 160; tick++)
        {
            switch (tick)
            {
                case 7:
                    pair.Native.Call(171, 0, pair.Native.SequenceHandle);
                    sequence.Pause();
                    break;
                case 12:
                    pair.Native.Call(173, 0, pair.Native.SequenceHandle);
                    sequence.Resume();
                    break;
            }

            pair.Tick(tick);
            Assert.Equal(pair.Native.Status, sequence.Status);
            Assert.Equal(pair.Native.EventOffset, sequence.EventOffset);
            Assert.Equal((int)pair.Native.Call(175, 0, pair.Native.SequenceHandle), sequence.RelativeVolume);
            Assert.Equal((int)pair.Native.Call(176, 0, pair.Native.SequenceHandle), sequence.RelativeTempo);
            Assert.Equal(pair.Native.Call(179, 0, pair.Native.SequenceHandle), sequence.BeatCount);
            Assert.Equal(pair.Native.Call(180, 0, pair.Native.SequenceHandle), sequence.MeasureCount);
        }

        Assert.Equal(SequenceStatus.Done, sequence.Status);
        pair.Check();
    }

    /// <summary>
    ///     XMIDI events that change the time signature (including denominators below a quarter), the tempo and the
    ///     beat/bar reset controller between delays, so beat and measure counting crosses every branch.
    /// </summary>
    internal static readonly byte[] BeatEvents =
    [
        0xff, 0x58, 4, 3, 3, 24, 8, 0xff, 0x51, 3, 0x07, 0xa1, 0x20, 0x91, 60, 100, 40, 127, 90,
        0xff, 0x51, 3, 0x03, 0xd0, 0x90, 127, 0xb1, 118, 0, 100, 0xff, 0x58, 4, 6, 2, 24, 8, 127, 127,
        0xff, 0x58, 4, 2, 1, 24, 8, 127, 60, 0xff, 0x58, 4, 5, 0, 24, 8, 0xff, 0x51, 3, 0x00, 0x40, 0x00, 127,
        0xb1, 118, 0, 0xff, 0x51, 3, 0x0f, 0x42, 0x40, 0xff, 0x58, 4, 7, 4, 24, 8, 127, 127, 127, 0xff, 0x2f, 0
    ];

    /// <summary>Compares beat and measure queries on every service while tempo, meter and resets change.</summary>
    [Fact]
    public void BeatAndMeasureCountsFollowTempoMeterAndResets()
    {
        using var pair = new MusicPair(MusicProfile.AdLib);
        var data = Fixtures.Xmi([0xc1, 68, .. BeatEvents]);
        pair.Native.Register(data);
        using var sequence = pair.Managed.CreateSequence(XmidiFile.Load(data));
        pair.Native.Start();
        sequence.Start();
        var beats = new HashSet<int>();
        for (var tick = 1; sequence.Status != SequenceStatus.Done; tick++)
        {
            pair.Tick(tick);
            Assert.Equal(pair.Native.Status, sequence.Status);
            Assert.Equal(pair.Native.Call(179, 0, pair.Native.SequenceHandle), sequence.BeatCount);
            Assert.Equal(pair.Native.Call(180, 0, pair.Native.SequenceHandle), sequence.MeasureCount);
            beats.Add(sequence.BeatCount);
        }

        Assert.True(beats.Count > 3, "The script must reach several beats within a measure.");
        pair.Check();
    }

    /// <summary>Compares exhausted voice allocation, percussion and source pan/pitch boundaries.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void AllocationExhaustionPercussionAndStereoBoundaries(MusicProfile profile)
    {
        using var pair = new MusicPair(profile);
        for (byte ch = 1; ch <= 8; ch++)
        {
            pair.Send((byte)(0xc0 | ch), 68);
        }

        for (byte note = 35; note < 83; note++)
        {
            pair.Send((byte)(0x90 | ((note % 8) + 1)), note, (byte)(note % 127));
        }

        foreach (var pan in new byte[] { 0, 27, 28, 63, 64, 99, 100, 127 })
        {
            for (byte ch = 1; ch <= 8; ch++)
            {
                pair.Send((byte)(0xb0 | ch), 10, pan);
                pair.Send((byte)(0xe0 | ch), pan, 70);
            }
        }

        for (byte note = 35; note < 83; note++)
        {
            pair.Send((byte)(0x80 | ((note % 8) + 1)), note);
        }

        for (byte note = 35; note < 83; note++)
        {
            pair.Send(0x99, note, 127);
            pair.Send(0x89, note);
        }

        pair.Check();
    }

    /// <summary>Compares resident keys after cache exhaustion while protecting an early entry.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void TimbreEvictionAndProtection(MusicProfile profile)
    {
        using var pair = new MusicPair(profile, false);
        var bank = Fixtures.Bank(profile);
        var key = bank.Keys.First(k => profile != MusicProfile.Mt32 || k >> 8 is not (0 or 127));
        bank.TryGet((byte)(key >> 8), (byte)key, out var bytes);
        for (var i = 0; i < 230; i++)
        {
            pair.Install((byte)(1 + (i / 128)), (byte)(i % 128), bytes);
            if (i == 0)
            {
                pair.Native.Call(157, 0, 1, 0);
                pair.Managed.ProtectTimbre(1, 0);
            }
        }

        for (var i = 0; i < 230; i++)
        {
            Assert.Equal(pair.Native.HasTimbre((byte)(1 + (i / 128)), (byte)(i % 128)),
                pair.Managed.HasTimbre((byte)(1 + (i / 128)), (byte)(i % 128)));
        }

        Assert.True(pair.Managed.HasTimbre(1, 0));
        pair.Check();
    }

    /// <summary>Compares all eight sequence slots and their channel-lock behavior, rejecting a ninth handle.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void EightSequencesAndChannelLocks(MusicProfile profile)
    {
        using var pair = new MusicPair(profile);
        var sequences = new List<XmidiSequence>();
        for (byte i = 0; i < 8; i++)
        {
            var data = Fixtures.Xmi([
                0xc1, 68, 0xb1, 110, 127, 0x91, (byte)(50 + i), 100, 30, 40, 0xb1, 110, 0, 0xff, 0x2f, 0
            ]);
            pair.Native.Register(data);
            var sequence = pair.Managed.CreateSequence(XmidiFile.Load(data));
            pair.Native.Start();
            sequence.Start();
            sequences.Add(sequence);
        }

        Assert.Throws<InvalidOperationException>(() =>
            pair.Managed.CreateSequence(XmidiFile.Load(Fixtures.Xmi([0xff, 0x2f, 0]))));
        for (var tick = 1; tick < 100; tick++)
        {
            pair.Tick(tick);
        }

        Assert.All(sequences, sequence => Assert.Equal(SequenceStatus.Done, sequence.Status));
        pair.Check();
    }

    /// <summary>Compares trigger order and output when the host stops playback inside the callback.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void CallbackMayStopPlayback(MusicProfile profile)
    {
        using var pair = new MusicPair(profile);
        var triggers = new List<byte>();
        var nativeTriggers = new List<byte>();
        var data = Fixtures.Xmi([0xc1, 68, 0x91, 60, 100, 50, 2, 0xb1, 119, 23, 0x91, 64, 100, 5, 8, 0xff, 0x2f, 0]);
        pair.Native.Register(data);
        using var sequence = pair.Managed.CreateSequence(XmidiFile.Load(data));
        pair.Native.SetCallback((_, value) => nativeTriggers.Add(value), stop: true);
        pair.Managed.Trigger += (seq, value) =>
        {
            triggers.Add(value);
            seq.Stop();
        };
        pair.Native.Start();
        sequence.Start();
        for (var tick = 1; tick <= 20; tick++)
        {
            pair.Tick(tick);
        }

        Assert.Equal(nativeTriggers, triggers);
        Assert.Single(triggers);
        Assert.Equal(pair.Native.Status, sequence.Status);
        pair.Check();
    }

    /// <summary>Compares an explicit RBRN branch using original EVNT byte offsets.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void BranchesUseOriginalByteOffsets(MusicProfile profile)
    {
        using var pair = new MusicPair(profile);
        var data = Fixtures.Xmi([0xc1, 68, 0x91, 60, 100, 20, 50, 0x91, 64, 100, 3, 5, 0xff, 0x2f, 0], (7, 7));
        pair.Native.Register(data);
        using var sequence = pair.Managed.CreateSequence(XmidiFile.Load(data));
        pair.Native.Start();
        sequence.Start();
        pair.Tick(1);
        pair.Native.Call(181, 0, pair.Native.SequenceHandle, 7);
        Assert.True(sequence.Branch(7));
        for (var tick = 2; tick < 20; tick++)
        {
            pair.Tick(tick);
        }

        Assert.Equal(SequenceStatus.Done, sequence.Status);
        pair.Check();
    }

    /// <summary>Observes the actual stack-width imbalance in the untouched original MT-32 binary.</summary>
    [Fact]
    public void UntouchedMt32BinaryDemonstratesDocumentedStackFault()
    {
        using var original = new OriginalMusicDriver(Fixtures.Root, MusicProfile.Mt32, new Trace(), false);
        var data = SyntheticResources.Song();
        original.Load(data, Fixtures.Bank(MusicProfile.Mt32));
        original.Start();
        // The corrupted saved pointer can land in mapped memory depending on fixture placement.
        // Observe the actual instruction's stack effect instead of relying on a particular crash address.
        try
        {
            for (var tick = 0; tick < 200; tick++)
            {
                original.Service();
            }
        }
        catch (InvalidOperationException error)
        {
            Assert.Contains("Unicorn", error.Message);
        }

        Assert.True(original.Mt32StackImbalanceObserved);
    }

    /// <summary>Compares every four-operator algorithm and mixed two/four-operator stealing.</summary>
    /// <param name="profile">An original OPL3 music policy.</param>
    [Theory]
    [InlineData(MusicProfile.SoundBlasterPro2)]
    [InlineData(MusicProfile.ProAudioSpectrumOpl3)]
    [InlineData(MusicProfile.AdLibGold)]
    public void FourOperatorAlgorithmsAndMixedVoiceStealing(MusicProfile profile)
    {
        using var pair = new MusicPair(profile, false);
        var bank = Fixtures.Bank(MusicProfile.AdLib);
        Assert.True(bank.TryGet(0, 68, out var first));
        Assert.True(bank.TryGet(0, 10, out var second));
        for (byte algorithm = 0; algorithm < 4; algorithm++)
        {
            byte[] timbre = [.. first.Span, .. second.Span[3..]];
            timbre[0] = 25;
            timbre[8] = (byte)((timbre[8] & 0x7e) | (algorithm & 1) | ((algorithm >> 1) << 7));
            pair.Install(1, algorithm, timbre);
        }

        pair.Install(1, 4, first);
        pair.Send(0xb1, 114, 1);
        for (byte algorithm = 0; algorithm < 4; algorithm++)
        {
            pair.Send(0xc1, algorithm);
            for (byte note = 48; note < 64; note++)
            {
                pair.Send(0x91, note, 100);
            }

            pair.Send(0xb1, 10, 27);
            pair.Send(0xe1, 45, 70);
            pair.Send(0xb1, 10, 100);
            for (byte note = 48; note < 64; note++)
            {
                pair.Send(0x81, note);
            }
        }

        pair.Send(0xc1, 4);
        for (byte note = 36; note < 52; note++)
        {
            pair.Send(0x91, note, 100);
        }

        pair.Send(0xc1, 0);
        for (byte note = 60; note < 76; note++)
        {
            pair.Send(0x91, note, 100);
        }

        pair.Send(0xb1, 123, 0);
        Assert.Contains(pair.NativeTrace.Events, e => e is { Kind: "opl", B: 0x104 } && e.C != 0);
        pair.Check();
    }

    /// <summary>Compares all MT-32 controller-upload queues, patch controls, checksums and display output.</summary>
    [Fact]
    public void Mt32ControllerQueuesChecksumsPatchControlsAndDisplayMatchOriginal()
    {
        using var pair = new MusicPair(MusicProfile.Mt32);
        for (byte queue = 0; queue < 3; queue++)
        {
            var first = 32 + (queue * 5);
            pair.Send(0xb1, (byte)first, 16);
            pair.Send(0xb1, (byte)(first + 1), 0);
            pair.Send(0xb1, (byte)(first + 2), 127);
            for (byte value = 0; value < 33; value++)
            {
                pair.Send(0xb1, (byte)(first + 3), value);
            }

            pair.Send(0xb1, (byte)(first + 4), 126);
        }

        pair.Send(0xc1, 68);
        foreach (var controller in new byte[] { 59, 60, 61, 62, 63 })
        {
            pair.Send(0xb1, controller, 3);
        }

        pair.Native.WriteDisplay("Miles original output");
        pair.Managed.WriteDisplay("Miles original output");
        pair.Check();
    }
}
