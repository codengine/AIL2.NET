using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AIL2.Formats;
using AIL2.Music;
using AIL2.Rendering;
using AIL2.Samples;

namespace AIL2.Player.Audio;

/// <summary>Contains one MIDI inspector entry that is safe to read outside the audio thread.</summary>
/// <param name="Ticks">The logical output timestamp in TimeSpan ticks.</param>
/// <param name="Kind">The entry kind: message or delay.</param>
/// <param name="Data">Uppercase hexadecimal message bytes, or an invariant decimal delay duration in ticks.</param>
public sealed record MidiRecord(long Ticks, string Kind, string Data);

/// <summary>Contains a snapshot of live playback and the last reported audio-worker error.</summary>
/// <param name="Time">The renderer session's logical time, or zero before loading.</param>
/// <param name="Paused">Whether the audio worker has suspended rendering.</param>
/// <param name="MusicStatus">The sequence status, or null without a sequence.</param>
/// <param name="SampleStatus">The sample status, or null without a sample.</param>
/// <param name="Error">The last worker error message, or null after successful playback starts.</param>
/// <param name="MidiMessages">The count of captured messages and pacing requests.</param>
public sealed record PlaybackSnapshot(
    TimeSpan Time,
    bool Paused,
    SequenceStatus? MusicStatus,
    SampleStatus? SampleStatus,
    string? Error,
    int MidiMessages);

/// <summary>Runs audio commands on one worker thread and returns snapshots and MIDI records that callers can read safely.</summary>
/// <remarks>
///     Public operations may be called from the UI thread. Returned tasks complete after queued work, and propagate
///     resource or device failures. The worker owns its renderer and audio device; WAV export owns a separate renderer and
///     thread.
/// </remarks>
public sealed class PlaybackWorker : IAsyncDisposable
{
    /// <summary>Commands serialized on the audio thread.</summary>
    private readonly BlockingCollection<Action> _commands = new();

    /// <summary>Creates an audio device that the worker will own for a requested output frame rate.</summary>
    private readonly Func<int, IAudioDevice> _deviceFactory;

    /// <summary>Stops exports when the worker is disposed.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Completes after the audio thread releases its renderer and device.</summary>
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Protects export state while an export starts or the worker is disposed.</summary>
    private readonly Lock _exportGate = new();

    /// <summary>The current or most recent independent WAV export.</summary>
    private Task<long>? _exportTask;

    /// <summary>The owned audio device, created lazily when playback starts.</summary>
    private IAudioDevice? _device;

    /// <summary>The renderer owned by the audio thread.</summary>
    private AILRenderer? _renderer;

    /// <summary>The live music handle, replaced on a new music selection.</summary>
    private XmidiSequence? _sequence;

    /// <summary>The live sample handle, replaced on a new effect selection.</summary>
    private SamplePlayback? _sample;

    /// <summary>The audio-thread MIDI log, including renderer initialization.</summary>
    private readonly MidiCapture _midi = new();

    /// <summary>Whether live rendering is suspended.</summary>
    private bool _paused = true;

    /// <summary>Interlocked flag used to reject commands after shutdown begins.</summary>
    private int _disposing;

    /// <summary>The last error, selected resource paths, and hash of the bank loaded into the current renderer.</summary>
    private string? _error, _musicFile, _bankFile, _sampleFile, _bankIdentity;

    /// <summary>Selected sequence index, nonnegative tempo percentage and 7-bit output volume.</summary>
    private int _sequenceIndex, _tempo = 100, _volume = 127;

    /// <summary>The music profile used by the current renderer.</summary>
    private MusicProfile _musicProfile = MusicProfile.AdLib;

    /// <summary>The sample profile used by the current renderer.</summary>
    private DigitalProfile _digitalProfile = DigitalProfile.SoundBlasterPro;

    /// <summary>Starts an audio command thread; the device is opened only when playback begins.</summary>
    /// <param name="deviceFactory">
    ///     An optional factory called on the worker with the frame rate in hertz. Returned devices are
    ///     owned and disposed by the worker; null selects OpenAL.
    /// </param>
    public PlaybackWorker(Func<int, IAudioDevice>? deviceFactory = null)
    {
        _deviceFactory = deviceFactory ?? (rate => new OpenAlDevice(rate));
        var thread = new Thread(Run) { IsBackground = true, Name = "AIL2.NET audio" };
        thread.Start();
    }

