using AIL2.Parity;
using AIL2.Rendering;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies the PC speaker driver against SPKR.INC's REL105 build and the 1991 driver's disassembled behavior.</summary>
public sealed class SpeakerTests
{
    /// <summary>
    ///     Channel messages, each followed by a number of services, that reach every SPKR.INC path: pitch bends across the
    ///     range, modulation, voice stealing with and without protection, repeated notes, octave folding at both table ends,
    ///     sustain, controller reset, all-notes-off, ignored channels and a note held into shutdown.
    /// </summary>
    internal static readonly (int Services, byte Status, byte Data1, byte Data2)[] Script =
    [
        (0, 0x91, 60, 100), (3, 0xe1, 0x7f, 0x3f), (3, 0xe1, 0x00, 0x50), (3, 0xe1, 0x7f, 0x7f),
        (3, 0xe1, 0x00, 0x00), (0, 0xe1, 0x00, 0x40), (0, 0xb1, 1, 127), (13, 0xb1, 7, 50), (0, 0xb1, 11, 90),
        (0, 0x91, 64, 100), (6, 0x81, 64, 0), (0, 0xb1, 1, 0), (0, 0x91, 60, 100), (1, 0x81, 60, 0),
        (0, 0x91, 60, 100), (1, 0x91, 60, 0), (0, 0x91, 12, 90), (1, 0x81, 12, 0), (0, 0x91, 13, 90),
        (1, 0x81, 13, 0), (0, 0x91, 0, 64), (1, 0x81, 0, 0), (0, 0x91, 127, 127), (1, 0x81, 127, 0),
        (0, 0xb1, 64, 127), (0, 0x91, 67, 100), (1, 0x81, 67, 0), (2, 0xb1, 64, 0), (0, 0xb1, 112, 127),
        (0, 0x91, 69, 100), (0, 0x91, 71, 100), (2, 0xb1, 112, 0), (0, 0xe1, 0x00, 0x60), (0, 0xb1, 121, 0),
        (2, 0xb1, 123, 0), (0, 0x92, 72, 100), (0, 0x90, 72, 100), (2, 0x91, 74, 100)
    ];

    /// <summary>Plays Script through a driver pair.</summary>
    /// <param name="send">Sends one channel message to both drivers.</param>
    /// <param name="service">Runs one service on both drivers.</param>
    internal static void Play(Action<byte, byte, byte> send, Action service)
    {
        foreach (var (services, status, data1, data2) in Script)
        {
            send(status, data1, data2);
            for (var i = 0; i < services; i++)
            {
                service();
            }
        }
    }

    /// <summary>Creates a speaker session and returns the speaker events written after initialization.</summary>
    /// <param name="revision">The driver release under test.</param>
    /// <param name="trace">Receives every event, including initialization.</param>
    /// <returns>The session; dispose it before reading shutdown output.</returns>
    private static AILSession Session(DriverRevision revision, Trace trace)
    {
        return new AILSession(new MusicConfiguration(MusicDevice.PcSpeaker, null, revision), speakerOutput: trace);
    }

    /// <summary>Formats speaker events after an index as compact text for comparison.</summary>
    /// <param name="trace">The recorded output.</param>
    /// <param name="from">The first event to include.</param>
    /// <returns>Divisors as numbers and gate states as "on" or "off".</returns>
    private static string[] Events(Trace trace, int from)
    {
        return trace.Events.Skip(from)
            .Select(e => e switch
            {
                { Kind: "speaker" } => e.A.ToString(System.Globalization.CultureInfo.InvariantCulture),
                { A: 1 } => "on",
                _ => "off"
            })
            .ToArray();
    }

    /// <summary>Maps the speaker for both releases, requires its sink, names no bank and is refused by the renderer.</summary>
    [Fact]
    public void SpeakerIsAChiplessDeviceWithItsOwnSink()
    {
        foreach (var revision in Enum.GetValues<DriverRevision>())
        {
            Assert.Equal(MusicProfile.PcSpeaker,
                new MusicConfiguration(MusicDevice.PcSpeaker, null, revision).DriverProfile);
            Assert.Empty(MusicConfiguration.SupportedChips(MusicDevice.PcSpeaker, revision));
        }

        Assert.Throws<NotSupportedException>(() =>
            new MusicConfiguration(MusicDevice.PcSpeaker, OplChipMode.Opl2).DriverProfile);
        Assert.Equal("", MusicProfile.PcSpeaker.BankExtension());
        Assert.Throws<ArgumentNullException>("speakerOutput", () => new AILSession(MusicProfile.PcSpeaker));
        Assert.Throws<NotSupportedException>(() => new AILRenderer(MusicProfile.PcSpeaker));
    }

