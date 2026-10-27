using AIL2.Formats;
using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>Specifies the 1991 driver release's FM and beat-counting behavior without original drivers.</summary>
public sealed class DriverRevisionTests
{
    /// <summary>The number of writes the original reset makes, registers 01h through F5h.</summary>
    private const int ResetWrites = 0xf5;

    /// <summary>XMIDI events that wait 1,016 intervals in the default meter, 4/4 at 500,000 microseconds per beat.</summary>
    internal static readonly byte[] DefaultMeter = [127, 127, 127, 127, 127, 127, 127, 127, 0xff, 0x2f, 0];

    /// <summary>
    ///     The default meter, then MusicParityTests.BeatEvents' tempo, meter and reset changes, then a zero tempo for
    ///     three intervals before the end.
    /// </summary>
    internal static readonly byte[] MeterChanges =
    [
        .. DefaultMeter[..^3], .. MusicParityTests.BeatEvents[..^3], 0xff, 0x51, 3, 0, 0, 0, 3, 0xff, 0x2f, 0
    ];

    /// <summary>
    ///     Plays MeterChanges with a tempo ramp to 170% over 250 ms and a volume ramp to 65% over 300 ms, and requires
    ///     the beat and measure changes, the services that complete each ramp and the service that ends the sequence. The
    ///     expected values are the answers of the game's ADLIB.ADV and PCSPKR.ADV (functions 175, 176, 179 and 180) on
    ///     every service, recorded once at their declared 120 Hz and 480 Hz.
    /// </summary>
    /// <param name="device">The music device.</param>
    [Theory]
    [InlineData(MusicDevice.AdLib)]
    [InlineData(MusicDevice.PcSpeaker)]
    public void Ail1991CountsMeterChangesAndRampsAsTheGameDriversDo(MusicDevice device)
    {
        var speaker = device == MusicDevice.PcSpeaker;
        var trace = new Trace();
        using var session = new AILSession(
            new MusicConfiguration(device, speaker ? null : OplChipMode.Opl2, DriverRevision.Ail1991),
            oplOutput: trace,
            speakerOutput: trace);
        using var sequence = session.CreateSequence(XmidiFile.Load(Fixtures.Xmi(MeterChanges)));
        sequence.Start();
        sequence.SetTempo(170, TimeSpan.FromMilliseconds(250));
        sequence.SetVolume(65, TimeSpan.FromMilliseconds(300));
        var changes = new List<(int, int, int)> { (0, sequence.BeatCount, sequence.MeasureCount) };
        int tempoDone = -1, volumeDone = -1, done = -1;
        for (var service = 1; service <= 20_000 && done < 0; service++)
        {
            session.AdvanceTo(TimeSpan.FromTicks(service * (speaker ? 20_830L : 83_330L)));
            if ((sequence.BeatCount, sequence.MeasureCount) != (changes[^1].Item2, changes[^1].Item3))
            {
                changes.Add((service, sequence.BeatCount, sequence.MeasureCount));
            }

            tempoDone = tempoDone < 0 && sequence.RelativeTempo == 170 ? service : tempoDone;
            volumeDone = volumeDone < 0 && sequence.RelativeVolume == 65 ? service : volumeDone;
            done = sequence.Status == SequenceStatus.Done ? service : -1;
        }

        (int, int, int)[] expected = speaker
            ?
            [
                (0, 0, 0), (156, 1, 0), (297, 2, 0), (439, 3, 0), (580, 0, 1), (673, 1, 1), (743, 2, 1), (777, 0, 2),
                (812, 1, 2), (827, 0, 0), (855, 1, 0), (894, 2, 0), (965, 3, 0), (1146, 0, 1), (1147, 2, 1), (1148, 3, 1),
                (1149, 0, 2), (1150, 1, 2), (1220, 0, 0), (1291, 1, 0), (1362, 2, 0), (1432, 3, 0), (1444, 4, 0),
                (1445, 5, 0), (1446, 6, 0)
            ]
            :
            [
                (0, 0, 0), (40, 1, 0), (75, 2, 0), (110, 3, 0), (146, 0, 1), (181, 1, 1), (216, 2, 1), (252, 3, 1),
                (287, 0, 2), (322, 1, 2), (357, 2, 2), (393, 3, 2), (428, 0, 3), (463, 1, 3), (499, 2, 3), (534, 3, 3),
                (569, 0, 4), (605, 1, 4), (623, 2, 4), (640, 0, 5), (658, 1, 5), (676, 2, 5), (693, 0, 6), (711, 1, 6),
                (729, 2, 6), (737, 0, 7), (746, 1, 7), (755, 2, 7), (763, 0, 8), (772, 1, 8), (781, 2, 8), (790, 0, 9),
                (799, 1, 9), (807, 0, 0), (815, 1, 0), (823, 2, 0), (832, 0, 1), (841, 1, 1), (850, 2, 1), (859, 0, 2),
                (869, 1, 2), (886, 2, 2), (904, 3, 2), (922, 4, 2), (939, 5, 2), (957, 0, 3), (975, 1, 3), (992, 2, 3),
                (1010, 3, 3), (1200, 0, 0), (1219, 1, 0), (1237, 2, 0), (1255, 3, 0), (1272, 4, 0), (1290, 5, 0),
                (1307, 6, 0), (1325, 0, 1), (1343, 1, 1), (1360, 2, 1), (1378, 3, 1), (1396, 4, 1), (1413, 5, 1),
                (1425, 6, 1), (1426, 1, 2)
            ];
        Assert.Equal(speaker ? (123, 149, 1446) : (30, 36, 1426), (tempoDone, volumeDone, done));
        Assert.Equal(expected, changes);
    }