    /// <summary>Queues an operation and transfers its result or exception to the caller.</summary>
    /// <typeparam name="T">The result returned to the caller.</typeparam>
    /// <param name="action">The operation to execute on the audio thread.</param>
    /// <returns>A task completed by the queued operation, or faulted when disposal has begun.</returns>
    private Task<T> Invoke<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _disposing) != 0)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(PlaybackWorker)));
        }

        try
        {
            _commands.Add(() =>
            {
                try
                {
                    result.SetResult(action());
                }
                catch (Exception e)
                {
                    _error = e.Message;
                    result.SetException(e);
                }
            });
        }
        catch (InvalidOperationException)
        {
            result.TrySetException(new ObjectDisposedException(nameof(PlaybackWorker)));
        }

        return result.Task;
    }

    /// <summary>Queues a command without a result payload.</summary>
    /// <param name="action">The command to run on the audio thread.</param>
    /// <returns>A task that completes with true or propagates the command failure.</returns>
    private Task<bool> Invoke(Action action)
    {
        return Invoke(() =>
        {
            action();
            return true;
        });
    }

    /// <summary>Processes queued commands and available audio buffers, then releases the worker's resources.</summary>
    private void Run()
    {
        try
        {
            var buffer = new short[1024];
            while (!_commands.IsCompleted)
            {
                if (_commands.TryTake(out var command, 4))
                {
                    command();
                }

                if (_paused || _renderer is null || _device is null)
                {
                    continue;
                }

                try
                {
                    if (_device.CanWrite)
                    {
                        _renderer.Render(buffer);
                        _device.Write(buffer);
                    }
                }
                catch (Exception e)
                {
                    _error = e.Message;
                    _paused = true;
                    _device.Dispose();
                    _device = null;
                }
            }
        }
        catch (Exception e)
        {
            _finished.TrySetException(e);
        }
        finally
        {
            try
            {
                _renderer?.Dispose();
                _device?.Dispose();
            }
            catch (Exception e)
            {
                _finished.TrySetException(e);
            }

            _finished.TrySetResult();
        }
    }

    /// <summary>Recreates rendering state when profiles or instrument definitions change.</summary>
    /// <param name="music">The requested music profile.</param>
    /// <param name="digital">The requested sample profile.</param>
    /// <param name="reset">Whether a fresh timbre cache is required even with unchanged profiles.</param>
    private void EnsureRenderer(MusicProfile music, DigitalProfile digital, bool reset = false)
    {
        if (!reset && _renderer is not null && music == _musicProfile && digital == _digitalProfile)
        {
            return;
        }

        _device?.Clear();
        _renderer?.Dispose();
        _sequence = null;
        _sample = null;
        _musicFile = _bankFile = _sampleFile = _bankIdentity = null;
        _midi.Clear();
        _musicProfile = music;
        _digitalProfile = digital;
        _renderer = new AILRenderer(MusicConfiguration.FromDriverProfile(music), digital, midiObserver: _midi)
            { MasterVolume = _volume / 127.0 };
    }

    /// <summary>Opens the device if needed and enables live rendering.</summary>
    private void Play()
    {
        _device ??= _deviceFactory(48000);
        _error = null;
        _paused = false;
    }

    /// <summary>Loads a bank and XMIDI sequence, replacing live music and starting playback.</summary>
    /// <param name="path">The XMIDI file path.</param>
    /// <param name="bankPath">The selected instrument bank path.</param>
    /// <param name="sequenceIndex">The zero-based sequence index.</param>
    /// <param name="music">The music profile.</param>
    /// <param name="digital">The independently selected sample profile.</param>
    /// <returns>A task completing after loading and device startup.</returns>
    /// <remarks>
    ///     Changing the bank contents creates a fresh instrument cache. Profile changes replace both music and sample
    ///     state.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">sequenceIndex is outside the parsed resource.</exception>
    public Task LoadMusicAsync(string path,
        string bankPath,
        int sequenceIndex,
        MusicProfile music,
        DigitalProfile digital)
    {
        return Invoke(() =>
        {
            var file = XmidiFile.Load(File.ReadAllBytes(path));
            var bankBytes = File.ReadAllBytes(bankPath);
            var bank = TimbreBank.Load(bankBytes);
            var identity = Convert.ToHexString(SHA256.HashData(bankBytes));
            if ((uint)sequenceIndex >= file.Sequences.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
            }

            EnsureRenderer(music, digital, _bankIdentity is not null && _bankIdentity != identity);
            _device?.Clear();
            _sequence?.Dispose();
            _sequence = null;
            if (_bankIdentity != identity)
            {
                _renderer!.Session.LoadTimbreBank(bank);
                _bankIdentity = identity;
            }

            _sequence = _renderer!.Session.CreateSequence(file, sequenceIndex);
            _sequence.Start();
            _sequence.SetTempo(_tempo);
            _musicFile = path;
            _bankFile = bankPath;
            _sequenceIndex = sequenceIndex;
            Play();
        });
    }

    /// <summary>Loads and starts music for the selected device and chip.</summary>
    /// <param name="path">The XMIDI file path.</param>
    /// <param name="bankPath">The selected instrument bank path.</param>
    /// <param name="sequenceIndex">The zero-based sequence index.</param>
    /// <param name="music">The device and chip selection.</param>
    /// <param name="digital">The independently selected sample profile.</param>
    /// <returns>A task completing after loading and device startup.</returns>
    /// <exception cref="NotSupportedException">The device and chip combination has no matching driver profile.</exception>
    public Task LoadMusicAsync(string path,
        string bankPath,
        int sequenceIndex,
        MusicConfiguration music,
        DigitalProfile digital)
    {
        return LoadMusicAsync(path, bankPath, sequenceIndex, music.DriverProfile, digital);
    }

    /// <summary>Replaces the live sample and starts a VOC resource.</summary>
    /// <param name="path">The VOC file path.</param>
    /// <param name="music">The music profile retained for simultaneous playback.</param>
    /// <param name="digital">The sample profile.</param>
    /// <returns>A task completing after loading and device startup.</returns>
    /// <remarks>
    ///     Music remains active when profiles are unchanged. Compressed resources require an external consumer and are
    ///     rejected by this PCM player.
    /// </remarks>
    public Task PlaySampleAsync(string path, MusicProfile music, DigitalProfile digital)
    {
        return Invoke(() =>
        {
            var file = VocFile.Load(File.ReadAllBytes(path));
            EnsureRenderer(music, digital);
            _sample?.Dispose();
            _sample = _renderer!.Session.CreateSample(file);
            _sample.Play();
            _sampleFile = path;
            Play();
        });
    }

    /// <summary>Starts a VOC resource while retaining the selected music device and chip.</summary>
    /// <param name="path">The VOC file path.</param>
    /// <param name="music">The device and chip selection.</param>
    /// <param name="digital">The independently selected sample profile.</param>
    /// <returns>A task completing after loading and device startup.</returns>
    /// <exception cref="NotSupportedException">The device and chip combination has no matching driver profile.</exception>
    public Task PlaySampleAsync(string path, MusicConfiguration music, DigitalProfile digital)
    {
        return PlaySampleAsync(path, music.DriverProfile, digital);
    }

    /// <summary>Suspends device consumption and logical rendering together.</summary>
    /// <returns>A task completing after the pause is applied.</returns>
    public Task PauseAsync()
    {
        return Invoke(() =>
        {
            _device?.Pause();
            _paused = true;
        });
    }

    /// <summary>Resumes live rendering and device consumption when a renderer is loaded.</summary>
    /// <returns>A task completing after resumption or a device-open failure.</returns>
    public Task ResumeAsync()
    {
        return Invoke(() =>
        {
            if (_renderer is not null)
            {
                Play();
                _device?.Resume();
            }
        });
    }

    /// <summary>Stops both playback handles and discards queued device audio.</summary>
    /// <returns>A task completing after the stop is applied.</returns>
    public Task StopAsync()
    {
        return Invoke(() =>
        {
            _paused = true;
            _device?.Clear();
            _sequence?.Stop();
            _sample?.Stop();
        });
    }

    /// <summary>Sets the live and future sequence tempo percentage.</summary>
    /// <param name="percent">The nonnegative percentage; 100 is nominal and zero suspends events.</param>
    /// <returns>A task completing after the setting is applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">percent is negative.</exception>
    public Task SetTempoAsync(int percent)
    {
        return Invoke(() =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(percent);
            _tempo = percent;
            _sequence?.SetTempo(percent);
        });
    }

    /// <summary>Sets the final output gain for live and future rendering.</summary>
    /// <param name="volume">The linear gain numerator from 0 through 127, divided by 127.</param>
    /// <returns>A task completing after the gain is applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">volume is outside 0 through 127.</exception>
    public Task SetVolumeAsync(int volume)
    {
        return Invoke(() =>
        {
            if ((uint)volume > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }

            _volume = volume;
            _renderer?.MasterVolume = volume / 127.0;
        });
    }

    /// <summary>Captures live status without exposing mutable audio-thread state.</summary>
    /// <returns>A status snapshot created on the audio thread.</returns>
    public Task<PlaybackSnapshot> SnapshotAsync()
    {
        return Invoke(() => new PlaybackSnapshot(_renderer?.Session.Time ?? TimeSpan.Zero,
            _paused,
            _sequence?.Status,
            _sample?.Status,
            _error,
            _midi.Messages.Count));
    }

    /// <summary>Copies the complete MIDI inspector log.</summary>
    /// <returns>A copied array in original output order, including pacing entries.</returns>
    public Task<MidiRecord[]> MidiAsync()
    {
        return Invoke(() => _midi.Messages.ToArray());
    }

    /// <summary>Writes the complete MIDI inspector log as indented UTF-8 JSON.</summary>
    /// <param name="path">The destination file, created or overwritten.</param>
    /// <returns>A task completing after the file is written and closed.</returns>
    public Task ExportMidiAsync(string path)
    {
        return Invoke(() =>
        {
            using var file = File.Create(path);
            using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteStartArray("messages");
            foreach (var record in _midi.Messages)
            {
                writer.WriteStartObject();
                writer.WriteNumber(nameof(MidiRecord.Ticks), record.Ticks);
                writer.WriteString(nameof(MidiRecord.Kind), record.Kind);
                writer.WriteString(nameof(MidiRecord.Data), record.Data);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>Exports the loaded resources from their beginning on an independent renderer.</summary>
    /// <param name="path">The WAV destination, created or overwritten.</param>
    /// <param name="stop">Stops and finalizes a playable partial export, including looping resources.</param>
    /// <returns>The number of complete stereo frames written.</returns>
    /// <remarks>
    ///     Captures profiles, tempo and gain before export; live controls remain responsive. Files are reread at export
    ///     start. Disposal also requests a finalized stop.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No resource is loaded or another export is still running.</exception>
    /// <exception cref="NotSupportedException">The loaded music uses MT-32 external output.</exception>
    /// <exception cref="ObjectDisposedException">Worker disposal has begun.</exception>
    public async Task<long> ExportWaveAsync(string path, CancellationToken stop = default)
    {
        var options = await Invoke(() =>
        {
            if (_musicFile is null && _sampleFile is null)
            {
                throw new InvalidOperationException("Load music or a sample before exporting a WAV.");
            }

            if (_musicFile is not null && _musicProfile == MusicProfile.Mt32)
            {
                throw new NotSupportedException(
                    "MT-32 produces external MIDI messages. Select an FM profile for WAV export.");
            }

            return (Music: _musicFile is null ? null : XmidiFile.Load(File.ReadAllBytes(_musicFile)),
                Bank: _bankFile is null ? null : TimbreBank.Load(File.ReadAllBytes(_bankFile)),
                Sample: _sampleFile is null ? null : VocFile.Load(File.ReadAllBytes(_sampleFile)),
                MusicProfile: _musicProfile, DigitalProfile: _digitalProfile, Index: _sequenceIndex, Tempo: _tempo,
                Volume: _volume / 127.0);
        }).ConfigureAwait(false);
        Task<long> task;
        lock (_exportGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
            if (_exportTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("An export is already running.");
            }

            task = _exportTask = Task.Run(() =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop, _shutdown.Token);
                using var renderer = new AILRenderer(options.MusicProfile, options.DigitalProfile);
                if (options.Bank is not null)
                {
                    renderer.Session.LoadTimbreBank(options.Bank);
                }

                using var sequence = options.Music is null
                    ? null
                    : renderer.Session.CreateSequence(options.Music, options.Index);
                sequence?.Start();
                sequence?.SetTempo(options.Tempo);
                renderer.MasterVolume = options.Volume;
                using var sample = options.Sample is null ? null : renderer.Session.CreateSample(options.Sample);
                sample?.Play();
                using var file = File.Create(path);
                return WaveExporter.WriteToEnd(file,
                    renderer,
                    () => sequence?.Status == SequenceStatus.Playing || sample?.Status == SampleStatus.Playing,
                    cancellation.Token);
            },
            stop);
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>Rejects new commands, requests export completion, and waits for the audio thread to release its device.</summary>
    /// <returns>A task that completes after the audio thread and any active export finish.</returns>
    /// <remarks>Repeated calls await the same work. Device and export failures propagate to the caller.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 0)
        {
            await _shutdown.CancelAsync();
            _commands.CompleteAdding();
        }

        await _finished.Task.ConfigureAwait(false);
        Task<long>? export;
        lock (_exportGate)
        {
            export = _exportTask;
        }

        if (export is not null)
        {
            await export.ConfigureAwait(false);
        }
    }

    /// <summary>Collects MIDI inspector entries on the audio thread and exposes them through copies.</summary>
    private sealed class MidiCapture : IMidiOutput
    {
        /// <summary>The complete ordered log; entries are not discarded.</summary>
        public readonly List<MidiRecord> Messages = [];

        /// <summary>Appends an inspector entry whose data no longer refers to callback memory.</summary>
        /// <param name="message">The message or delay entry.</param>
        private void Add(MidiRecord message)
        {
            Messages.Add(message);
        }

        /// <inheritdoc />
        public void SendMessage(TimeSpan time, ReadOnlySpan<byte> message)
        {
            Add(new MidiRecord(time.Ticks, "message", Convert.ToHexString(message)));
        }

        /// <inheritdoc />
        public void Delay(TimeSpan time, TimeSpan duration)
        {
            Add(new MidiRecord(time.Ticks, "delay", duration.Ticks.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>Clears entries when the renderer and its initialization state are replaced.</summary>
        public void Clear()
        {
            Messages.Clear();
        }
    }
}
