using AIL2.Formats;
using AIL2.Music;
using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies the real-mode oracle against the A214 .ADV drivers and their REL105 32-bit builds.</summary>
public sealed class AdvDriverTests
{
    /// <summary>The A214 drivers built for the music profiles Star Trek 25th Anniversary can select.</summary>
    public static TheoryData<MusicProfile, string> Drivers => new()
    {
        { MusicProfile.AdLib, "ADLIB.ADV" },
        { MusicProfile.SoundBlaster, "SBFM.ADV" },
        { MusicProfile.SoundBlasterPro1, "SBP1FM.ADV" },
        { MusicProfile.Mt32, "MT32MPU.ADV" },
        { MusicProfile.PcSpeaker, "PCSPKR.ADV" }
    };

    /// <summary>
    ///     The Star Trek 25th Anniversary music drivers with their device configuration and song suffix. They are game
    ///     data and are read only from an uncommitted local copy under game_files/st25th. The speaker driver names no
    ///     suffix, so the game falls back to "pc" (0x29A13).
    /// </summary>
    public static TheoryData<string, MusicDevice, OplChipMode?, string> GameDrivers => new()
    {
        { "ADLIB.ADV", MusicDevice.AdLib, OplChipMode.Opl2, "AD" },
        { "SBFM.ADV", MusicDevice.SoundBlaster, OplChipMode.Opl2, "AD" },
        { "SBPFM.ADV", MusicDevice.SoundBlaster, OplChipMode.DualOpl2, "AD" },
        { "MT32MPU.ADV", MusicDevice.Mt32, null, "MT" },
        { "PCSPKR.ADV", MusicDevice.PcSpeaker, null, "PC" }
    };

    /// <summary>Resolves a file in the local game copy, skipping the test when the copy is absent.</summary>
    /// <param name="relative">The path below game_files/st25th.</param>
    /// <returns>The existing file path.</returns>
    private static string Game(string relative)
    {
        var path = Path.Combine(Fixtures.Root, "game_files/st25th", relative);
        if (!File.Exists(path))
        {
            Assert.Skip($"Star Trek 25th Anniversary file {relative} is not in game_files/st25th.");
        }

        return path;
    }

    /// <summary>Loads the bank the game installs at startup: GTL with the driver's suffix, or none when the driver keeps no cache.</summary>
    /// <param name="suffix">The driver's data suffix.</param>
    /// <returns>The owned bank, empty for MT-32 and the speaker.</returns>
    private static TimbreBank GameBank(string suffix)
    {
        return suffix == "AD" ? TimbreBank.Load(File.ReadAllBytes(Game("music/GTL.AD"))) : TimbreBank.Load([0, 0xff]);
    }

    /// <summary>
    ///     Plays every sequence of every Star Trek 25th Anniversary song through the game's own driver and through the
    ///     library with the 1991 revision, including driver shutdown.
    /// </summary>
    /// <param name="driver">The game driver filename.</param>
    /// <param name="device">The device the driver serves.</param>
    /// <param name="chip">The FM topology, or null for MT-32.</param>
    /// <param name="suffix">The song and bank suffix the driver reports.</param>
    [Theory]
    [MemberData(nameof(GameDrivers))]
    public void GameSongsPlayAsTheGameDriversPlayThem(string driver, MusicDevice device, OplChipMode? chip, string suffix)
    {
        var path = Game("drivers/" + driver);
        var music = new MusicConfiguration(device, chip, DriverRevision.Ail1991);
        var bank = GameBank(suffix);
        var songs = suffix == "MT"
            ? new[] { "INIT", "TITLE", "BRIDGE", "BRIDGEB", "BRIDGEW", "GROUND" }
            : ["TITLE", "BRIDGE", "BRIDGEB", "BRIDGEW", "GROUND"];
        foreach (var song in songs)
        {
            var data = File.ReadAllBytes(Game($"music/{song}.{suffix}"));
            var xmidi = XmidiFile.Load(data);
            for (var index = 0; index < xmidi.Sequences.Count; index++)
            {
                var adv = new Trace();
                var managed = new Trace();
                using (var original = new OriginalAdvMusicDriver(path, music.DriverProfile, adv))
                {
                    using var session = new AILSession(music, oplOutput: managed, midiOutput: managed, speakerOutput: managed);
                    original.Install(bank);
                    session.LoadTimbreBank(bank);
                    original.Register(data, index);
                    var sequence = session.CreateSequence(xmidi, index);
                    original.Start();
                    sequence.Start();
                    for (var tick = 1; tick <= 200_000; tick++)
                    {
                        var time = TimeSpan.FromTicks(tick * original.ServicePeriod.Ticks);
                        original.Time = time;
                        original.Service();
                        session.AdvanceTo(time);
                        Assert.True(original.Status == sequence.Status,
                            $"{song} sequence {index} status at service {tick}: {original.Status}/{sequence.Status}");
                        Assert.True(original.EventOffset == sequence.EventOffset,
                            $"{song} sequence {index} position at service {tick}: {original.EventOffset}/{sequence.EventOffset}");
                        AssertCounts(original, sequence, $"{song} sequence {index} at service {tick}");
                        if (sequence.Status == SequenceStatus.Done)
                        {
                            break;
                        }

                        if (sequence.UnboundedLoopIterations >= 2)
                        {
                            original.Call(171, 0, original.SequenceHandle);
                            sequence.Stop();
                            break;
                        }
                    }

                    original.Shutdown();
                    session.Dispose();
                }

                Assert.NotEmpty(adv.Events);
                Fixtures.Equal(adv, managed);
            }
        }
    }

