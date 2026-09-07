using AIL2.Formats;
using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies device/chip selection and instrument-bank independence.</summary>
public sealed class ConfigurationTests
{
    /// <summary>Checks that explicit Sound Blaster chip choices reach the expected driver and output topology.</summary>
    /// <param name="chip">The requested FM topology.</param>
    /// <param name="driver">The expected original music policy.</param>
    [Theory]
    [InlineData(OplChipMode.Opl2, MusicProfile.SoundBlaster)]
    [InlineData(OplChipMode.DualOpl2, MusicProfile.SoundBlasterPro1)]
    [InlineData(OplChipMode.Opl3, MusicProfile.SoundBlasterPro2)]
    public void EngineChoosesSoundBlasterAndChipIndependently(OplChipMode chip, MusicProfile driver)
    {
        var configuration = new MusicConfiguration(MusicDevice.SoundBlaster, chip);
        var capture = new Trace();
        using var session = new AILSession(configuration, DigitalProfile.AdLibGold, capture, sampleOutput: capture);
        Assert.Equal(configuration, session.MusicConfiguration);
        Assert.Equal(driver, session.MusicProfile);
        Assert.Equal(DigitalProfile.AdLibGold, session.DigitalProfile);
        Assert.Equal(chip == OplChipMode.Opl3, capture.Events.Any(e => e is { Kind: "opl", B: 0x105 }));
        Assert.Equal(chip == OplChipMode.DualOpl2, capture.Events.Any(e => e is { Kind: "opl", A: 1 }));
    }

    /// <summary>Checks that every original music policy round-trips through explicit configuration.</summary>
    [Fact]
    public void EveryOriginalProfileHasAnExplicitDeviceAndChipMapping()
    {
        foreach (var profile in Enum.GetValues<MusicProfile>())
        {
            Assert.Equal(profile, MusicConfiguration.FromDriverProfile(profile).DriverProfile);
        }
    }

    /// <summary>Checks that bank bytes depend on authored instruments rather than a filename or chip label.</summary>
    [Fact]
    public void BankContentIsIndependentOfFilenameAndChipSelection()
    {
        var bytes = SyntheticResources.Bank(MusicProfile.AdLib);
        Assert.Equal(bytes, SyntheticResources.Bank(MusicProfile.SoundBlasterPro2));
        Assert.False(bytes.AsSpan().SequenceEqual(SyntheticResources.Bank(MusicProfile.SoundBlasterPro2, 1)));
        var bank = TimbreBank.Load(bytes);
        foreach (var key in bank.Keys)
        {
            bank.TryGet((byte)(key >> 8), (byte)key, out var timbre);
            Assert.Equal(14, timbre.Length);
        }
    }
}
