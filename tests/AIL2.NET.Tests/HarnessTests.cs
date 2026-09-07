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

    /// <summary>Checks that absent resources, missing banks and invalid or ambiguous options fail explicitly.</summary>
    [Fact]
    public void EmptyMissingAmbiguousAndInvalidInputsCannotPass()
    {
        var empty = Path.Combine(Fixtures.Root, "artifacts", "empty-input-" + Guid.NewGuid());
        Directory.CreateDirectory(empty);
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
