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
    Mt32 = 4
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

/// <summary>Maps a music device and FM topology to the corresponding Miles driver profile.</summary>
/// <param name="Device">The music device family.</param>
/// <param name="Chip">The FM topology, or null for MT-32.</param>
/// <remarks>The pair is validated when DriverProfile is read.</remarks>
public readonly record struct MusicConfiguration(MusicDevice Device, OplChipMode? Chip)
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

    /// <summary>Lists the FM topologies available for a device family.</summary>
    /// <param name="device">The family whose chip choices are requested.</param>
    /// <returns>A read-only list in selector order, or an empty list for MT-32.</returns>
    /// <exception cref="ArgumentOutOfRangeException">device is not a defined MusicDevice value.</exception>
    public static IReadOnlyList<OplChipMode> SupportedChips(MusicDevice device)
    {
        return device switch
        {
            MusicDevice.AdLib => AdLibChips,
            MusicDevice.SoundBlaster => SoundBlasterChips,
            MusicDevice.ProAudioSpectrum => PasChips,
            MusicDevice.AdLibGold => GoldChips,
            MusicDevice.Mt32 => Array.Empty<OplChipMode>(),
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
    }

    /// <summary>Gets the Miles driver profile for this device and chip selection.</summary>
    /// <exception cref="NotSupportedException">The device and chip combination has no matching driver profile.</exception>
    public MusicProfile DriverProfile =>
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
            _ => throw new NotSupportedException($"No supplied Miles profile supports {Device} with {Chip}.")
        };

    /// <summary>Converts a Miles driver profile to its device and chip selection.</summary>
    /// <param name="profile">The Miles music profile.</param>
    /// <returns>The equivalent selection; MT-32 has a null chip.</returns>
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
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }
}
