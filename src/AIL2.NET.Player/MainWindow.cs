using AIL2.Formats;
using AIL2.Player.Audio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AIL2.Player;

/// <summary>Browses music and sound resources and controls them through one audio worker.</summary>
public sealed class MainWindow : Window
{
    /// <summary>The worker disposed asynchronously when this window closes.</summary>
    private readonly PlaybackWorker _worker;

    /// <summary>The music device-family selector; the PC speaker is absent because the renderer cannot play it.</summary>
    private readonly ComboBox _music = new()
    {
        Name = "MusicDevice",
        ItemsSource = Enum.GetValues<MusicDevice>().Where(device => device != MusicDevice.PcSpeaker)
            .Select(device => new DeviceChoice(device)).ToArray(),
        SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>The FM topology selector filtered by the selected device family.</summary>
    private readonly ComboBox _chip = new() { Name = "OplChip", HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>The sample-profile selector, independent of music.</summary>
    private readonly ComboBox _digital = new()
    {
        ItemsSource = Enum.GetValues<DigitalProfile>(), SelectedIndex = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>The scrollable list of discovered resources.</summary>
    private readonly ListBox _files = new();

    /// <summary>Resource folder, selected instrument bank and filename filter inputs.</summary>
    private readonly TextBox _folder = new(),
        _bank = new() { Name = "TimbreBank" },
        _filter = new() { Watermark = "Filter music and effects…" };

    /// <summary>Operation feedback and the currently loaded resource name.</summary>
    private readonly TextBlock _status =
            new() { Text = "Choose a folder or open a music file.", TextWrapping = TextWrapping.Wrap },
        _now = new() { Text = "Ready", FontSize = 25 };

    /// <summary>The complete MT-32 message and pacing inspector.</summary>
    private readonly TextBox _messages = new()
    {
        IsReadOnly = true, AcceptsReturn = true, MinHeight = 150, FontFamily = FontFamily.Default,
        Watermark = "MT-32 messages appear here. Synthesis and transport are supplied by an external host."
    };

    /// <summary>The sequence index, bounded by the selected resource, and the nonnegative tempo percentage.</summary>
    private readonly NumericUpDown _index = new() { Minimum = 0, Maximum = 0, Value = 0 },
        _tempo = new() { Minimum = 0, Maximum = int.MaxValue, Value = 100, Increment = 1 };

    /// <summary>The final output gain numerator, divided by 127.</summary>
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 127, Value = 127 };

    /// <summary>Cancels the current WAV export.</summary>
    private Action? _stopExport;

    /// <summary>Polls playback snapshots on the UI dispatcher.</summary>
    private readonly DispatcherTimer _timer;

    /// <summary>The complete scanned path list before filename filtering.</summary>
    private string[] _paths = [];

    /// <summary>Guards against overlapping polling and repeated asynchronous shutdown.</summary>
    private bool _polling, _closing, _closed;

    /// <summary>Creates the player with its default audio worker.</summary>
    public MainWindow() : this(new PlaybackWorker())
    {
    }

    /// <summary>Creates the player controls on the Avalonia UI thread.</summary>
    /// <param name="worker">The audio worker. The window takes ownership and disposes it when closed.</param>
    public MainWindow(PlaybackWorker worker)
    {
        _worker = worker;
        Title = "AIL2.NET player";
        Width = 1140;
        Height = 810;
        MinWidth = 800;
        MinHeight = 620;
        var outer = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var heading = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 22) };
        heading.Children.Add(new TextBlock
            { Text = "AIL2.NET", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.Turquoise });
        heading.Children.Add(new TextBlock
            { Text = "Music & sound workshop", FontSize = 30, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock
            { Text = "XMIDI · OPL2 / OPL3 · MT-32 output · VOC effects", Opacity = .65 });
        outer.Children.Add(heading);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), ColumnSpacing = 24 };
        Grid.SetRow(body, 1);
        outer.Children.Add(body);
        var library = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 10 };
        library.Children.Add(Label("RESOURCE LIBRARY"));
        library.Children.Add(_folder);
        library.Children.Add(Row(Button("Folder…", ChooseFolder),
            Button("Open file…", OpenFile),
            Button("Scan", Scan)));
        library.Children.Add(_filter);
        library.Children.Add(_files);
        library.Children.Add(Row(Button("Play selected", PlaySelected), Button("Use as bank", UseBank)));
        for (var row = 0; row < library.Children.Count; row++)
        {
            Grid.SetRow(library.Children[row], row);
        }

        body.Children.Add(library);
        var playback = new StackPanel { Spacing = 12 };
        var playbackScroll = new ScrollViewer { Content = playback };
        Grid.SetColumn(playbackScroll, 1);
        body.Children.Add(playbackScroll);
        playback.Children.Add(_now);
        playback.Children.Add(Label("Music device / profile"));
        playback.Children.Add(_music);
        playback.Children.Add(Label("OPL chip mode"));
        playback.Children.Add(_chip);
        playback.Children.Add(Label("Digital profile"));
        playback.Children.Add(_digital);
        playback.Children.Add(Label("Instrument bank"));
        playback.Children.Add(_bank);
        playback.Children.Add(Button("Choose bank…", ChooseBank));
        playback.Children.Add(Row(Label("Sequence"), _index, Button("Load / play", PlaySelected)));
        playback.Children.Add(Row(Button("Pause", _worker.PauseAsync),
            Button("Resume", _worker.ResumeAsync),
            Button("Stop", _worker.StopAsync)));
        playback.Children.Add(Label("Tempo (%)"));
        playback.Children.Add(_tempo);
        playback.Children.Add(Label("Output volume"));
        playback.Children.Add(_volume);
        playback.Children.Add(Row(Button("Save WAV…", ExportWave), Button("Stop export", StopExport)));
        playback.Children.Add(Label("MT-32 · external output"));
        playback.Children.Add(_messages);
        playback.Children.Add(Button("Export messages…", ExportMidi));
        Grid.SetRow(_status, 2);
        _status.Margin = new Thickness(0, 18, 0, 0);
        outer.Children.Add(_status);
        Content = outer;
        _filter.TextChanged += (_, _) => Filter();
        _files.SelectionChanged += (_, _) => Select();
        _music.SelectionChanged += (_, _) => UpdateChips();
        _chip.SelectionChanged += (_, _) => SuggestBank();
        UpdateChips();
        _tempo.ValueChanged += async (_, _) => await Guard(() => _worker.SetTempoAsync((int)(_tempo.Value ?? 100)));
        _volume.ValueChanged += async (_, _) => await Guard(() => _worker.SetVolumeAsync((int)_volume.Value));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
        Closing += async (_, e) =>
        {
            if (_closed)
            {
                return;
            }

            e.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            _timer.Stop();
            await Guard(async () => await _worker.DisposeAsync());
            _closed = true;
            Close();
        };
    }

    /// <summary>Creates a secondary label with the standard alignment.</summary>
    /// <param name="text">The displayed label text.</param>
    /// <returns>The configured label.</returns>
    private static TextBlock Label(string text)
    {
        return new TextBlock { Text = text, Opacity = .7, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Groups related controls in a horizontal row.</summary>
    /// <param name="controls">The controls in display order.</param>
    /// <returns>The populated row.</returns>
    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls)
        {
            row.Children.Add(control);
        }

        return row;
    }

    /// <summary>Creates an asynchronous action button that reports failures in the status line.</summary>
    /// <param name="text">The button caption.</param>
    /// <param name="action">The operation to run on click.</param>
    /// <returns>The configured button.</returns>
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await Guard(action);
        return button;
    }

    /// <summary>Runs an operation and presents any failure in the status line.</summary>
    /// <param name="action">The asynchronous operation.</param>
    /// <returns>A task completing after success or error presentation.</returns>
    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            _status.Text = e.Message;
        }
    }

    /// <summary>The selected music family, falling back to AdLib before selection.</summary>
    private MusicDevice Device => (_music.SelectedItem as DeviceChoice)?.Device ?? MusicDevice.AdLib;

    /// <summary>The device and chip selection passed to the library.</summary>
    private MusicConfiguration Configuration => new(Device, (_chip.SelectedItem as ChipChoice)?.Chip);

    /// <summary>The original driver profile resolved from the current selection.</summary>
    private MusicProfile Music => Configuration.DriverProfile;

    /// <summary>The independently selected sample profile.</summary>
    private DigitalProfile Digital => (DigitalProfile)(_digital.SelectedItem ?? DigitalProfile.SoundBlasterPro);

    /// <summary>The full path of the selected resource, or null.</summary>
    private string? Selected => (_files.SelectedItem as ResourceItem)?.Path;

    /// <summary>Chooses a folder and scans its resources.</summary>
    /// <returns>A task completing after selection and scanning.</returns>
    private async Task ChooseFolder()
    {
        var choices = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose resource folder" });
        if (choices.Count != 0)
        {
            _folder.Text = choices[0].TryGetLocalPath();
            await Scan();
        }
    }

    /// <summary>Chooses an XMIDI or VOC file and selects it in the resource list.</summary>
    /// <returns>A task completing after selection and folder scanning.</returns>
    private async Task OpenFile()
    {
        var choices = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open XMIDI or VOC",
            FileTypeFilter = [new FilePickerFileType("AIL resources") { Patterns = ["*.xmi", "*.voc"] }]
        });
        if (choices.Count == 0 || choices[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        _folder.Text = Path.GetDirectoryName(path);
        await Scan();
        _filter.Text = "";
        _files.SelectedItem = _files.Items.OfType<ResourceItem>().FirstOrDefault(item => item.Path == path);
    }

    /// <summary>Chooses the instrument bank used for music playback.</summary>
    /// <returns>A task completing when the dialog closes.</returns>
    private async Task ChooseBank()
    {
        var choices = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose timbre bank",
            FileTypeFilter = [new FilePickerFileType("AIL banks") { Patterns = ["*.ad", "*.opl", "*.mt"] }]
        });
        if (choices.Count != 0)
        {
            _bank.Text = choices[0].TryGetLocalPath();
        }
    }

    /// <summary>Enumerates supported resource suffixes off the UI thread, then refreshes the list.</summary>
    /// <returns>A task completing after the list and bank suggestion are updated.</returns>
    private async Task Scan()
    {
        var folder = _folder.Text ?? "";
        _paths = await Task.Run(() => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(p =>
                new[] { ".xmi", ".voc", ".ad", ".opl", ".mt" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Order()
            .ToArray());
        Filter();
        SuggestBank();
        _status.Text = $"{_paths.Length} resources in {folder}";
    }

    /// <summary>Applies the case-insensitive filename filter without rescanning the folder.</summary>
    private void Filter()
    {
        _files.ItemsSource = _paths
            .Where(path => Path.GetFileName(path).Contains(_filter.Text ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(path => new ResourceItem(path, Path.GetRelativePath(_folder.Text ?? "", path))).ToArray();
    }

    /// <summary>Updates available chip modes, retaining a compatible selection and disabling FM for MT-32.</summary>
    private void UpdateChips()
    {
        var selected = (_chip.SelectedItem as ChipChoice)?.Chip;
        _chip.ItemsSource = MusicConfiguration.SupportedChips(Device).Select(chip => new ChipChoice(chip)).ToArray();
        _chip.SelectedItem = _chip.Items.OfType<ChipChoice>().FirstOrDefault(choice => choice.Chip == selected) ??
                             _chip.Items.OfType<ChipChoice>().FirstOrDefault();
        _chip.IsEnabled = Device != MusicDevice.Mt32;
        SuggestBank();
    }

    /// <summary>Suggests a conventional bank only when an existing compatible selection is absent.</summary>
    private void SuggestBank()
    {
        if (Device != MusicDevice.Mt32 && _chip.SelectedItem is null)
        {
            return;
        }

        var current = _bank.Text;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            var extension = Path.GetExtension(current).ToLowerInvariant();
            if (Device == MusicDevice.Mt32 ? extension == ".mt" : extension is ".ad" or ".opl")
            {
                return;
            }
        }

        var path = _paths.FirstOrDefault(p =>
            Path.GetExtension(p).Equals(Music.BankExtension(), StringComparison.OrdinalIgnoreCase));
        if (path is not null)
        {
            _bank.Text = path;
        }
    }

    /// <summary>Bounds the sequence selector to the selected XMIDI resource's track count.</summary>
    private void Select()
    {
        if (Selected is not { } path || !Path.GetExtension(path).Equals(".xmi", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _index.Maximum = XmidiFile.Load(File.ReadAllBytes(path)).Sequences.Count - 1;
            _index.Value = 0;
        }
        catch (Exception e)
        {
            _status.Text = e.Message;
        }
    }

    /// <summary>Uses the selected resource path as the instrument bank.</summary>
    /// <returns>A completed task.</returns>
    private Task UseBank()
    {
        if (Selected is { } path)
        {
            _bank.Text = path;
        }

        return Task.CompletedTask;
    }

    /// <summary>Loads the selected music or effect through the audio worker.</summary>
    /// <returns>A task completing after loading and playback startup.</returns>
    /// <exception cref="InvalidOperationException">No playable XMIDI or VOC resource is selected.</exception>
    private async Task PlaySelected()
    {
        var path = Selected ?? throw new InvalidOperationException("Select an XMIDI or VOC resource.");
        if (Path.GetExtension(path).Equals(".xmi", StringComparison.OrdinalIgnoreCase))
        {
            await _worker.LoadMusicAsync(path, _bank.Text ?? "", (int)(_index.Value ?? 0), Configuration, Digital);
        }
        else if (Path.GetExtension(path).Equals(".voc", StringComparison.OrdinalIgnoreCase))
        {
            await _worker.PlaySampleAsync(path, Configuration, Digital);
        }
        else
        {
            throw new InvalidOperationException("Choose a music or sound resource to play.");
        }

        _now.Text = Path.GetFileName(path);
        _status.Text = Music == MusicProfile.Mt32
            ? "MT-32 messages are available below. An external synthesizer is required for music audio."
            : "Playing";
    }

    /// <summary>Chooses a WAV destination and keeps the export cancellable until it finishes.</summary>
    /// <returns>A task completing after export or dialog cancellation.</returns>
    /// <exception cref="InvalidOperationException">Another export is already running.</exception>
    private async Task ExportWave()
    {
        if (_stopExport is not null)
        {
            throw new InvalidOperationException("An export is already running. Use Stop export to finish it.");
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Export WAV", SuggestedFileName = "ail.wav", DefaultExtension = "wav" });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        using var stop = new CancellationTokenSource();
        _stopExport = stop.Cancel;
        _status.Text = "Exporting to sequence end. For looping music, use Stop export when ready.";
        try
        {
            var frames = await _worker.ExportWaveAsync(path, stop.Token);
            _status.Text = $"Saved {path} · {frames:N0} stereo frames";
        }
        finally
        {
            _stopExport = null;
        }
    }

    /// <summary>Stops and finalizes the current export, including looping music.</summary>
    /// <returns>A completed task.</returns>
    private Task StopExport()
    {
        _stopExport?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Chooses where to save the complete MIDI log with timestamps.</summary>
    /// <returns>A task completing after writing or dialog cancellation.</returns>
    private async Task ExportMidi()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export timestamped MIDI messages", SuggestedFileName = "mt32-messages.json",
            DefaultExtension = "json"
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await _worker.ExportMidiAsync(path);
            _status.Text = $"Saved {path}";
        }
    }

    /// <summary>Refreshes status and the MIDI inspector without overlapping worker requests.</summary>
    /// <returns>A task completing after one refresh or an early shutdown exit.</returns>
    private async Task Poll()
    {
        if (_polling || _closing)
        {
            return;
        }

        _polling = true;
        try
        {
            var state = await _worker.SnapshotAsync();
            if (state.Error is not null)
            {
                _status.Text = state.Error;
            }

            if (state.MidiMessages > 0)
            {
                var messages = await _worker.MidiAsync();
                _messages.Text = string.Join(Environment.NewLine,
                    messages.Select(m => $"{TimeSpan.FromTicks(m.Ticks).TotalSeconds:F3}  {m.Kind}  {m.Data}"));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Separates the resource's full path from its relative display name.</summary>
    /// <param name="Path">The full local resource path.</param>
    /// <param name="Name">The name relative to the selected library folder.</param>
    private sealed record ResourceItem(string Path, string Name)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>Provides display names for music device families.</summary>
    /// <param name="Device">The library device-family value.</param>
    private sealed record DeviceChoice(MusicDevice Device)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Device switch
            {
                MusicDevice.AdLib => "AdLib", MusicDevice.SoundBlaster => "Sound Blaster",
                MusicDevice.ProAudioSpectrum => "Pro Audio Spectrum", MusicDevice.AdLibGold => "AdLib Gold",
                MusicDevice.Mt32 => "Roland MT-32", _ => Device.ToString()
            };
        }
    }

    /// <summary>Provides display names for FM topologies.</summary>
    /// <param name="Chip">The library chip-mode value.</param>
    private sealed record ChipChoice(OplChipMode Chip)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Chip switch
            {
                OplChipMode.Opl2 => "OPL2", OplChipMode.DualOpl2 => "Dual OPL2", OplChipMode.Opl3 => "OPL3",
                _ => Chip.ToString()
            };
        }
    }
}
