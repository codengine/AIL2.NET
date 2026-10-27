namespace AIL2;

/// <summary>Selects a device family independently of its available FM chip modes.</summary>
public enum MusicDevice
{
    /// <summary>AdLib with OPL2 music.</summary>
    AdLib = 0,

    /// <summary>Sound Blaster with selectable OPL2, dual OPL2 or OPL3 music.</summary>
    SoundBlaster = 1,

    /// <summary>Pro Audio Spectrum with dual OPL2 or OPL3 music.</summary>
    ProAudioSpectrum = 2,

    /// <summary>AdLib Gold with OPL3 music.</summary>
    AdLibGold = 3,

    /// <summary>MT-32 external MIDI output, without an OPL chip selection.</summary>
    Mt32 = 4,

    /// <summary>IBM PC internal speaker timer output, without an OPL chip selection.</summary>
    PcSpeaker = 5
}

/// <summary>Selects FM topology and the corresponding original Miles interpretation.</summary>
public enum OplChipMode
{
    /// <summary>One OPL2 register space with nine two-operator voices.</summary>
    Opl2 = 0,

    /// <summary>Two independent OPL2 register spaces using the original stereo routing.</summary>
    DualOpl2 = 1,

    /// <summary>One OPL3 register space supporting two- and four-operator instruments.</summary>
    Opl3 = 2
}

/// <summary>Selects which release of the original Miles drivers the music interpretation follows.</summary>
public enum DriverRevision
{
    /// <summary>
    ///     The AIL 2.14 and AIL/32 1.05 drivers, whose sources ship in reference/. Beat and measure queries follow AIL/32
    ///     1.05 where the two differ.
    /// </summary>
    Ail214 = 0,

    /// <summary>
    ///     The 1991 AIL 2.0 drivers by John Miles, as shipped with Star Trek: 25th Anniversary. Their sequencer counts
    ///     beats and measures without looking ahead, from measure 0, and keeps the counts at a time signature. Their FM
    ///     drivers skip register writes that repeat the current value and give a new note the lowest free voice. Their
    ///     speaker driver is serviced at 480 Hz instead of 120 Hz, bends pitch in semitone steps, emulates no modulation
    ///     and reloads the timer on every pitch update. Supported for AdLib and Sound Blaster OPL2, Sound Blaster dual
    ///     OPL2, MT-32 and the PC speaker; MT-32 output is the same as under AIL 2.14.
    /// </summary>
    Ail1991 = 1
}

