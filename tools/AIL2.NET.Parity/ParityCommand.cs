using System.Globalization;
using System.Text.Json;
using AIL2.Formats;

namespace AIL2.Parity;

/// <summary>Resource-independent command line for generated scenarios and caller-supplied corpora.</summary>
public static class ParityCommand
{
    /// <summary>The authored finite sample scenarios used without external input.</summary>
    private static readonly string[] SampleFiles = ["effect.voc"];

    /// <summary>The authored finite, looping and multiple-sequence resources.</summary>
    private static readonly string[] MusicFiles = ["finite.xmi", "loop.xmi", "multiple.xmi"];

    /// <summary>Runs a generated or caller-supplied corpus, reporting failures without treating them as passes.</summary>
    /// <param name="args">The command-line arguments documented by --help.</param>
    /// <returns>Zero for successful nonempty comparisons or informational commands; one on failure.</returns>
    public static int Run(string[] args)
    {
        try
        {
            return Execute(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL: {error.Message}");
            return 1;
        }
    }

    /// <summary>Validates options, resolves resources and executes selected original/managed comparisons.</summary>
    /// <param name="args">The raw harness command-line arguments.</param>
    /// <returns>The comparison or informational-command exit code.</returns>
    /// <exception cref="ArgumentException">Options, profile inputs, bank selection or sequence indices are invalid.</exception>
    /// <exception cref="FileNotFoundException">A resource input does not exist.</exception>
    /// <exception cref="InvalidDataException">No matching resource or playable sample block is present.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Original and managed state differ or a scenario does not reach its stopping
    ///     condition.
    /// </exception>
    private static int Execute(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] flags = ["--help", "--samples"];
        string[] valued =
        [
            "--root", "--drivers", "--input", "--file", "--bank", "--bank-ad", "--bank-opl", "--bank-mt", "--profile",
            "--digital", "--sequence", "--marker", "--ticks", "--report", "--keep-going", "--generate", "--dump"
        ];
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!flags.Contains(key) && !valued.Contains(key))
            {
                throw new ArgumentException($"Unknown option {key}. Use --help.");
            }

