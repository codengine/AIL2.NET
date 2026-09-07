using System.Text.Json;
using AIL2.Player;
using AIL2.Player.Audio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;

namespace AIL2.Tests;

/// <summary>Verifies audio-worker ownership, UI layout, export and native OpenAL integration.</summary>
public sealed class PlayerTests
{
    /// <summary>The authored looping music resource used by worker tests.</summary>
    private static string Music => Fixtures.Resource("loop.xmi");

    /// <summary>The authored two-operator bank used by worker tests.</summary>
    private static string Bank => Fixtures.Resource("instruments.ad");

    /// <summary>Checks serialized controls, resource replacement, stopped state and disposal.</summary>
    /// <returns>A task completing after worker lifecycle assertions.</returns>
    [Fact]
    public async Task WorkerSerializesReplacementPauseResumeAndDisposal()
    {
        var device = new FakeDevice();
        var worker = new PlaybackWorker(_ => device);
        await worker.LoadMusicAsync(Music, Bank, 0, MusicProfile.AdLib, DigitalProfile.SoundBlasterPro);
        await worker.PauseAsync();
        var before = await worker.SnapshotAsync();
        await worker.SetTempoAsync(125);
        var after = await worker.SnapshotAsync();
        Assert.Equal(before.Time, after.Time);
        Assert.True(after.Paused);
        await worker.ResumeAsync();
        Assert.False((await worker.SnapshotAsync()).Paused);
        await worker.LoadMusicAsync(Music, Bank, 0, MusicProfile.AdLib, DigitalProfile.SoundBlasterPro);
        await worker.PlaySampleAsync(Fixtures.Resource("effect.voc"),
            MusicProfile.AdLib,
            DigitalProfile.SoundBlasterPro);
        await worker.StopAsync();
        var stopped = await worker.SnapshotAsync();
        Assert.Equal(SequenceStatus.Stopped, stopped.MusicStatus);
        Assert.Equal(SampleStatus.Stopped, stopped.SampleStatus);
        await worker.DisposeAsync();
        await worker.DisposeAsync();
        Assert.True(device.Disposed);
        Assert.True(device.Owner > 0);
        await Assert.ThrowsAsync<ObjectDisposedException>(worker.SnapshotAsync);
    }

