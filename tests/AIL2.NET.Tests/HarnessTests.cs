using System.Security.Cryptography;
using System.Text.Json;
using AIL2.Parity;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies portable harness inputs and explicit failure for empty or invalid scenarios.</summary>
public sealed class HarnessTests
{
    /// <summary>
    ///     Exercises renamed resources, independent banks, explicit drivers and every sequence outside a checkout output
    ///     root.
    /// </summary>
    [Fact]
    public void ArbitraryPathsBanksDriversAndAllSequencesWorkWithoutACheckoutAtOutputRoot()
    {
        var folder = Path.Combine(Fixtures.Root, "artifacts", "external-input-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var music = Path.Combine(folder, "renamed resource.data");
        File.WriteAllBytes(music, SyntheticResources.MultipleSequences());
        var bank = Path.Combine(folder, "independent bank.data");
        File.WriteAllBytes(bank, SyntheticResources.Bank(MusicProfile.AdLib));
        var root = Path.Combine(folder, "output");
        var drivers = Path.Combine(Fixtures.Root, "reference/REL105/RELEASE.105/WR");
        Assert.Equal(0,
            ParityCommand.Run([
                "--root", root, "--drivers", drivers, "--input", music, "--profile", "SoundBlaster,SoundBlasterPro2",
                "--bank", bank
            ]));
        using var report =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts/parity/music-corpus.json")));
        Assert.True(report.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(4, report.RootElement.GetProperty("results").GetArrayLength());
        var nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "unrelated name.VOC"), SyntheticResources.Sample());
        Assert.Equal(0, ParityCommand.Run(["--root", root, "--drivers", drivers, "--samples", "--input", folder]));
        using var samples =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts/parity/sample-corpus.json")));
        foreach (var result in samples.RootElement.GetProperty("results").EnumerateArray())
        {
            Assert.True(result.GetProperty("blocks").GetInt32() > 0);
        }
    }

    /// <summary>Ensures a marked VOC needs a playable marker selection and cannot pass with zero blocks.</summary>
    [Fact]
    public void MarkedVocRequiresAPlayableSelectionAndCannotProduceAnEmptyPass()
    {
        var root = Path.Combine(Fixtures.Root, "artifacts", "marked-input-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "marked.voc");
        File.WriteAllBytes(file, SyntheticResources.Voc((4, [7, 0]), (1, [211, 0, 128, 255]), (0, [])));
        string[] arguments =
        [
            "--root", root, "--drivers", Path.Combine(Fixtures.Root, "reference/REL105/RELEASE.105/WR"), "--samples",
            "--input", file
        ];
        Assert.Equal(1, ParityCommand.Run(arguments));
        Assert.Equal(0, ParityCommand.Run([.. arguments, "--marker", "7"]));
    }

    /// <summary>Compares an external resource on the PC speaker without a bank, because its driver uses no timbres.</summary>
    [Fact]
    public void SpeakerComparisonsNeedNoBank()
    {
        var root = Path.Combine(Fixtures.Root, "artifacts", "speaker-input-" + Guid.NewGuid());
        Assert.Equal(0,
            ParityCommand.Run([
                "--root", root, "--drivers", Path.Combine(Fixtures.Root, "reference/REL105/RELEASE.105/WR"), "--input",
                Fixtures.Resource("finite.xmi"), "--profile", "PcSpeaker"
            ]));
        using var report =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts/parity/music-corpus.json")));
        var result = report.RootElement.GetProperty("results")[0];
        Assert.Equal(JsonValueKind.Null, result.GetProperty("bank").ValueKind);
        Assert.True(result.GetProperty("events").GetInt32() > 0);
    }

    /// <summary>Compares through the real-mode driver --adv names and reports it, not a REL105 module, as the oracle.</summary>
    [Fact]
    public void AdvComparesAgainstTheNamedRealModeDriver()
    {
        var root = Path.Combine(Fixtures.Root, "artifacts", "adv-input-" + Guid.NewGuid());
        var driver = Path.Combine(Fixtures.Root, "reference/A214_D1/ADLIB.ADV");
        Assert.Equal(0, ParityCommand.Run(["--root", root, "--adv", driver, "--profile", "AdLib"]));
        using var report =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts/parity/music-corpus.json")));
        Assert.Equal("AIL 2.x real-mode .ADV driver", report.RootElement.GetProperty("baseline").GetString());
        var results = report.RootElement.GetProperty("results");
        Assert.NotEqual(0, results.GetArrayLength());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(driver))).ToLowerInvariant();
        foreach (var result in results.EnumerateArray())
        {
            Assert.Equal(hash, result.GetProperty("driver").GetProperty("sha256").GetString());
        }
    }

    /// <summary>Checks that absent resources, missing banks and invalid or ambiguous options fail explicitly.</summary>
    [Fact]
    public void EmptyMissingAmbiguousAndInvalidInputsCannotPass()
    {
        var empty = Path.Combine(Fixtures.Root, "artifacts", "empty-input-" + Guid.NewGuid());
        Directory.CreateDirectory(empty);
        var adlib = Path.Combine(Fixtures.Root, "reference/A214_D1/ADLIB.ADV");
        var effect = Path.Combine(empty, "effect.voc");
        File.WriteAllBytes(effect, SyntheticResources.Sample());
        // Each call would pass if the option combination were accepted and one of the two options ignored.
        Assert.Equal(1,
            ParityCommand.Run([
                "--root", Fixtures.Root, "--report", "rejected-" + Guid.NewGuid(), "--adv", adlib, "--samples",
                "--input", effect
            ]));
        Assert.Equal(1,
            ParityCommand.Run([
                "--root", empty, "--adv", adlib, "--profile", "AdLib", "--drivers",
                Path.Combine(Fixtures.Root, "reference/REL105/RELEASE.105/WR")
            ]));
        Assert.Equal(1, ParityCommand.Run(["--input", empty]));
        Assert.Equal(1, ParityCommand.Run(["--input", Path.Combine(empty, "missing.xmi")]));
        Assert.Equal(1,
            ParityCommand.Run(["--input", Fixtures.Resource("finite.xmi"), "--profile", "AdLib", "--root", empty]));
        Assert.Equal(1, ParityCommand.Run(["--input", empty, "--file", "another.xmi"]));
        Assert.Equal(1, ParityCommand.Run(["--file", "all"]));
        Assert.Equal(1, ParityCommand.Run(["--unknown"]));
        Assert.Equal(1, ParityCommand.Run(["--profile"]));
        Assert.Equal(1, ParityCommand.Run(["--ticks", "0"]));
    }
}