/// <summary>Maps a music device, FM topology and driver release to the corresponding Miles driver profile.</summary>
/// <param name="Device">The music device family.</param>
/// <param name="Chip">The FM topology, or null for MT-32 and the PC speaker.</param>
/// <param name="Revision">The driver release whose behavior to follow; AIL 2.14 by default.</param>
/// <remarks>The combination is validated when DriverProfile is read.</remarks>
public readonly record struct MusicConfiguration(
    MusicDevice Device,
    OplChipMode? Chip,
    DriverRevision Revision = DriverRevision.Ail214)
{
    /// <summary>Supported AdLib topology in selector order.</summary>
    private static readonly IReadOnlyList<OplChipMode> AdLibChips = Array.AsReadOnly(new[] { OplChipMode.Opl2 });

    /// <summary>Supported Sound Blaster topologies in selector order.</summary>
    private static readonly IReadOnlyList<OplChipMode> SoundBlasterChips =
        Array.AsReadOnly(new[] { OplChipMode.Opl2, OplChipMode.DualOpl2, OplChipMode.Opl3 });

    /// <summary>Supported Pro Audio Spectrum topologies in selector order.</summary>
    private static readonly IReadOnlyList<OplChipMode> PasChips =
        Array.AsReadOnly(new[] { OplChipMode.DualOpl2, OplChipMode.Opl3 });

    /// <summary>Supported AdLib Gold topology.</summary>
    private static readonly IReadOnlyList<OplChipMode> GoldChips = Array.AsReadOnly(new[] { OplChipMode.Opl3 });

    /// <summary>Sound Blaster topologies of the 1991 release, which has no OPL3 driver.</summary>
    private static readonly IReadOnlyList<OplChipMode> SoundBlaster1991Chips =
        Array.AsReadOnly(new[] { OplChipMode.Opl2, OplChipMode.DualOpl2 });

    /// <summary>Lists the FM topologies available for a device family in a driver release.</summary>
    /// <param name="device">The family whose chip choices are requested.</param>
    /// <param name="revision">The driver release; AIL 2.14 by default.</param>
    /// <returns>
    ///     A read-only list in selector order, or an empty list for MT-32, the PC speaker and devices the release
    ///     lacks.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">device or revision is not a defined value.</exception>
    public static IReadOnlyList<OplChipMode> SupportedChips(MusicDevice device,
        DriverRevision revision = DriverRevision.Ail214)
    {
        if (!Enum.IsDefined(revision))
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        return (device, revision) switch
        {
            (MusicDevice.AdLib, _) => AdLibChips,
            (MusicDevice.SoundBlaster, DriverRevision.Ail1991) => SoundBlaster1991Chips,
            (MusicDevice.SoundBlaster, _) => SoundBlasterChips,
            (MusicDevice.ProAudioSpectrum or MusicDevice.AdLibGold, DriverRevision.Ail1991) => Array.Empty<OplChipMode>(),
            (MusicDevice.ProAudioSpectrum, _) => PasChips,
            (MusicDevice.AdLibGold, _) => GoldChips,
            (MusicDevice.Mt32 or MusicDevice.PcSpeaker, _) => Array.Empty<OplChipMode>(),
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
    }

    /// <summary>Gets the Miles driver profile for this device, chip and release selection.</summary>
    /// <exception cref="NotSupportedException">
    ///     The combination has no matching driver profile, or the release has no driver for it.
    /// </exception>
    public MusicProfile DriverProfile =>
        Revision switch
        {
            DriverRevision.Ail214 => Profile,
            DriverRevision.Ail1991 when Profile is MusicProfile.AdLib or MusicProfile.SoundBlaster
                or MusicProfile.SoundBlasterPro1 or MusicProfile.Mt32 or MusicProfile.PcSpeaker => Profile,
            _ => throw new NotSupportedException($"No supplied Miles {Revision} driver supports {Device} with {Chip}.")
        };

    /// <summary>Gets the AIL 2.14 driver profile for the device and chip selection.</summary>
    /// <exception cref="NotSupportedException">The device and chip combination has no matching driver profile.</exception>
    private MusicProfile Profile =>
        (Device, Chip) switch
        {
            (MusicDevice.AdLib, OplChipMode.Opl2) => MusicProfile.AdLib,
            (MusicDevice.SoundBlaster, OplChipMode.Opl2) => MusicProfile.SoundBlaster,
            (MusicDevice.SoundBlaster, OplChipMode.DualOpl2) => MusicProfile.SoundBlasterPro1,
            (MusicDevice.SoundBlaster, OplChipMode.Opl3) => MusicProfile.SoundBlasterPro2,
            (MusicDevice.ProAudioSpectrum, OplChipMode.DualOpl2) => MusicProfile.ProAudioSpectrum,
            (MusicDevice.ProAudioSpectrum, OplChipMode.Opl3) => MusicProfile.ProAudioSpectrumOpl3,
            (MusicDevice.AdLibGold, OplChipMode.Opl3) => MusicProfile.AdLibGold,
            (MusicDevice.Mt32, null) => MusicProfile.Mt32,
            (MusicDevice.PcSpeaker, null) => MusicProfile.PcSpeaker,
            _ => throw new NotSupportedException($"No supplied Miles profile supports {Device} with {Chip}.")
        };

    /// <summary>Converts a Miles driver profile to its device and chip selection.</summary>
    /// <param name="profile">The Miles music profile.</param>
    /// <returns>The equivalent selection; MT-32 and the PC speaker have a null chip.</returns>
    /// <exception cref="ArgumentOutOfRangeException">profile is not a defined MusicProfile value.</exception>
    public static MusicConfiguration FromDriverProfile(MusicProfile profile)
    {
        return profile switch
        {
            MusicProfile.AdLib => new MusicConfiguration(MusicDevice.AdLib, OplChipMode.Opl2),
            MusicProfile.SoundBlaster => new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl2),
            MusicProfile.SoundBlasterPro1 => new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.DualOpl2),
            MusicProfile.SoundBlasterPro2 => new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl3),
            MusicProfile.ProAudioSpectrum => new MusicConfiguration(MusicDevice.ProAudioSpectrum, OplChipMode.DualOpl2),
            MusicProfile.ProAudioSpectrumOpl3 => new MusicConfiguration(MusicDevice.ProAudioSpectrum, OplChipMode.Opl3),
            MusicProfile.AdLibGold => new MusicConfiguration(MusicDevice.AdLibGold, OplChipMode.Opl3),
            MusicProfile.Mt32 => new MusicConfiguration(MusicDevice.Mt32, null),
            MusicProfile.PcSpeaker => new MusicConfiguration(MusicDevice.PcSpeaker, null),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }
}
