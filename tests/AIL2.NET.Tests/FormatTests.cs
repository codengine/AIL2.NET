using System.Buffers.Binary;
using AIL2.Formats;
using AIL2.Parity;
using AIL2.Rendering;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies malformed-resource rejection and managed lifetime/time boundaries.</summary>
public sealed class FormatTests
{
    /// <summary>Rejects every relevant truncation of minimal XMIDI/VOC resources and an invalid timbre offset.</summary>
    [Fact]
    public void EveryTruncationOfMinimalResourcesIsRejected()
    {
        var xmi = Fixtures.Xmi([0x91, 60, 100, 1, 2, 0xff, 0x2f, 0]);
        for (var i = 0; i < xmi.Length - 1; i++)
        {
            Assert.Throws<InvalidDataException>(() => XmidiFile.Load(xmi.AsSpan(0, i)));
        }

        var voc = Fixtures.Voc((1, [211, 0, 128]), (0, []));
        for (var i = 0; i < voc.Length; i++)
        {
            Assert.Throws<InvalidDataException>(() => VocFile.Load(voc.AsSpan(0, i)));
        }

        Assert.Throws<InvalidDataException>(() => TimbreBank.Load([1, 0, 255, 255, 255, 127, 0, 255]));
    }

    /// <summary>Rejects branches into event payloads and missing required timbre banks.</summary>
    [Fact]
    public void InvalidBranchAndMissingBankAreRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            XmidiFile.Load(Fixtures.Xmi([0x91, 60, 100, 1, 2, 0xff, 0x2f, 0], (0, 1))));
        using var renderer = new AILRenderer();
        var file = XmidiFile.Load(SyntheticResources.Song());
        using var sequence = renderer.Session.CreateSequence(file);
        Assert.NotNull(sequence.TimbreRequest);
        Assert.Throws<InvalidDataException>(sequence.PrepareTimbres);
    }

    /// <summary>Rejects zero-time XMIDI loops and attempts to move logical time backward.</summary>
    [Fact]
    public void ZeroTimeCyclesAreRejectedAndTimeCannotMoveBackward()
    {
        using var renderer = new AILRenderer();
        using var sequence =
            renderer.Session.CreateSequence(
                XmidiFile.Load(Fixtures.Xmi([0xb1, 116, 0, 0xb1, 117, 127, 0xff, 0x2f, 0])));
        sequence.Start();
        Assert.Throws<InvalidDataException>(() => renderer.Session.AdvanceTo(TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Session.AdvanceTo(TimeSpan.Zero));
    }

    /// <summary>Checks constructing-thread ownership and idempotent disposal.</summary>
    [Fact]
    public void OwningThreadAndDisposalAreEnforced()
    {
        var session = new AILSession();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                session.AdvanceTo(TimeSpan.Zero);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        session.AdvanceTo(TimeSpan.MaxValue);
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.AdvanceTo(TimeSpan.MaxValue));
    }

    /// <summary>Exercises deep iterative IFF traversal and rejects empty voice payloads.</summary>
    [Fact]
    public void DeepIffNestingUsesManagedWorkListAndEmptyVoiceBlocksAreRejected()
    {
        var inner = Fixtures.Xmi([0xff, 0x2f, 0]);
        const int containers = 20000;
        var bytes = new byte[(containers * 12) + inner.Length];
        inner.CopyTo(bytes, containers * 12);
        for (var at = 0; at < containers * 12; at += 12)
        {
            "CAT "u8.CopyTo(bytes.AsSpan(at));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 4), bytes.Length - at - 8);
            "XMID"u8.CopyTo(bytes.AsSpan(at + 8));
        }

        Assert.Single(XmidiFile.Load(bytes).Sequences);
        Assert.Throws<InvalidDataException>(() =>
            VocFile.Load(Fixtures.Voc((6, [255, 255]), (1, [211, 0]), (7, []), (0, []))));
    }
}