    /// <summary>Rejects a zero tempo as malformed under DriverRevision.Ail214; under the 1991 release it plays on.</summary>
    /// <param name="revision">The release under test.</param>
    [Theory]
    [InlineData(DriverRevision.Ail214)]
    [InlineData(DriverRevision.Ail1991)]
    public void AZeroTempoIsRejectedOnlyByAil214(DriverRevision revision)
    {
        using var session = new AILSession(new MusicConfiguration(MusicDevice.AdLib, OplChipMode.Opl2, revision),
            oplOutput: new Trace());
        using var sequence = session.CreateSequence(XmidiFile.Load(Fixtures.Xmi([0xff, 0x51, 3, 0, 0, 0, 10, 0xff, 0x2f, 0])));
        sequence.Start();
        if (revision == DriverRevision.Ail214)
        {
            Assert.Throws<InvalidDataException>(() => session.AdvanceTo(TimeSpan.FromSeconds(1)));
        }
        else
        {
            session.AdvanceTo(TimeSpan.FromSeconds(1));
            Assert.Equal(SequenceStatus.Done, sequence.Status);
        }
    }

    /// <summary>
    ///     Plays the default meter and records when the counts change and when the sequence ends, in microseconds.
    ///     DriverRevision.Ail214 counts as AIL/32 1.05 does: no beats before a time signature, looking ahead from before
    ///     measure 0 (65535; the 16-bit A214 drivers would report 0). The 1991 drivers report the
    ///     counts as they stand from measure 0, adding their interval to a 32-bit accumulator that they compare with the
    ///     tempo by signed 16-bit halves; at 500,000 (7A120h) a beat therefore completes once the accumulator reaches
    ///     70000h. The 1991 speaker driver declares 480 intervals per second of 2,083 microseconds; every other driver
    ///     uses 120 intervals of 8,333 microseconds.
    /// </summary>
    /// <param name="device">The music device.</param>
    /// <param name="revision">The release under test.</param>
    [Theory]
    [InlineData(MusicDevice.AdLib, DriverRevision.Ail214)]
    [InlineData(MusicDevice.AdLib, DriverRevision.Ail1991)]
    [InlineData(MusicDevice.PcSpeaker, DriverRevision.Ail214)]
    [InlineData(MusicDevice.PcSpeaker, DriverRevision.Ail1991)]
    public void TheDefaultMeterCountsBeatsAsTheDriverDoes(MusicDevice device, DriverRevision revision)
    {
        var chip = device == MusicDevice.PcSpeaker ? (OplChipMode?)null : OplChipMode.Opl2;
        var trace = new Trace();
        using var session = new AILSession(new MusicConfiguration(device, chip, revision),
            oplOutput: trace,
            speakerOutput: trace);
        using var sequence = session.CreateSequence(XmidiFile.Load(Fixtures.Xmi(DefaultMeter)));
        sequence.Start();
        var changes = new List<(long Time, ushort Beat, ushort Measure)> { (0, sequence.BeatCount, sequence.MeasureCount) };
        // Every service time of either period, so each observation and the end fall on the service that caused them.
        var steps = Enumerable.Range(1, 1100).SelectMany(k => new[] { k * 20_830L, k * 83_330L }).Distinct().Order();
        foreach (var ticks in steps.TakeWhile(_ => sequence.Status != SequenceStatus.Done))
        {
            session.AdvanceTo(TimeSpan.FromTicks(ticks));
            if ((sequence.BeatCount, sequence.MeasureCount) != (changes[^1].Beat, changes[^1].Measure))
            {
                changes.Add((session.Time.Ticks / 10, sequence.BeatCount, sequence.MeasureCount));
            }
        }

        // 1,017 intervals: the first reads the opening delay, the eight delays of 127 end on the end-of-track event.
        (long, int, int)[] expected = (device, revision) switch
        {
            (MusicDevice.AdLib, DriverRevision.Ail1991) =>
                [(0, 0, 0), (466_648, 1, 0), (966_628, 2, 0), (1_466_608, 3, 0), (1_966_588, 0, 1)],
            (MusicDevice.PcSpeaker, DriverRevision.Ail1991) =>
                [(0, 0, 0), (460_343, 1, 0), (960_263, 2, 0), (1_460_183, 3, 0), (1_960_103, 0, 1)],
            _ => [(0, 0, 65535)]
        };
        Assert.Equal(SequenceStatus.Done, sequence.Status);
        Assert.Equal(1017 * (device == MusicDevice.PcSpeaker && revision == DriverRevision.Ail1991 ? 2083 : 8333),
            session.Time.Ticks / 10);
        Assert.Equal(expected, changes.Take(5).Select(c => (c.Time, (int)c.Beat, (int)c.Measure)));
    }

