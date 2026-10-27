using System.Buffers.Binary;
using System.Text;
using AIL2.Formats;
using AIL2.Parity;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]

namespace AIL2.Tests;

/// <summary>Authors portable resources and locates original-code oracle inputs for the test suite.</summary>
internal static class Fixtures
{
    /// <summary>Gets the checkout root containing the solution above the test output.</summary>
    /// <exception cref="DirectoryNotFoundException">No checkout root can be located.</exception>
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AIL2.NET.slnx")))
                {
                    return dir.FullName;
                }
            }

            throw new DirectoryNotFoundException("Run the suite from this checkout with its original fixtures.");
        }
    }

    /// <summary>Loads an authored bank for a music policy.</summary>
    /// <param name="profile">The policy selecting FM or MT-32 definitions.</param>
    /// <returns>A freshly parsed owned bank.</returns>
    public static TimbreBank Bank(MusicProfile profile)
    {
        return TimbreBank.Load(SyntheticResources.Bank(profile));
    }

    /// <summary>Creates the shared authored corpus once per test process.</summary>
    private static readonly Lazy<string> GeneratedDirectory = new(() =>
    {
        var directory = Path.Combine(Root, "artifacts/test-inputs");
        SyntheticResources.WriteDirectory(directory);
        return directory;
    });

    /// <summary>Resolves a filename in the authored test corpus.</summary>
    /// <param name="name">The generated resource basename.</param>
    /// <returns>The local generated path.</returns>
    public static string Resource(string name)
    {
        return Path.Combine(GeneratedDirectory.Value, name);
    }

    /// <summary>Frames test-authored events and optional branches as one XMIDI sequence.</summary>
    /// <param name="events">The EVNT bytes, including deliberately malformed cases.</param>
    /// <param name="branches">Marker identifiers and EVNT byte offsets.</param>
    /// <returns>The allocated IFF resource.</returns>
    public static byte[] Xmi(byte[] events, params (byte Marker, int Offset)[] branches)
    {
        var chunks = new List<byte>();
        if (branches.Length != 0)
        {
            var body = new byte[2 + (branches.Length * 6)];
            BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)branches.Length);
            for (var i = 0; i < branches.Length; i++)
            {
                body[2 + (i * 6)] = branches[i].Marker;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 + (i * 6)), branches[i].Offset);
            }

            chunks.AddRange(Chunk("RBRN", body));
        }

        chunks.AddRange(Chunk("EVNT", events));
        return Chunk("FORM", [.. "XMID"u8, .. chunks]);
    }

    /// <summary>Builds an IFF chunk with big-endian length and even padding.</summary>
    /// <param name="tag">The four-byte ASCII tag.</param>
    /// <param name="body">The chunk payload.</param>
    /// <returns>The framed chunk.</returns>
    private static byte[] Chunk(string tag, byte[] body)
    {
        var bytes = new byte[8 + body.Length + (body.Length & 1)];
        Encoding.ASCII.GetBytes(tag, bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), body.Length);
        body.CopyTo(bytes, 8);
        return bytes;
    }

    /// <summary>Frames caller-authored VOC blocks without validating their semantics.</summary>
    /// <param name="blocks">Ordered block type/payload pairs, including any terminator.</param>
    /// <returns>The allocated Creative Voice resource.</returns>
    public static byte[] Voc(params (byte Type, byte[] Bytes)[] blocks)
    {
        var bytes = new List<byte>("Creative Voice File\u001a"u8.ToArray());
        bytes.AddRange([26, 0, 10, 1, 41, 17]);
        foreach (var (type, body) in blocks)
        {
            bytes.Add(type);
            if (type == 0)
            {
                continue;
            }

            bytes.AddRange([(byte)body.Length, (byte)(body.Length >> 8), (byte)(body.Length >> 16)]);
            bytes.AddRange(body);
        }

        return bytes.ToArray();
    }

    /// <summary>Asserts exact event order, values and count with first-difference context.</summary>
    /// <param name="expected">The original-code trace.</param>
    /// <param name="actual">The managed trace.</param>
    public static void Equal(Trace expected, Trace actual)
    {
        var count = Math.Min(expected.Events.Count, actual.Events.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.True(expected.Events[i] == actual.Events[i],
                $"First differing event {i}:\noriginal {expected.Events[i]}\nmanaged  {actual.Events[i]}");
        }

        Assert.Equal(expected.Events.Count, actual.Events.Count);
    }
}

/// <summary>Runs identical actions against an original driver and a managed session.</summary>
internal sealed class MusicPair : IDisposable
{
    /// <summary>The original-code output trace.</summary>
    public Trace NativeTrace { get; } = new();

    /// <summary>The managed output trace.</summary>
    public Trace ManagedTrace { get; } = new();

    /// <summary>The owned original music oracle.</summary>
    public OriginalMusicDriver Native { get; }

    /// <summary>The owned managed session on the test thread.</summary>
    public AILSession Managed { get; }

    /// <summary>Initializes both paths and optionally installs the authored bank.</summary>
    /// <param name="profile">The original music policy to compare.</param>
    /// <param name="preload">Whether to install all authored bank entries on both paths.</param>
    public MusicPair(MusicProfile profile, bool preload = true)
    {
        Native = new OriginalMusicDriver(Fixtures.Root, profile, NativeTrace);
        Managed = new AILSession(profile, oplOutput: ManagedTrace, midiOutput: ManagedTrace, speakerOutput: ManagedTrace);
        if (preload)
        {
            var bank = Fixtures.Bank(profile);
            foreach (var key in bank.Keys)
            {
                bank.TryGet((byte)(key >> 8), (byte)key, out var bytes);
                Install((byte)(key >> 8), (byte)key, bytes);
            }

            Managed.Timbres = bank;
        }
    }

    /// <summary>Installs the same timbre on both paths.</summary>
    /// <param name="bank">The bank byte.</param>
    /// <param name="patch">The patch byte.</param>
    /// <param name="bytes">The length-prefixed definition.</param>
    public void Install(byte bank, byte patch, ReadOnlyMemory<byte> bytes)
    {
        Native.Install(bank, patch, bytes.Span);
        Managed.InstallTimbre(bank, patch, bytes);
    }

    /// <summary>Sends the same direct channel message on both paths.</summary>
    /// <param name="status">The MIDI status including channel.</param>
    /// <param name="d1">The first data byte.</param>
    /// <param name="d2">The second data byte, defaulting to zero.</param>
    public void Send(byte status, byte d1, byte d2 = 0)
    {
        Native.Call(186, 0, status, d1, d2);
        Managed.SendChannelMessage(status, d1, d2);
    }

    /// <summary>Services the original once and advances managed time to the same source deadline.</summary>
    /// <param name="tick">The monotonically increasing 8,333-microsecond service index.</param>
    public void Tick(int tick)
    {
        var time = TimeSpan.FromTicks(tick * 83330L);
        Native.Time = time;
        Native.Service();
        Managed.AdvanceTo(time);
    }

    /// <summary>Asserts that all accumulated output events are identical.</summary>
    public void Check()
    {
        Fixtures.Equal(NativeTrace, ManagedTrace);
    }

    /// <summary>Compares shutdown output and releases both owned playback paths.</summary>
    public void Dispose()
    {
        Native.Shutdown();
        Managed.Dispose();
        try
        {
            Check();
        }
        finally
        {
            Native.Dispose();
        }
    }
}
