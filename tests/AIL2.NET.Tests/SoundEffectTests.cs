using AIL2.Music;
using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>Compares MIDI effects against the compiled original SOUNDFX.C helper.</summary>
public sealed class SoundEffectTests
{
    /// <summary>Compares eight concurrent effects, source service periods, message order and channel release.</summary>
    /// <param name="profile">The original music policy receiving the helper's messages.</param>
    [Theory]
    [InlineData(MusicProfile.AdLib)]
    [InlineData(MusicProfile.SoundBlasterPro2)]
    [InlineData(MusicProfile.Mt32)]
    public void OriginalHelperDefinesMessageOrderDurationAndChannelRelease(MusicProfile profile)
    {
        using var pair = new MusicPair(profile);
        using var original = new OriginalSoundEffects(Fixtures.Root, pair.Native);
        Assert.Equal(10000, original.PeriodMicroseconds);
        var effects = new List<MidiEffect>();
        var handles = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            handles.Add(original.Invoke(1, 68, 60 + i, 100, 64, 25 + (i * 10)));
            effects.Add(pair.Managed.PlayEffect(68, (byte)(60 + i), 100, 64, TimeSpan.FromMilliseconds(25 + (i * 10))));
        }

        for (var tick = 1; tick <= 12; tick++)
        {
            var time = TimeSpan.FromMilliseconds(tick * 10);
            pair.Native.Time = time;
            original.Invoke(2);
            pair.Managed.AdvanceTo(time);
            for (var i = 0; i < 8; i++)
            {
                Assert.Equal(original.Invoke(3, handles[i]) != 0, effects[i].IsPlaying);
            }

            pair.Check();
        }
    }
}
