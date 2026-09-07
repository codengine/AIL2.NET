using System.Security.Cryptography;
using System.Text.Json;

namespace AIL2.Parity;

/// <summary>Records local corpus identities, comparison results and oracle assumptions.</summary>
/// <param name="root">The workspace containing the local artifacts directory.</param>
/// <param name="name">The report basename without an extension.</param>
/// <param name="arguments">The exact harness arguments used for this run.</param>
internal sealed class CorpusReport(string root, string name, string[] arguments)
{
    /// <summary>Shared indented JSON settings for report serialization.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Gets the ordered scenario results, including generated or supplied input identities.</summary>
    public List<object> Results { get; } = [];

    /// <summary>Computes the identity of the exact input bytes.</summary>
    /// <param name="file">The local file to hash.</param>
    /// <returns>The lowercase SHA-256 hexadecimal digest.</returns>
    public static string Hash(string file)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    }

    /// <summary>Describes an input with its workspace-relative path and byte identity.</summary>
    /// <param name="file">The local input file.</param>
    /// <returns>The report's path and SHA-256 object.</returns>
    public object Input(string file)
    {
        return new { path = Path.GetRelativePath(root, file), sha256 = Hash(file) };
    }

    /// <summary>Writes the report and declared timing/oracle boundaries to local artifacts.</summary>
    /// <param name="passed">Whether every recorded comparison succeeded.</param>
    /// <exception cref="ArgumentException">The report name is not a simple basename.</exception>
    public void Save(bool passed)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
        {
            throw new ArgumentException("Report must be a basename.");
        }

        var directory = Path.Combine(root, "artifacts/parity");
        Directory.CreateDirectory(directory);
        var report = new
        {
            schema = "miles-parity/v1", passed, arguments, baseline = "AIL/32 REL105 WR",
            musicServiceTicks = 83330, effectServiceTicks = 100000, nominalMt32WaitTicks = 140000,
            mt32OracleRepair = "User-authorized POP EAX; NOP at loaded offset 0x989; untouched binary remains tested.",
            results = Results
        };
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(report, JsonOptions));
    }
}