            var value = "true";
            if (!flags.Contains(key))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for {key}.", nameof(args));
                }

                value = args[i];
            }

            if (!options.TryAdd(key, value))
            {
                throw new ArgumentException($"Repeated option {key}.");
            }
        }

        if (options.ContainsKey("--help"))
        {
            Console.WriteLine("""
                              AIL2.NET original-code parity (Unicorn 2; original REL105 WR drivers required)
                              No input: deterministic generated XMIDI/VOC/banks; all profiles by default.
                              --generate DIRECTORY       Write authored fixture resources and exit.
                              --input FILE|DIRECTORY     Compare an arbitrary file or recursively scan a directory.
                              --file FILE                Alias for a single input; --file all requires --input DIRECTORY.
                              --bank FILE                Bank for all selected music profiles.
                              --bank-ad FILE             Two-operator bank for OPL2/dual OPL2 profiles.
                              --bank-opl FILE            Bank for OPL3 profiles (two-operator banks also accepted).
                              --bank-mt FILE             MT-32 bank. External music inputs require explicit banks.
                              --profile all|NAME,...     MusicProfile enum names.
                              --samples                 Compare VOC resources instead of XMIDI.
                              --digital all|NAME,...     DigitalProfile enum names.
                              --sequence all|INDEX       All XMIDI sequences by default.
                              --marker NUMBER            Select a VOC marker (default start of resource).
                              --ticks COUNT              Music service failure budget (default 200000).
                              --drivers DIRECTORY        Original driver DLL directory.
                              --root DIRECTORY           Checkout/output root (default current directory).
                              --report NAME              Report basename below ROOT/artifacts/parity.
                              --keep-going true|false    Continue after failed cases (default false).
                              --dump FILE                Inspect an original LX image and exit.
                              Relative input/bank/driver paths resolve from the working directory.
                              """);
            return 0;
        }

        var root = Path.GetFullPath(Option("--root", Directory.GetCurrentDirectory()));
        if (options.TryGetValue("--generate", out var generated))
        {
            SyntheticResources.WriteDirectory(Path.GetFullPath(generated));
            return 0;
        }

        if (options.TryGetValue("--dump", out var dump))
        {
            var image = new LxImage(File.ReadAllBytes(dump));
            var folder = Path.Combine(root, "artifacts/inspect");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "loaded.bin"), image.Bytes);
            Console.WriteLine(JsonSerializer.Serialize(image.Functions));
            return 0;
        }

        var drivers = Path.GetFullPath(Option("--drivers", Path.Combine(root, "reference/REL105/RELEASE.105/WR")));
        var samples = options.ContainsKey("--samples");
        var keepGoing = bool.Parse(Option("--keep-going", "false"));
        var ticks = int.Parse(Option("--ticks", "200000"), CultureInfo.InvariantCulture);
        if (ticks <= 0)
        {
            throw new ArgumentException("--ticks must be positive.", nameof(args));
        }

        var input = options.GetValueOrDefault("--input");
        if (options.TryGetValue("--file", out var fileOption))
        {
            if (fileOption == "all")
            {
                if (input is null || !Directory.Exists(input))
                {
                    throw new ArgumentException("--file all requires --input DIRECTORY.");
                }
            }
            else
            {
                if (input is not null)
                {
                    throw new ArgumentException("Use either --input or --file for a single resource.");
                }

                input = fileOption;
            }
        }

        var synthetic = input is null;
        if (synthetic)
        {
            input = Path.Combine(root, "artifacts/generated-resources");
            SyntheticResources.WriteDirectory(input);
        }

        var extension = samples ? ".voc" : ".xmi";
        string[] files;
        if (synthetic)
        {
            files = (samples ? SampleFiles : MusicFiles).Select(name => Path.Combine(input!, name)).ToArray();
        }
        else if (Directory.Exists(input))
        {
            files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                .Where(p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray();
        }
        else if (File.Exists(input))
        {
            files = [Path.GetFullPath(input)];
        }
        else
        {
            throw new FileNotFoundException("Resource input does not exist.", input);
        }

        if (files.Length == 0)
        {
            throw new InvalidDataException($"No {extension} resources in {input}.");
        }

        var report = new CorpusReport(root, Option("--report", samples ? "sample-corpus" : "music-corpus"), args);
        var failures = 0;
        var count = 0;
        var totalBlocks = 0;
        if (samples)
        {
            ushort? marker = options.TryGetValue("--marker", out var markerText)
                ? ushort.Parse(markerText, CultureInfo.InvariantCulture)
                : null;
            foreach (var profile in Profiles<DigitalProfile>(Option("--digital", "all")))
            {
                foreach (var file in files)
                {
                    try
                    {
                        var blocks = SampleScenarios.Compare(root, profile, File.ReadAllBytes(file), marker, drivers);
                        if (blocks == 0)
                        {
                            throw new InvalidDataException(
                                "No sample blocks played. For a marked VOC, select its marker with --marker.");
                        }

                        totalBlocks += blocks;
                        count++;
                        report.Results.Add(new
                        {
                            profile = profile.ToString(), resource = report.Input(file),
                            driver = report.Input(Path.Combine(drivers, OriginalSampleDriver.FileName(profile))),
                            marker,
                            blocks, ending = "finite completion"
                        });
                        Console.WriteLine($"PASS {profile} {Path.GetFileName(file)}: {blocks} complete blocks.");
                    }
                    catch (Exception error)
                    {
                        Failure(profile.ToString(), file, error);
                        if (!keepGoing)
                        {
                            return Finish();
                        }
                    }
                }
            }

            Console.WriteLine($"{count} passing sample/profile pairs, {totalBlocks} complete blocks.");
            return Finish();
        }

        foreach (var profile in Profiles<MusicProfile>(Option("--profile", "all")))
        {
            foreach (var file in files)
            {
                try
                {
                    var bankPath = options.GetValueOrDefault("--bank") ??
                                   options.GetValueOrDefault("--bank-" + profile.BankExtension()[1..]);
                    if (bankPath is null && synthetic)
                    {
                        bankPath = Path.Combine(input!, "instruments" + profile.BankExtension());
                    }

                    if (bankPath is null)
                    {
                        throw new ArgumentException(
                            $"An explicit --bank or --bank-{profile.BankExtension()[1..]} is required for {profile}.");
                    }

                    bankPath = Path.GetFullPath(bankPath);
                    var data = File.ReadAllBytes(file);
                    var bank = TimbreBank.Load(File.ReadAllBytes(bankPath));
                    var xmidi = XmidiFile.Load(data);
                    var selection = Option("--sequence", "all");
                    var indices = selection == "all"
                        ? Enumerable.Range(0, xmidi.Sequences.Count).ToArray()
                        : [int.Parse(selection, CultureInfo.InvariantCulture)];
                    foreach (var index in indices)
                    {
                        if (index < 0 || index >= xmidi.Sequences.Count)
                        {
                            throw new ArgumentException("--sequence is outside the resource.", nameof(args));
                        }

                        var reference = new Trace();
                        var managed = new Trace();
                        using var original =
                            new OriginalMusicDriver(root, profile, reference, driverDirectory: drivers);
                        using var session = new AILSession(profile, oplOutput: managed, midiOutput: managed);
                        session.Timbres = bank;
                        original.Load(data, bank, index);
                        session.LoadTimbreBank(bank);
                        using var sequence = session.CreateSequence(xmidi, index);
                        original.Start();
                        sequence.Start();
                        var ending = "budget exhausted";
                        for (var tick = 1; tick <= ticks; tick++)
                        {
                            var time = TimeSpan.FromTicks(tick * 83_330L);
                            original.Time = time;
                            original.Service();
                            session.AdvanceTo(time);
                            if (original.Status != sequence.Status)
                            {
                                throw new InvalidOperationException(
                                    $"Status mismatch at service {tick}: {original.Status}/{sequence.Status}.");
                            }

                            if (original.EventOffset != sequence.EventOffset)
                            {
                                throw new InvalidOperationException(
                                    $"Event position mismatch at service {tick}: {original.EventOffset}/{sequence.EventOffset}.");
                            }

                            if (sequence.Status == SequenceStatus.Done)
                            {
                                ending = "finite completion";
                                break;
                            }

                            if (sequence.UnboundedLoopIterations >= 2)
                            {
                                ending = "two unbounded-loop iterations, explicit stop";
                                original.Call(171, 0, original.SequenceHandle);
                                sequence.Stop();
                                break;
                            }
                        }

                        if (ending == "budget exhausted")
                        {
                            throw new InvalidOperationException(
                                $"Scenario did not complete or reach two unbounded-loop iterations within {ticks} services.");
                        }

                        original.Shutdown();
                        session.Dispose();
                        Compare(root, reference, managed);
                        report.Results.Add(new
                        {
                            profile = profile.ToString(), resource = report.Input(file), sequence = index,
                            bank = report.Input(bankPath),
                            driver = report.Input(Path.Combine(drivers, OriginalMusicDriver.FileName(profile))),
                            events = reference.Events.Count, endTicks = session.Time.Ticks, ending
                        });
                        count++;
                        Console.WriteLine(
                            $"PASS {profile} {Path.GetFileName(file)} sequence {index}: {reference.Events.Count} identical events; {ending}.");
                    }
                }
                catch (Exception error)
                {
                    Failure(profile.ToString(), file, error);
                    if (!keepGoing)
                    {
                        return Finish();
                    }
                }
            }
        }

        return Finish();

        string Option(string key, string fallback)
        {
            return options.GetValueOrDefault(key, fallback);
        }

        void Failure(string profile, string file, Exception error)
        {
            failures++;
            report.Results.Add(new { profile, file, error = error.ToString() });
            Console.Error.WriteLine($"FAIL {profile} {Path.GetFileName(file)}: {error}");
        }

        int Finish()
        {
            var passed = failures == 0 && count > 0;
            report.Save(passed);
            return passed ? 0 : 1;
        }
    }

    /// <summary>Parses a distinct, validated profile selection.</summary>
    /// <typeparam name="T">The music or digital profile enum.</typeparam>
    /// <param name="text">The word all or comma-separated enum names.</param>
    /// <returns>The selected profiles in requested order.</returns>
    /// <exception cref="ArgumentException">The selection is empty or contains undefined enum values.</exception>
    private static T[] Profiles<T>(string text) where T : struct, Enum
    {
        var profiles = text == "all" ? Enum.GetValues<T>() : text.Split(',').Select(Enum.Parse<T>).ToArray();
        if (profiles.Length == 0 || profiles.Any(p => !Enum.IsDefined(p)))
        {
            throw new ArgumentException($"Invalid {typeof(T).Name} selection.");
        }

        return profiles.Distinct().ToArray();
    }

    /// <summary>Compares every event and retains full traces plus nearby context on the first mismatch.</summary>
    /// <param name="root">The workspace for local failure artifacts.</param>
    /// <param name="reference">The original-code output trace.</param>
    /// <param name="managed">The managed output trace.</param>
    /// <exception cref="InvalidOperationException">An event or event count differs.</exception>
    private static void Compare(string root, Trace reference, Trace managed)
    {
        var n = Math.Min(reference.Events.Count, managed.Events.Count);
        var first = Enumerable.Range(0, n).FirstOrDefault(i => reference.Events[i] != managed.Events[i], -1);
        if (first < 0 && reference.Events.Count != managed.Events.Count)
        {
            first = n;
        }

        if (first < 0)
        {
            return;
        }

        var directory = Path.Combine(root, "artifacts/parity");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "original.json"), JsonSerializer.Serialize(reference.Events));
        File.WriteAllText(Path.Combine(directory, "managed.json"), JsonSerializer.Serialize(managed.Events));
        var context = JsonSerializer.Serialize(new
        {
            first, original = reference.Events.Skip(Math.Max(0, first - 3)).Take(7),
            managed = managed.Events.Skip(Math.Max(0, first - 3)).Take(7)
        });
        throw new InvalidOperationException(
            $"Event {first} mismatch (counts {reference.Events.Count}/{managed.Events.Count}). Context: {context}");
    }
}