    /// <summary>
    ///     Compares beat and measure queries and tempo and volume ramps on every service through the default meter, then
    ///     tempo, meter and reset changes, including denominators below a quarter, ending with a zero tempo.
    /// </summary>
    /// <param name="driver">The game driver filename.</param>
    /// <param name="device">The device the driver serves.</param>
    /// <param name="chip">The FM topology, or null for MT-32.</param>
    /// <param name="suffix">The song and bank suffix the driver reports.</param>
    [Theory]
    [MemberData(nameof(GameDrivers))]
    public void GameDriversCountBeatsAndRampAsTheLibraryDoes(string driver,
        MusicDevice device,
        OplChipMode? chip,
        string suffix)
    {
        var path = Game("drivers/" + driver);
        var music = new MusicConfiguration(device, chip, DriverRevision.Ail1991);
        var bank = GameBank(suffix);
        var data = Fixtures.Xmi(DriverRevisionTests.MeterChanges);
        var adv = new Trace();
        var managed = new Trace();
        var counts = new HashSet<(ushort, ushort)>();
        using (var original = new OriginalAdvMusicDriver(path, music.DriverProfile, adv))
        {
            using var session = new AILSession(music, oplOutput: managed, midiOutput: managed, speakerOutput: managed);
            original.Install(bank);
            session.LoadTimbreBank(bank);
            original.Register(data);
            var sequence = session.CreateSequence(XmidiFile.Load(data));
            original.Start();
            sequence.Start();
            original.Call(178, 0, original.SequenceHandle, 170, 250);
            sequence.SetTempo(170, TimeSpan.FromMilliseconds(250));
            original.Call(177, 0, original.SequenceHandle, 65, 300);
            sequence.SetVolume(65, TimeSpan.FromMilliseconds(300));
            for (var tick = 1; tick <= 20_000 && sequence.Status != SequenceStatus.Done; tick++)
            {
                var time = TimeSpan.FromTicks(tick * original.ServicePeriod.Ticks);
                original.Time = time;
                original.Service();
                session.AdvanceTo(time);
                Assert.Equal(original.Status, sequence.Status);
                AssertCounts(original, sequence, $"service {tick}");
                var tempo = (ushort)original.Call(176, 0, original.SequenceHandle);
                var volume = (ushort)original.Call(175, 0, original.SequenceHandle);
                Assert.True(tempo == sequence.RelativeTempo && volume == sequence.RelativeVolume,
                    $"service {tick}: tempo/volume {tempo}/{volume}, library {sequence.RelativeTempo}/{sequence.RelativeVolume}");
                counts.Add((sequence.BeatCount, sequence.MeasureCount));
            }

            Assert.Equal(SequenceStatus.Done, sequence.Status);
            original.Shutdown();
            session.Dispose();
        }

        Assert.True(counts.Count > 8, "The script must reach several beats and measures.");
        Fixtures.Equal(adv, managed);
    }

    /// <summary>Requires the library's beat and measure counts to equal the driver's queries, which return AX.</summary>
    /// <param name="original">The game driver.</param>
    /// <param name="sequence">The library sequence.</param>
    /// <param name="where">The position for the failure message.</param>
    private static void AssertCounts(OriginalAdvMusicDriver original, XmidiSequence sequence, string where)
    {
        var beat = (ushort)original.Call(179, 0, original.SequenceHandle);
        var measure = (ushort)original.Call(180, 0, original.SequenceHandle);
        Assert.True(beat == sequence.BeatCount && measure == sequence.MeasureCount,
            $"{where}: beat/measure {beat}/{measure}, library {sequence.BeatCount}/{sequence.MeasureCount}");
    }