    /// <summary>Restricts the 1991 release to its OPL2, dual OPL2 and MT-32 drivers and keeps the selection.</summary>
    [Fact]
    public void Ail1991OffersOnlyItsShippedDevicesAndIsKeptBySessions()
    {
        Assert.Equal([OplChipMode.Opl2], MusicConfiguration.SupportedChips(MusicDevice.AdLib, DriverRevision.Ail1991));
        Assert.Equal([OplChipMode.Opl2, OplChipMode.DualOpl2],
            MusicConfiguration.SupportedChips(MusicDevice.SoundBlaster, DriverRevision.Ail1991));
        Assert.Empty(MusicConfiguration.SupportedChips(MusicDevice.ProAudioSpectrum, DriverRevision.Ail1991));
        Assert.Empty(MusicConfiguration.SupportedChips(MusicDevice.AdLibGold, DriverRevision.Ail1991));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MusicConfiguration.SupportedChips(MusicDevice.AdLib, (DriverRevision)2));
        Assert.Equal(MusicProfile.SoundBlasterPro1,
            new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.DualOpl2, DriverRevision.Ail1991)
                .DriverProfile);
        Assert.Throws<NotSupportedException>(() =>
            new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl3, DriverRevision.Ail1991).DriverProfile);
        Assert.Throws<NotSupportedException>(() =>
            new MusicConfiguration(MusicDevice.ProAudioSpectrum, OplChipMode.DualOpl2, DriverRevision.Ail1991)
                .DriverProfile);
        Assert.Throws<NotSupportedException>(() =>
            new MusicConfiguration(MusicDevice.AdLib, OplChipMode.Opl2, (DriverRevision)2).DriverProfile);
        var music = new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl2, DriverRevision.Ail1991);
        using var session = new AILSession(music, oplOutput: new Trace());
        Assert.Equal(music, session.MusicConfiguration);
    }

    /// <summary>Gives a new note the lowest free voice instead of the voice after the last assignment.</summary>
    /// <param name="revision">The release under test.</param>
    /// <param name="voice">The voice the third note must key on.</param>
    [Theory]
    [InlineData(DriverRevision.Ail214, 2)]
    [InlineData(DriverRevision.Ail1991, 0)]
    public void ANewNoteTakesTheVoiceTheReleaseAssigns(DriverRevision revision, int voice)
    {
        var trace = new Trace();
        using var session = Session(MusicDevice.AdLib, OplChipMode.Opl2, revision, trace);
        session.SendChannelMessage(0x91, 60, 100);
        session.SendChannelMessage(0x91, 64, 100);
        session.SendChannelMessage(0x81, 60, 0);
        var start = trace.Events.Count;
        session.SendChannelMessage(0x91, 67, 100);
        var keyOn = Assert.Single(trace.Events.Skip(start), e => e.B is >= 0xb0 and <= 0xb8 && (e.C & 0x20) != 0);
        Assert.Equal(0xb0 + voice, keyOn.B);
    }

    /// <summary>
    ///     After the reset, every 1991 write changes the register it targets, while AIL 2.14 rewrites unchanged values
    ///     in the same scenario.
    /// </summary>
    /// <param name="chip">The single-chip or dual OPL2 topology.</param>
    [Theory]
    [InlineData(OplChipMode.Opl2)]
    [InlineData(OplChipMode.DualOpl2)]
    public void Ail1991WritesOnlyChangedValuesAfterTheReset(OplChipMode chip)
    {
        var current = Repeats(chip, DriverRevision.Ail214);
        var old = Repeats(chip, DriverRevision.Ail1991);
        Assert.NotEqual(0, current.Repeated);
        Assert.Equal(0, old.Repeated);
        Assert.NotEqual(0, old.Changed);
    }

    /// <summary>Writes dual OPL2 voice levels to both chips on every voice update, even when they are unchanged.</summary>
    [Fact]
    public void Ail1991DualOpl2RewritesPannedLevelsOnBothChips()
    {
        var trace = new Trace();
        using var session = Session(MusicDevice.SoundBlaster, OplChipMode.DualOpl2, DriverRevision.Ail1991, trace);
        session.SendChannelMessage(0x91, 60, 100);
        session.SendChannelMessage(0x81, 60, 0);
        var start = trace.Events.Count;
        session.SendChannelMessage(0x91, 60, 100);
        var levels = trace.Events.Skip(start).Where(e => e.B is >= 0x40 and <= 0x55).ToList();
        Assert.Contains(levels, e => e.A == 0);
        Assert.Contains(levels, e => e.A == 1);
        Assert.Equal(levels.Count(e => e.A == 0), levels.Count(e => e.A == 1));
    }

    /// <summary>Resets every register at shutdown regardless of the values the chip already holds.</summary>
    [Fact]
    public void Ail1991ShutdownRewritesTheWholeRegisterSet()
    {
        var trace = new Trace();
        var session = Session(MusicDevice.AdLib, OplChipMode.Opl2, DriverRevision.Ail1991, trace);
        var start = trace.Events.Count;
        session.Dispose();
        Assert.Equal(Enumerable.Range(1, ResetWrites), trace.Events.Skip(start).Select(e => e.B));
    }

    /// <summary>Creates an FM session with the authored AdLib bank resident and program 68 on channel 1.</summary>
    /// <param name="device">The music device.</param>
    /// <param name="chip">The FM topology.</param>
    /// <param name="revision">The driver release.</param>
    /// <param name="trace">The OPL sink.</param>
    /// <returns>The owned session.</returns>
    private static AILSession Session(MusicDevice device, OplChipMode chip, DriverRevision revision, Trace trace)
    {
        var session = new AILSession(new MusicConfiguration(device, chip, revision), oplOutput: trace);
        session.LoadTimbreBank(Fixtures.Bank(MusicProfile.AdLib));
        session.SendChannelMessage(0xc1, 68, 0);
        return session;
    }

    /// <summary>
    ///     Plays repeated identical notes on two channels and counts post-reset writes that repeat or change the
    ///     register's value. Dual OPL2 panned levels are excluded because they bypass the comparison.
    /// </summary>
    /// <param name="chip">The FM topology.</param>
    /// <param name="revision">The driver release.</param>
    /// <returns>The repeated and changed write counts.</returns>
    private static (int Repeated, int Changed) Repeats(OplChipMode chip, DriverRevision revision)
    {
        var trace = new Trace();
        int end;
        using (var session = Session(MusicDevice.SoundBlaster, chip, revision, trace))
        {
            session.SendChannelMessage(0xc2, 68, 0);
            for (var i = 0; i < 4; i++)
            {
                session.SendChannelMessage(0x91, 60, 100);
                session.SendChannelMessage(0x92, 64, 90);
                session.SendChannelMessage(0x81, 60, 0);
                session.SendChannelMessage(0x82, 64, 0);
            }

            session.SendChannelMessage(0xb1, 7, 90);
            end = trace.Events.Count;
        }

        var values = new Dictionary<(int, int), int>();
        var repeated = 0;
        var changed = 0;
        var index = 0;
        foreach (var e in trace.Events.Take(end).Where(e => e.Kind == "opl"))
        {
            var reset = index++ < ResetWrites * (chip == OplChipMode.DualOpl2 ? 2 : 1);
            var level = chip == OplChipMode.DualOpl2 && e.B is >= 0x40 and <= 0x55;
            if (!reset && !level && values.TryGetValue((e.A, e.B), out var value))
            {
                if (value == e.C)
                {
                    repeated++;
                }
                else
                {
                    changed++;
                }
            }

            values[(e.A, e.B)] = e.C;
        }

        return (repeated, changed);
    }
}