    /// <summary>Checks that device-open failure is visible and later startup can retry successfully.</summary>
    /// <returns>A task completing after failure and recovery assertions.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The deliberately failing device factory is invoked; the test expects this
    ///     failure.
    /// </exception>
    [Fact]
    public async Task DeviceFailureIsVisibleAndRetryIsPossible()
    {
        var fail = true;
        await using var worker = new PlaybackWorker(_ =>
            fail ? throw new InvalidOperationException("Fixture device unavailable") : new FakeDevice());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.LoadMusicAsync(Music, Bank, 0, MusicProfile.AdLib, DigitalProfile.SoundBlaster));
        Assert.Contains("Fixture device unavailable", (await worker.SnapshotAsync()).Error);
        fail = false;
        await worker.ResumeAsync();
        Assert.Null((await worker.SnapshotAsync()).Error);
    }

    /// <summary>Checks initialization capture and the stable MIDI JSON export shape without reflection serialization.</summary>
    /// <returns>A task completing after exported messages are read and checked.</returns>
    [Fact]
    public async Task Mt32InspectorIncludesInitializationAndExportsMessages()
    {
        await using var worker = new PlaybackWorker(_ => new FakeDevice());
        await worker.LoadMusicAsync(Music,
            Path.ChangeExtension(Bank, ".mt"),
            0,
            MusicProfile.Mt32,
            DigitalProfile.SoundBlaster);
        var messages = await worker.MidiAsync();
        Assert.NotEmpty(messages);
        Assert.StartsWith("F0411016127F", messages[0].Data);
        var path = Path.Combine(Fixtures.Root, "artifacts/test-midi.json");
        await worker.ExportMidiAsync(path);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var exported = json.RootElement.GetProperty("messages");
        Assert.Equal(messages.Length, exported.GetArrayLength());
        for (var i = 0; i < messages.Length; i++)
        {
            Assert.Equal(messages[i].Ticks, exported[i].GetProperty("Ticks").GetInt64());
            Assert.Equal(messages[i].Kind, exported[i].GetProperty("Kind").GetString());
            Assert.Equal(messages[i].Data, exported[i].GetProperty("Data").GetString());
        }
    }

    /// <summary>Checks that loop export finalizes on cancellation while live controls remain responsive.</summary>
    /// <returns>A task completing after the exported WAV size is checked.</returns>
    [Fact]
    public async Task LoopExportCanBeStoppedWhileLiveControlsRemainResponsive()
    {
        await using var worker = new PlaybackWorker(_ => new FakeDevice());
        await worker.LoadMusicAsync(Music, Bank, 0, MusicProfile.AdLib, DigitalProfile.SoundBlaster);
        using var stop = new CancellationTokenSource();
        var path = Path.Combine(Fixtures.Root, "artifacts/test-loop.wav");
        var export = worker.ExportWaveAsync(path, stop.Token);
        await worker.PauseAsync();
        Assert.True((await worker.SnapshotAsync()).Paused);
        await stop.CancelAsync();
        var frames = await export;
        Assert.Equal(80 + (frames * 4), new FileInfo(path).Length);
    }

    /// <summary>Checks bank replacement against a fresh renderer under the same OPL3 profile.</summary>
    /// <returns>A task completing after captured PCM comparisons.</returns>
    [Fact]
    public async Task ChangingBanksUnderTheSameProfileReplacesCachedInstruments()
    {
        var device = new CaptureDevice();
        await using var worker = new PlaybackWorker(_ => device);
        var song = Fixtures.Resource("finite.xmi");
        await worker.LoadMusicAsync(song,
            Path.ChangeExtension(Bank, ".opl"),
            0,
            MusicProfile.SoundBlasterPro2,
            DigitalProfile.SoundBlasterPro);
        var stock = await device.Capture(128).WaitAsync(TestContext.Current.CancellationToken);
        await worker.LoadMusicAsync(song, Bank, 0, MusicProfile.SoundBlasterPro2, DigitalProfile.SoundBlasterPro);
        var changed = await device.Capture(128).WaitAsync(TestContext.Current.CancellationToken);
        var cleanDevice = new CaptureDevice();
        await using var clean = new PlaybackWorker(_ => cleanDevice);
        await clean.LoadMusicAsync(song, Bank, 0, MusicProfile.SoundBlasterPro2, DigitalProfile.SoundBlasterPro);
        var expected = await cleanDevice.Capture(128).WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(stock.AsSpan().SequenceEqual(expected));
        Assert.Equal(expected, changed);
    }

    /// <summary>Exercises native library loading, queue limits, pause/resume, rendering and cleanup through OpenAL loopback.</summary>
    [Fact]
    public void OpenAlStreamingLifecycleRunsOnExplicitLoopbackDevice()
    {
        using var device = new OpenAlDevice(48000, true);
        Assert.True(device.CanWrite);
        var input = Enumerable.Repeat((short)1000, 1024).ToArray();
        device.Write(input);
        device.Write(input);
        device.Write(input);
        Assert.False(device.CanWrite);
        device.Pause();
        device.Resume();
        var rendered = new short[8192];
        device.RenderLoopback(rendered);
        Assert.Contains(rendered, sample => sample > 100);
        Assert.True(device.CanWrite);
        device.Write(input);
        device.Clear();
        Assert.True(device.CanWrite);
    }

    /// <summary>Checks nonoverlapping layout, fixed track-list buttons, chip selection and asynchronous window shutdown.</summary>
    [Fact]
    public void WindowCreatesAndClosesOnHeadlessDispatcher()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
                var worker = new PlaybackWorker(_ => new FakeDevice());
                var window = new MainWindow(worker);
                window.Show();
                window.Measure(new Size(1140, 810));
                window.Arrange(new Rect(0, 0, 1140, 810));
                Assert.True(window.IsVisible);
                var outer = Assert.IsType<Grid>(window.Content);
                var body = Assert.IsType<Grid>(outer.Children[1]);
                var library = Assert.IsType<Grid>(body.Children[0]);
                var playback = Assert.IsType<ScrollViewer>(body.Children[1]);
                Assert.True(playback.Bounds.Left >= library.Bounds.Right,
                    $"Library {library.Bounds} overlaps playback {playback.Bounds}.");
                Assert.True(library.Bounds.Width > 250 && playback.Bounds.Width > 350);
                var tracks = Assert.IsType<ListBox>(library.Children[4]);
                var buttons = library.Children[5];
                var beforeScroll = buttons.Bounds;
                tracks.ItemsSource = Enumerable.Range(0, 5000).ToArray();
                tracks.ScrollIntoView(4999);
                Dispatcher.UIThread.RunJobs();
                window.Measure(new Size(1140, 810));
                window.Arrange(new Rect(0, 0, 1140, 810));
                Assert.Equal(beforeScroll, buttons.Bounds);
                Assert.True(tracks.Bounds.Bottom <= buttons.Bounds.Top);
                var controls = Assert.IsType<StackPanel>(playback.Content);
                var bankBox = controls.Children.OfType<TextBox>().Single(c => c.Name == "TimbreBank");
                bankBox.Text = Bank;
                var deviceBox = controls.Children.OfType<ComboBox>().Single(c => c.Name == "MusicDevice");
                deviceBox.SelectedIndex = 1;
                var chipBox = controls.Children.OfType<ComboBox>().Single(c => c.Name == "OplChip");
                chipBox.SelectedIndex = 2;
                Assert.Equal("OPL3", chipBox.SelectedItem?.ToString());
                Assert.Equal(Bank, bankBox.Text);
                window.Close();
                var until = DateTime.UtcNow.AddSeconds(5);
                while (window.IsVisible && DateTime.UtcNow < until)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Yield();
                }

                Assert.False(window.IsVisible);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    /// <summary>Checks device-thread ownership without allowing the renderer to advance.</summary>
    private sealed class FakeDevice : IAudioDevice
    {
        /// <summary>The first calling thread's identifier.</summary>
        public int Owner { get; private set; }

        /// <summary>Whether the worker has disposed this device.</summary>
        public bool Disposed { get; private set; }

        /// <summary>Asserts consistent thread ownership and a live device.</summary>
        private void Check()
        {
            var id = Environment.CurrentManagedThreadId;
            if (Owner == 0)
            {
                Owner = id;
            }

            Assert.Equal(Owner, id);
            Assert.False(Disposed);
        }

        /// <inheritdoc />
        public bool CanWrite
        {
            get
            {
                Check();
                return false;
            }
        }

        /// <inheritdoc />
        public void Write(ReadOnlySpan<short> stereoFrames)
        {
            Check();
        }

        /// <inheritdoc />
        public void Pause()
        {
            Check();
        }

        /// <inheritdoc />
        public void Resume()
        {
            Check();
        }

        /// <inheritdoc />
        public void Clear()
        {
            Check();
        }

        /// <summary>Checks ownership and records worker disposal.</summary>
        public void Dispose()
        {
            Check();
            Disposed = true;
        }
    }

    /// <summary>Admits a requested number of render blocks and returns their copied PCM.</summary>
    private sealed class CaptureDevice : IAudioDevice
    {
        /// <summary>The captured interleaved stereo samples.</summary>
        private readonly List<short> _samples = [];

        /// <summary>Completes when the requested final block arrives.</summary>
        private TaskCompletionSource<short[]>? _completion;

        /// <summary>The remaining accepted block count, synchronized across caller and worker.</summary>
        private int _remaining;

        /// <summary>Arms capture after the previous request has completed.</summary>
        /// <param name="blocks">The number of render blocks to accept.</param>
        /// <returns>A detached sample array after the final block is written.</returns>
        public Task<short[]> Capture(int blocks)
        {
            Assert.Equal(0, Volatile.Read(ref _remaining));
            _samples.Clear();
            _completion = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _remaining, blocks);
            return _completion.Task;
        }

        /// <inheritdoc />
        public bool CanWrite => Volatile.Read(ref _remaining) > 0;

        /// <inheritdoc />
        public void Write(ReadOnlySpan<short> stereoFrames)
        {
            _samples.AddRange(stereoFrames);
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _completion!.SetResult(_samples.ToArray());
            }
        }

        /// <inheritdoc />
        public void Pause()
        {
        }

        /// <inheritdoc />
        public void Resume()
        {
        }

        /// <inheritdoc />
        public void Clear()
        {
        }

        /// <summary>Completes the device contract; captured arrays own their bytes.</summary>
        public void Dispose()
        {
        }
    }
}