    /// <summary>
    ///     Drives the game driver and the library the way the game's music routines (0x29E3F, 0x2A4E9, 0x2A5BA) do:
    ///     a primary sequence restarted when it ends, secondary cues on all seven other channels, a cue restarted while
    ///     playing, cues replaced by stop, release, registration and start, and a primary switch.
    /// </summary>
    /// <param name="driver">The game driver filename.</param>
    /// <param name="device">The device the driver serves.</param>
    /// <param name="chip">The FM topology, or null for MT-32.</param>
    /// <param name="suffix">The song and bank suffix the driver reports.</param>
    [Theory]
    [MemberData(nameof(GameDrivers))]
    public void GameCuesPlayAsTheGameDriversPlayThem(string driver, MusicDevice device, OplChipMode? chip, string suffix)
    {
        var path = Game("drivers/" + driver);
        var music = new MusicConfiguration(device, chip, DriverRevision.Ail1991);
        var bank = GameBank(suffix);
        var data = File.ReadAllBytes(Game($"music/GROUND.{suffix}"));
        var xmidi = XmidiFile.Load(data);
        var adv = new Trace();
        var managed = new Trace();
        var handles = new ushort?[8];
        var sequences = new XmidiSequence?[8];
        using (var original = new OriginalAdvMusicDriver(path, music.DriverProfile, adv))
        {
            using var session = new AILSession(music, oplOutput: managed, midiOutput: managed, speakerOutput: managed);
            original.Install(bank);
            session.LoadTimbreBank(bank);
            var schedule = new Dictionary<int, Action>
            {
                [1] = () => Cue(0, 0), [40] = () => Cue(1, 4), [70] = () => Cue(2, 5), [100] = () => Restart(1),
                [130] = () => Cue(2, 6), [160] = () => Cue(3, 7), [175] = () => Cue(4, 9), [190] = () => Cue(5, 10),
                [205] = () => Cue(6, 11), [220] = () => Cue(7, 12), [400] = () => Stop(3), [600] = () => Cue(0, 1),
                [700] = () => Restart(2)
            };
            for (var tick = 1; tick <= 1500; tick++)
            {
                if (schedule.TryGetValue(tick, out var action))
                {
                    action();
                }

                var time = TimeSpan.FromTicks(tick * original.ServicePeriod.Ticks);
                original.Time = time;
                original.Service();
                session.AdvanceTo(time);
                for (var channel = 0; channel < 8; channel++)
                {
                    if (handles[channel] is { } handle)
                    {
                        var status = (SequenceStatus)(ushort)original.Call(174, 0, handle);
                        Assert.True(status == sequences[channel]!.Status,
                            $"channel {channel} status at service {tick}: {status}/{sequences[channel]!.Status}");
                    }
                }

                // The game's service loop restarts a finished primary sequence on its existing handle.
                if (sequences[0] is { Status: SequenceStatus.Done })
                {
                    Restart(0);
                }
            }

            for (var channel = 0; channel < 8; channel++)
            {
                Stop(channel);
            }

            original.Shutdown();
            session.Dispose();

            void Stop(int channel)
            {
                if (handles[channel] is not { } handle)
                {
                    return;
                }

                original.Call(171, 0, handle);
                original.Call(152, 0, handle);
                sequences[channel]!.Stop();
                sequences[channel]!.Dispose();
                handles[channel] = null;
                sequences[channel] = null;
            }

            void Cue(int channel, int index)
            {
                Stop(channel);
                handles[channel] = original.Register(data, index);
                sequences[channel] = session.CreateSequence(xmidi, index);
                Restart(channel);
            }

            void Restart(int channel)
            {
                original.Call(170, 0, handles[channel]!.Value);
                sequences[channel]!.Start();
            }
        }

        Assert.NotEmpty(adv.Events);
        Fixtures.Equal(adv, managed);
    }

    /// <summary>Plays the speaker scenario through the game's speaker driver and the library's 1991 revision, with shutdown.</summary>
    [Fact]
    public void GameSpeakerDriverPlaysTheSpeakerScenarioAsTheLibraryDoes()
    {
        var path = Game("drivers/PCSPKR.ADV");
        var adv = new Trace();
        var managed = new Trace();
        using (var original = new OriginalAdvMusicDriver(path, MusicProfile.PcSpeaker, adv))
        {
            using var session = new AILSession(
                new MusicConfiguration(MusicDevice.PcSpeaker, null, DriverRevision.Ail1991),
                speakerOutput: managed);
            var tick = 0;
            SpeakerTests.Play((status, data1, data2) =>
                {
                    original.Call(186, 0, status, data1, data2);
                    session.SendChannelMessage(status, data1, data2);
                },
                () =>
                {
                    var time = TimeSpan.FromTicks(++tick * original.ServicePeriod.Ticks);
                    original.Time = time;
                    original.Service();
                    session.AdvanceTo(time);
                });
            original.Shutdown();
            session.Dispose();
        }

        Assert.Contains(adv.Events, e => e.Kind == "speaker");
        Fixtures.Equal(adv, managed);
    }