    /// <summary>Plays Script through the REL105 speaker driver and the library and requires identical output.</summary>
    [Fact]
    public void Ail214MatchesTheRel105Driver()
    {
        using var pair = new MusicPair(MusicProfile.PcSpeaker);
        var tick = 0;
        Play(pair.Send, () => pair.Tick(++tick));
        pair.Check();
        Assert.Contains(pair.NativeTrace.Events, e => e.Kind == "speaker");
    }

    /// <summary>
    ///     Reloads the timer for an unchanged divisor only in the 1991 release; AIL 2.14 remembers the last count
    ///     (SPKR.INC 1.01, "PC speaker clicks reduced"). Both turn the speaker on again.
    /// </summary>
    /// <param name="revision">The release under test.</param>
    /// <param name="expected">The speaker events of note-on, note-off and note-on of key 60.</param>
    [Theory]
    [InlineData(DriverRevision.Ail214, new[] { "4561", "on", "off", "on" })]
    [InlineData(DriverRevision.Ail1991, new[] { "4561", "on", "off", "4561", "on" })]
    public void ARepeatedNoteReloadsTheTimerAsTheReleaseDoes(DriverRevision revision, string[] expected)
    {
        var trace = new Trace();
        using var session = Session(revision, trace);
        var from = trace.Events.Count;
        session.SendChannelMessage(0x91, 60, 100);
        session.SendChannelMessage(0x81, 60, 0);
        session.SendChannelMessage(0x91, 60, 100);
        Assert.Equal(expected, Events(trace, from));
    }

    /// <summary>
    ///     Emulates modulation at 20 Hz only in AIL 2.14: every sixth service flips the sign of 5 pitch units per
    ///     modulation step. The 1991 driver has no modulation service and stays silent.
    /// </summary>
    /// <param name="revision">The release under test.</param>
    /// <param name="expected">
    ///     The speaker events of 100 ms (twelve services at 120 Hz, 48 at 480 Hz) with key 60 held at full modulation.
    /// </param>
    [Theory]
    [InlineData(DriverRevision.Ail214, new[] { "4914", "on", "4385", "on" })]
    [InlineData(DriverRevision.Ail1991, new string[0])]
    public void ModulationIsEmulatedAsTheReleaseDoes(DriverRevision revision, string[] expected)
    {
        var trace = new Trace();
        using var session = Session(revision, trace);
        session.SendChannelMessage(0x91, 60, 100);
        session.SendChannelMessage(0xb1, 1, 127);
        var from = trace.Events.Count;
        session.AdvanceTo(TimeSpan.FromTicks(12 * 83_330L));
        Assert.Equal(expected, Events(trace, from));
    }

    /// <summary>
    ///     Bends in semitone steps around 2000h in the 1991 release: 1FFFh already selects the semitone below, and 2800h
    ///     the third semitone above.
    /// </summary>
    [Fact]
    public void Ail1991BendsInSemitonesAround2000h()
    {
        var trace = new Trace();
        using var session = Session(DriverRevision.Ail1991, trace);
        session.SendChannelMessage(0x91, 60, 100);
        var from = trace.Events.Count;
        session.SendChannelMessage(0xe1, 0x7f, 0x3f);
        session.SendChannelMessage(0xe1, 0x00, 0x50);
        Assert.Equal(["4832", "on", "3835", "on"], Events(trace, from));
    }

    /// <summary>Uses each release's divisor table, which differ only in the two lowest entries.</summary>
    /// <param name="revision">The release under test.</param>
    /// <param name="expected">The speaker events of keys 12 and 13, which select table entries 0 and 1.</param>
    [Theory]
    [InlineData(DriverRevision.Ail214, new[] { "36488", "on", "off", "34485", "on" })]
    [InlineData(DriverRevision.Ail1991, new[] { "7218", "on", "off", "3434", "on" })]
    public void TheLowestNotesUseTheReleaseTable(DriverRevision revision, string[] expected)
    {
        var trace = new Trace();
        using var session = Session(revision, trace);
        var from = trace.Events.Count;
        session.SendChannelMessage(0x91, 12, 100);
        session.SendChannelMessage(0x81, 12, 0);
        session.SendChannelMessage(0x91, 13, 100);
        Assert.Equal(expected, Events(trace, from));
    }
}
