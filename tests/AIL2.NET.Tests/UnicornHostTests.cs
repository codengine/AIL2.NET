using System.Diagnostics;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies which processes can run the Unicorn oracle, through the harness executable users start.</summary>
public sealed class UnicornHostTests
{
    /// <summary>
    ///     The harness built for this platform runs the original REL105 AdLib driver. On Windows this needs the
    ///     downloaded Unicorn library and an executable built without Control Flow Guard and CET shadow stacks.
    /// </summary>
    [Fact]
    public async Task TheParityExecutableRunsUnicorn()
    {
        var name = OperatingSystem.IsWindows() ? "AIL2.NET.Parity.exe" : "AIL2.NET.Parity";
        var (exitCode, output) = await RunParity(Path.Combine(AppContext.BaseDirectory, name));

        Assert.True(exitCode == 0, $"Exit code {exitCode}: {output}");
        Assert.Contains("PASS AdLib", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     dotnet.exe carries the Control Flow Guard flag, which Windows enforces by default, so the harness must refuse
    ///     to start Unicorn there instead of being terminated with 0xC0000409 and no output.
    /// </summary>
    [Fact]
    public async Task ADotnetHostedHarnessNamesControlFlowGuard()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Control Flow Guard exists only on Windows.");
        var (exitCode, output) = await RunParity("dotnet", Path.Combine(AppContext.BaseDirectory, "AIL2.NET.Parity.dll"));

        Assert.True(exitCode == 1, $"Exit code {exitCode}: {output}");
        Assert.Contains("Control Flow Guard", output, StringComparison.Ordinal);
    }

    /// <summary>Runs the harness's generated AdLib comparison in a scratch root and collects everything it prints.</summary>
    /// <param name="program">The executable to start.</param>
    /// <param name="prefix">Arguments placed before the harness options, such as the assembly for dotnet.</param>
    /// <returns>The exit code and the combined standard output and error.</returns>
    private static async Task<(int ExitCode, string Output)> RunParity(string program, params string[] prefix)
    {
        var root = Directory.CreateTempSubdirectory("ail2-unicorn-host-");
        try
        {
            var start = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in prefix.Concat([
                         "--root", root.FullName,
                         "--drivers", Path.Combine(Fixtures.Root, "reference/REL105/RELEASE.105/WR"),
                         "--profile", "AdLib"
                     ]))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var cancellation = TestContext.Current.CancellationToken;
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            var error = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