    /// <summary>Runs a game driver through the parity command, which must match it only with the 1991 revision.</summary>
    [Fact]
    public void ParityCommandMatchesAGameDriverOnlyWithItsRevision()
    {
        string[] arguments =
        [
            "--root", Path.Combine(Fixtures.Root, "artifacts", "adv-game-" + Guid.NewGuid()), "--adv",
            Game("drivers/ADLIB.ADV"), "--profile", "AdLib", "--input", Game("music/TITLE.AD"), "--bank",
            Game("music/GTL.AD")
        ];
        Assert.Equal(1, ParityCommand.Run(arguments));
        Assert.Equal(0, ParityCommand.Run([.. arguments, "--revision", "Ail1991"]));
    }

    /// <summary>Runs the game's speaker driver through the parity command, which services it at its 480 Hz.</summary>
    [Fact]
    public void ParityCommandServicesAGameDriverAtItsOwnRate()
    {
        Assert.Equal(0,
            ParityCommand.Run([
                "--root", Path.Combine(Fixtures.Root, "artifacts", "adv-game-" + Guid.NewGuid()), "--adv",
                Game("drivers/PCSPKR.ADV"), "--profile", "PcSpeaker", "--input", Game("music/TITLE.PC"), "--revision",
                "Ail1991"
            ]));
    }

    /// <summary>Resolves an A214 distribution driver.</summary>
    /// <param name="file">The driver filename.</param>
    /// <returns>The checkout path.</returns>
    private static string A214(string file)
    {
        return Path.Combine(Fixtures.Root, "reference/A214_D1", file);
    }

    /// <summary>Reads the description table each driver returns, which XMIDI.ASM, YAMAHA.INC, MT32.INC and SPKR.INC define.</summary>
    /// <param name="profile">The port layout the driver uses.</param>
    /// <param name="file">The A214 driver filename.</param>
    [Theory]
    [MemberData(nameof(Drivers))]
    public void DriversReportTheirSourceDescriptionTables(MusicProfile profile, string file)
    {
        using var driver = new OriginalAdvMusicDriver(A214(file), profile, new Trace());
        Assert.Equal(200, driver.MinimumApiVersion);
        Assert.Equal(3, driver.DriverType);
        Assert.Equal(profile switch { MusicProfile.Mt32 => "MT", MusicProfile.PcSpeaker => "", _ => "AD" },
            driver.DataSuffix);
        Assert.Equal(120, driver.ServiceRate);
    }

    /// <summary>Rejects a driver whose description table requests no periodic service, as A214's digital drivers do.</summary>
    [Fact]
    public void ADriverWithoutPeriodicServiceIsRejected()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            new OriginalAdvMusicDriver(A214("SBDIG.ADV"), MusicProfile.AdLib, new Trace()));
        Assert.Contains("service rate -1", error.Message);
    }

    /// <summary>
    ///     Plays the generated resources through a real-mode A214 driver and its REL105 build, which share their
    ///     sequencing and synthesizer sources, and requires identical output, status and event position on every service.
    /// </summary>
    /// <param name="profile">The port layout and REL105 driver to compare.</param>
    /// <param name="file">The A214 driver filename.</param>
    [Theory]
    [MemberData(nameof(Drivers))]
    public void RealModeDriversMatchTheirProtectedModeBuilds(MusicProfile profile, string file)
    {
        var bank = Fixtures.Bank(profile);
        foreach (var name in new[] { "finite.xmi", "loop.xmi" })
        {
            var data = File.ReadAllBytes(Fixtures.Resource(name));
            var adv = new Trace();
            var lx = new Trace();
            using (var real = new OriginalAdvMusicDriver(A214(file), profile, adv))
            using (var protectedMode = new OriginalMusicDriver(Fixtures.Root, profile, lx))
            {
                real.Load(data, bank);
                protectedMode.Load(data, bank);
                real.Start();
                protectedMode.Start();
                for (var tick = 1; tick <= 3000; tick++)
                {
                    var time = TimeSpan.FromTicks(tick * 83_330L);
                    real.Time = time;
                    protectedMode.Time = time;
                    real.Service();
                    protectedMode.Service();
                    Assert.Equal(protectedMode.Status, real.Status);
                    Assert.Equal(protectedMode.EventOffset, real.EventOffset);
                    if (protectedMode.Status == SequenceStatus.Done)
                    {
                        break;
                    }
                }

                real.Shutdown();
                protectedMode.Shutdown();
            }

            Assert.NotEmpty(lx.Events);
            Fixtures.Equal(lx, adv);
        }
    }
}
