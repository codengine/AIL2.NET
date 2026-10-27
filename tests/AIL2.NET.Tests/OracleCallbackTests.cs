using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>
///     Verifies that managed callbacks native oracle code keeps outlive a garbage collection. A collected callback
///     terminates the test host with "A callback was made on a garbage collected delegate".
/// </summary>
public sealed class OracleCallbackTests
{
    /// <summary>Collects every unreachable object, including delegates whose only reference is native.</summary>
    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Calls detect_device, which the sample oracle substitutes with a code hook, after a collection.</summary>
    [Fact]
    public void CodeHooksRunAfterACollection()
    {
        using var native = new OriginalSampleDriver(Fixtures.Root, DigitalProfile.SoundBlaster);
        Collect();
        Assert.Equal(1u, native.Call(101, 0));
    }

    /// <summary>Starts an effect, whose channel messages reach the music oracle through the shim's sink, after a collection.</summary>
    [Fact]
    public void SoundEffectSinkRunsAfterACollection()
    {
        using var driver = new OriginalMusicDriver(Fixtures.Root, MusicProfile.AdLib, new Trace());
        using var original = new OriginalSoundEffects(Fixtures.Root, driver);
        Collect();
        var handle = original.Invoke(1, 68, 60, 100, 64, 25);
        Assert.NotEqual(0, original.Invoke(3, handle));
    }
}
