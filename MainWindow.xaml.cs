using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReelGood;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<CueItem> _cues = [];
    private readonly List<AudioTrack> _audioTracks = [];
    private readonly List<MediaElement> _stemPlayers = [];
    private readonly List<CheckBox> _stemMutes = [];
    private readonly List<Slider> _stemVolumes = [];
    private readonly Dictionary<int, double[]> _waveforms = [];
    private readonly string _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReelGood");
    private string? _videoPath;
    private string? _ffmpegPath;
    private readonly Task<string?> _engineInitialization;
    private Process? _captureProcess;
    private string? _captureOutputPath;
    private Canvas? _ruler;
    private Canvas? _playhead;
    private Canvas? _videoLane;
    private Border? _videoClip;
    private Border? _leftTrimHandle;
    private Border? _rightTrimHandle;
    private double _durationSeconds = 1;
    private double _pixelsPerSecond = 2.2;
    private double _clipStartSeconds;
    private double _clipEndSeconds = 1;
    private bool _dragTrimStart;
    private bool _dragTrimEnd;
    private bool _isMuted;
    private bool _seeking;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        CueList.ItemsSource = _cues;
        EngineStatus.Text = "Preparing built-in media engine…";
        _engineInitialization = InitializeBundledEngineAsync();
        BuildTimeline();
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); BuildTimeline(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += async (_, _) => { _isClosing = true; await StopCaptureAsync(); StopStemPlayers(); };
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import video", Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await LoadVideoAsync(dialog.FileName);
    }

    private async Task LoadVideoAsync(string path)
    {
        try
        {
            StopStemPlayers();
            VideoPreview.Stop(); VideoPreview.Source = null;
            _videoPath = Path.GetFullPath(path); _audioTracks.Clear(); _cues.Clear(); _waveforms.Clear(); _clipStartSeconds = 0; _clipEndSeconds = 1;
            _isMuted = false; VideoPreview.IsMuted = false; MuteButton.Content = "🔊";
            MediaName.Text = Path.GetFileName(_videoPath); ProjectName.Text = Path.GetFileNameWithoutExtension(_videoPath);
            var info = new FileInfo(_videoPath); MediaDetails.Text = $"{FormatBytes(info.Length)} · local file";
            VideoPreview.Source = new Uri(_videoPath); VideoPreview.Visibility = Visibility.Visible; PreviewEmpty.Visibility = Visibility.Collapsed;
            PreviewStatus.Text = "Loading preview…"; PlayButton.IsEnabled = true; SeekSlider.IsEnabled = true;
            CueSummary.Text = "Import audio tracks to enable independent scanning.";
            StatusText.Text = "Video loaded. Track probing runs when you choose Extract audio.";
            BuildTimeline();
            if (await EnsureEngineAsync()) await ProbeTracksAsync();
        }
        catch (Exception ex) { StatusText.Text = $"Could not load this video: {ex.Message}"; }
    }

    private void VideoPreview_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!VideoPreview.NaturalDuration.HasTimeSpan) return;
        _durationSeconds = Math.Max(1, VideoPreview.NaturalDuration.TimeSpan.TotalSeconds); _clipEndSeconds = _durationSeconds;
        SeekSlider.Maximum = _durationSeconds; DurationText.Text = FormatTime(_durationSeconds); PreviewStatus.Text = "Preview ready";
        BuildTimeline();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void TogglePlayback()
    {
        if (VideoPreview.Source is null) return;
        if (PlayButton.Content?.ToString() == "Ⅱ")
        {
            VideoPreview.Pause(); foreach (var player in _stemPlayers) try { player.Pause(); } catch { }
            PlayButton.Content = "▶";
        }
        else
        {
            if (VideoPreview.Position.TotalSeconds < _clipStartSeconds || VideoPreview.Position.TotalSeconds >= _clipEndSeconds) SeekTo(_clipStartSeconds);
            SetStemPositions(VideoPreview.Position); VideoPreview.Play(); foreach (var player in _stemPlayers) try { player.Play(); } catch { }
            PlayButton.Content = "Ⅱ";
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted; MuteButton.Content = _isMuted ? "🔇" : "🔊";
        VideoPreview.IsMuted = _audioTracks.Count > 0 || _isMuted;
        foreach (var player in _stemPlayers) player.IsMuted = _isMuted;
    }

    private void SeekSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void SeekSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { _seeking = false; SeekTo(SeekSlider.Value); }
    private void SeekTo(double seconds)
    {
        if (VideoPreview.Source is null) return;
        var pos = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _durationSeconds));
        try { VideoPreview.Position = pos; } catch { }
        SetStemPositions(pos); UpdatePlayhead(pos.TotalSeconds);
    }

    private void CueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CueList.SelectedItem is CueItem cue) PlayCue(cue);
    }

    private void PlayCue(CueItem cue)
    {
        SeekTo(cue.TimeSeconds); VideoPreview.Play(); foreach (var player in _stemPlayers) try { player.Play(); } catch { }
        PlayButton.Content = "Ⅱ"; StatusText.Text = $"Reviewing {FormatTime(cue.TimeSeconds)} · {cue.SourceLabel}";
    }

    private void ReviewCue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CueItem cue, Tag: string decision }) return;
        cue.Decision = decision;
        SaveFeedback(); DrawCueMarkers();
        CueSummary.Text = $"{_cues.Count} cues · {_cues.Count(c => c.Decision is not null)} reviewed";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox and not Button)
        { e.Handled = true; TogglePlayback(); }
        if (e.Key == Key.M && Keyboard.FocusedElement is not TextBox) Mute_Click(this, new RoutedEventArgs());
    }

    private async void ExtractAudio_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEngineAsync() || _videoPath is null) return;
        SetBusy(true, "Inspecting audio streams…");
        try
        {
            await ProbeTracksAsync();
            if (_audioTracks.Count == 0) { StatusText.Text = "This video has no audio tracks."; return; }
            var outputFolder = Path.Combine(Path.GetDirectoryName(_videoPath)!, Path.GetFileNameWithoutExtension(_videoPath) + "_audio");
            Directory.CreateDirectory(outputFolder);
            for (var i = 0; i < _audioTracks.Count; i++)
            {
                var track = _audioTracks[i]; track.WavPath = Path.Combine(outputFolder, $"track_{i + 1:00}.wav");
                StatusText.Text = $"Extracting audio track {i + 1} of {_audioTracks.Count}…";
                await RunFfmpegAsync(["-y", "-hide_banner", "-loglevel", "error", "-i", _videoPath, "-map", $"0:{track.StreamIndex}", "-vn", "-c:a", "pcm_s16le", track.WavPath]);
            }
            VideoPreview.IsMuted = true;
            StatusText.Text = $"Extracted {_audioTracks.Count} separate WAV track(s). Building compact waveforms…";
            _waveforms.Clear();
            for (var i = 0; i < _audioTracks.Count; i++)
            {
                var wav = _audioTracks[i].WavPath!;
                _waveforms[i] = await Task.Run(() => ReadWaveform(wav, 1800));
            }
            BuildTimeline();
            CueSummary.Text = $"{_audioTracks.Count} separate audio track(s) ready to scan.";
            StatusText.Text = $"WAV tracks saved under {outputFolder}";
        }
        catch (Exception ex) { StatusText.Text = $"Audio extraction failed: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEngineAsync() || _videoPath is null) return;
        SetBusy(true, "Checking audio tracks…");
        try
        {
            if (_audioTracks.Count == 0) await ProbeTracksAsync();
            if (_audioTracks.Count == 0) { StatusText.Text = "No audio streams were found."; return; }
            if (_audioTracks.Any(t => string.IsNullOrWhiteSpace(t.WavPath) || !File.Exists(t.WavPath)))
            {
                StatusText.Text = "Extracting separate WAV tracks before analysis…";
                var folder = Path.Combine(Path.GetDirectoryName(_videoPath)!, Path.GetFileNameWithoutExtension(_videoPath) + "_audio"); Directory.CreateDirectory(folder);
                for (var i = 0; i < _audioTracks.Count; i++)
                {
                    var track = _audioTracks[i]; track.WavPath = Path.Combine(folder, $"track_{i + 1:00}.wav");
                    await RunFfmpegAsync(["-y", "-hide_banner", "-loglevel", "error", "-i", _videoPath, "-map", $"0:{track.StreamIndex}", "-vn", "-c:a", "pcm_s16le", track.WavPath]);
                }
                VideoPreview.IsMuted = true;
                _waveforms.Clear();
                for (var i = 0; i < _audioTracks.Count; i++) { var wav = _audioTracks[i].WavPath!; _waveforms[i] = await Task.Run(() => ReadWaveform(wav, 1800)); }
                BuildTimeline();
            }
            _cues.Clear();
            for (var i = 0; i < _audioTracks.Count; i++)
            {
                var track = _audioTracks[i]; StatusText.Text = $"Scanning {track.Label} separately ({i + 1}/{_audioTracks.Count})…";
                var times = await Task.Run(() => ScanWav(track.WavPath!, .06));
                foreach (var time in times)
                    if (!_cues.Any(c => c.SourceLabel == track.Label && Math.Abs(c.TimeSeconds - time) < 5))
                        _cues.Add(new CueItem(time, track.Label));
            }
            var ordered = _cues.OrderBy(c => c.TimeSeconds).ToArray(); _cues.Clear(); foreach (var cue in ordered) _cues.Add(cue);
            CueSummary.Text = $"{_cues.Count} candidate moments · review each cue";
            StatusText.Text = "Separate-track scan complete. Cues are sound-level suggestions, not comedy judgments.";
            BuildTimeline();
        }
        catch (Exception ex) { StatusText.Text = $"Cue scan failed: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async Task ProbeTracksAsync()
    {
        if (_videoPath is null || !await EnsureEngineAsync()) return;
        var probe = Path.Combine(Path.GetDirectoryName(_ffmpegPath!)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(probe)) throw new FileNotFoundException("ffprobe.exe must be beside ffmpeg.exe to inspect separate tracks.", probe);
        var result = await RunProcessAsync(probe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index,codec_name,channels:stream_tags=title,language", "-of", "json", _videoPath]);
        using var document = JsonDocument.Parse(result.Stdout);
        _audioTracks.Clear();
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var index = stream.GetProperty("index").GetInt32(); var tags = stream.TryGetProperty("tags", out var t) ? t : default;
            var title = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("title", out var name) ? name.GetString() : null;
            var channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
            var codec = stream.TryGetProperty("codec_name", out var co) ? co.GetString() ?? "audio" : "audio";
            _audioTracks.Add(new AudioTrack(index, string.IsNullOrWhiteSpace(title) ? $"Audio track {_audioTracks.Count + 1}" : title!, codec, channels));
        }
        MediaDetails.Text = $"{FormatBytes(new FileInfo(_videoPath).Length)} · {_audioTracks.Count} audio stream(s)";
        if (_audioTracks.Count == 0) MediaDetails.Text += " · no audio";
        StatusText.Text = _audioTracks.Count == 0 ? "No audio tracks found." : $"Detected {_audioTracks.Count} separate audio stream(s).";
    }

    private void BuildTimeline()
    {
        TimelineTracks.Children.Clear(); TimelineTracks.RowDefinitions.Clear(); TimelineTracks.ColumnDefinitions.Clear(); _stemMutes.Clear(); _stemVolumes.Clear();
        var width = Math.Max(1200, _durationSeconds * _pixelsPerSecond);
        var audioCount = _audioTracks.Count(t => !string.IsNullOrWhiteSpace(t.WavPath) && File.Exists(t.WavPath));
        EnsureStemPlayers();
        TimelineTracks.Width = width; TimelineTracks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) }); TimelineTracks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width - 118) });
        TimelineTracks.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); TimelineTracks.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        for (var i = 0; i < audioCount; i++) TimelineTracks.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        AddLaneLabel("TIME", 0);
        var ruler = new Canvas { Width = width - 118, Height = 32, Background = Brush("#101316") }; _ruler = ruler;
        var interval = _durationSeconds > 1800 ? 120 : _durationSeconds > 600 ? 60 : _durationSeconds > 120 ? 15 : 5;
        for (var second = 0d; second <= _durationSeconds; second += interval)
        {
            var x = second * _pixelsPerSecond;
        ruler.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = 18, Y2 = 31, Stroke = Brush("#626970"), StrokeThickness = 1 });
            var text = new TextBlock { Text = FormatTime(second), FontSize = 9, Foreground = Brush("#B2B8BE") }; Canvas.SetLeft(text, x + 4); Canvas.SetTop(text, 2); ruler.Children.Add(text);
        }
        ruler.MouseLeftButtonDown += (_, e) => { SeekTo(e.GetPosition(ruler).X / _pixelsPerSecond); e.Handled = true; };
        Grid.SetColumn(ruler, 1); TimelineTracks.Children.Add(ruler);
        AddLaneLabel("V1  VIDEO", 1);
        var videoLane = MakeVideoClipLane(width - 118, 50); _videoLane = videoLane;
        Grid.SetRow(videoLane, 1); Grid.SetColumn(videoLane, 1); TimelineTracks.Children.Add(videoLane);
        var laneIndex = 0;
        foreach (var track in _audioTracks.Where(t => !string.IsNullOrWhiteSpace(t.WavPath) && File.Exists(t.WavPath)))
        {
            var row = laneIndex + 2; var title = new TextBlock { Text = $"A{laneIndex + 1}  {track.Label}", Foreground = Brush("#E0E3E6"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 7, 4, 0) };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 28, 0, 0) };
            var mute = new CheckBox { Content = "Mute", FontSize = 9, Foreground = Brush("#A0A6AC"), VerticalAlignment = VerticalAlignment.Center };
            var volume = new Slider { Minimum = 0, Maximum = 1, Value = .8, Width = 72, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(mute); controls.Children.Add(volume);
            var labelGrid = new Grid(); labelGrid.Children.Add(title); labelGrid.Children.Add(controls); Grid.SetRow(labelGrid, row); TimelineTracks.Children.Add(labelGrid);
            var audioLane = MakeClipLane(width - 118, 72, string.Empty);
            if (_waveforms.TryGetValue(_audioTracks.IndexOf(track), out var values)) DrawWaveform(audioLane, values);
            audioLane.MouseLeftButtonDown += (_, e) => SeekTo(e.GetPosition(audioLane).X / _pixelsPerSecond);
            Grid.SetRow(audioLane, row); Grid.SetColumn(audioLane, 1); TimelineTracks.Children.Add(audioLane);
            mute.Checked += (_, _) => ApplyTrackVolumes(); mute.Unchecked += (_, _) => ApplyTrackVolumes(); volume.ValueChanged += (_, _) => ApplyTrackVolumes();
            _stemMutes.Add(mute); _stemVolumes.Add(volume); laneIndex++;
        }
        _playhead = new Canvas { Width = 2, Height = 82 + audioCount * 72, Background = Brush("#D5FF5B"), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumnSpan(_playhead, 2); Grid.SetRowSpan(_playhead, audioCount + 2); TimelineTracks.Children.Add(_playhead); UpdatePlayhead(VideoPreview.Position.TotalSeconds);
        TimelineTracks.Height = 82 + audioCount * 72; DrawCueMarkers();
        TimelineStatus.Text = $"V1 video  ·  {audioCount} actual WAV track(s)  ·  click the ruler or a track to seek  ·  cue flags stay on the ruler";
    }

    private void AddLaneLabel(string text, int row)
    {
        var block = new TextBlock { Text = text, Foreground = Brush("#9DA4AA"), FontWeight = FontWeights.SemiBold, FontSize = 9, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(block, row); TimelineTracks.Children.Add(block);
    }

    private Canvas MakeClipLane(double width, double height, string title)
    {
        var lane = new Canvas { Width = width, Height = height, Background = Brush("#101316") };
        lane.Children.Add(new Border { Width = Math.Max(1, width - 4), Height = height - 10, Margin = new Thickness(2, 5, 0, 0), CornerRadius = new CornerRadius(4), Background = Brush("#283025"), BorderBrush = Brush("#556341"), BorderThickness = new Thickness(1) });
        if (!string.IsNullOrEmpty(title)) lane.Children.Add(new TextBlock { Text = title, Foreground = Brush("#D0E6A2"), FontSize = 9, Margin = new Thickness(9, 7, 0, 0), IsHitTestVisible = false });
        return lane;
    }

    private Canvas MakeVideoClipLane(double width, double height)
    {
        var lane = new Canvas { Width = width, Height = height, Background = Brush("#101316") };
        _videoClip = new Border { Height = height - 10, CornerRadius = new CornerRadius(4), Background = Brush("#303A29"), BorderBrush = Brush("#78934B"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "VIDEO CLIP  ·  drag either edge to trim", Foreground = Brush("#D5E6B3"), FontSize = 9, Margin = new Thickness(10, 7, 0, 0), IsHitTestVisible = false } };
        _leftTrimHandle = new Border { Width = 8, Height = height - 10, Background = Brush("#D5FF5B"), CornerRadius = new CornerRadius(3, 0, 0, 3), Cursor = Cursors.SizeWE, ToolTip = "Drag to set clip in-point" };
        _rightTrimHandle = new Border { Width = 8, Height = height - 10, Background = Brush("#D5FF5B"), CornerRadius = new CornerRadius(0, 3, 3, 0), Cursor = Cursors.SizeWE, ToolTip = "Drag to set clip out-point" };
        lane.Children.Add(_videoClip); lane.Children.Add(_leftTrimHandle); lane.Children.Add(_rightTrimHandle);
        _leftTrimHandle.MouseLeftButtonDown += (_, e) => { _dragTrimStart = true; lane.CaptureMouse(); e.Handled = true; };
        _rightTrimHandle.MouseLeftButtonDown += (_, e) => { _dragTrimEnd = true; lane.CaptureMouse(); e.Handled = true; };
        lane.MouseMove += (_, e) =>
        {
            if (!_dragTrimStart && !_dragTrimEnd) return;
            var seconds = Math.Clamp(e.GetPosition(lane).X / _pixelsPerSecond, 0, _durationSeconds);
            if (_dragTrimStart) _clipStartSeconds = Math.Min(seconds, _clipEndSeconds - .25);
            if (_dragTrimEnd) _clipEndSeconds = Math.Max(seconds, _clipStartSeconds + .25);
            UpdateVideoClipVisuals(); TimelineStatus.Text = $"Trim range  {FormatTime(_clipStartSeconds)} – {FormatTime(_clipEndSeconds)}  ·  drag either edge to adjust";
        };
        lane.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragTrimStart || _dragTrimEnd) { _dragTrimStart = false; _dragTrimEnd = false; lane.ReleaseMouseCapture(); e.Handled = true; }
        };
        lane.MouseLeftButtonDown += (_, e) => { SeekTo(e.GetPosition(lane).X / _pixelsPerSecond); };
        UpdateVideoClipVisuals(); return lane;
    }

    private void UpdateVideoClipVisuals()
    {
        if (_videoLane is null || _videoClip is null || _leftTrimHandle is null || _rightTrimHandle is null) return;
        var left = _clipStartSeconds * _pixelsPerSecond; var right = _clipEndSeconds * _pixelsPerSecond;
        _videoClip.Width = Math.Max(8, right - left); Canvas.SetLeft(_videoClip, left); Canvas.SetTop(_videoClip, 5);
        Canvas.SetLeft(_leftTrimHandle, left); Canvas.SetTop(_leftTrimHandle, 5);
        Canvas.SetLeft(_rightTrimHandle, right - _rightTrimHandle.Width); Canvas.SetTop(_rightTrimHandle, 5);
    }

    private void DrawWaveform(Canvas lane, double[] values)
    {
        if (values.Length == 0) return;
        var points = new PointCollection(values.Length * 2); var center = lane.Height / 2; var amplitude = lane.Height * .38;
        for (var i = 0; i < values.Length; i++)
        {
            var x = 3 + (lane.Width - 6) * i / Math.Max(1, values.Length - 1); var h = Math.Max(1, amplitude * values[i]);
            points.Add(new Point(x, center - h)); points.Add(new Point(x, center + h));
        }
        lane.Children.Add(new System.Windows.Shapes.Polyline { Points = points, Stroke = Brush("#B7E56C"), StrokeThickness = 1, IsHitTestVisible = false });
    }

    private static double[] ReadWaveform(string path, int bins)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 256, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") return []; _ = reader.ReadInt32(); if (new string(reader.ReadChars(4)) != "WAVE") return [];
        ushort channels = 0, bits = 0; long start = 0, length = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4)); var size = reader.ReadUInt32(); var next = stream.Position + size + (size & 1);
            if (id == "fmt ") { _ = reader.ReadUInt16(); channels = reader.ReadUInt16(); _ = reader.ReadInt32(); _ = reader.ReadInt32(); _ = reader.ReadUInt16(); bits = reader.ReadUInt16(); }
            else if (id == "data") { start = stream.Position; length = size; break; } stream.Position = Math.Min(next, stream.Length);
        }
        if (channels == 0 || bits != 16 || length <= 0) return [];
        bins = Math.Clamp(bins, 100, 2400); var frames = length / (channels * 2L); var bytesPerBin = (int)Math.Max(channels * 2L, Math.Min(int.MaxValue, frames / bins * channels * 2L)); var buffer = new byte[bytesPerBin]; var values = new double[bins]; stream.Position = start;
        for (var i = 0; i < bins && stream.Position < start + length; i++)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, start + length - stream.Position)); double peak = 0;
            for (var j = 0; j + 1 < read; j += 2) peak = Math.Max(peak, Math.Abs((short)(buffer[j] | buffer[j + 1] << 8) / 32768.0)); values[i] = peak;
        }
        var max = values.Max(); if (max > 0) for (var i = 0; i < values.Length; i++) values[i] = Math.Sqrt(values[i] / max); return values;
    }

    private static IReadOnlyList<double> ScanWav(string path, double sensitivity)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Expected a WAV file."); _ = reader.ReadInt32(); if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Expected a WAV file.");
        ushort channels = 0, bits = 0, format = 0; int rate = 0; long start = 0, length = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4)); var size = reader.ReadUInt32(); var next = stream.Position + size + (size & 1);
            if (id == "fmt ") { format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32(); _ = reader.ReadInt32(); _ = reader.ReadUInt16(); bits = reader.ReadUInt16(); }
            else if (id == "data") { start = stream.Position; length = size; break; } stream.Position = Math.Min(next, stream.Length);
        }
        if (format != 1 || bits != 16 || channels == 0 || rate <= 0) throw new InvalidDataException("Scan requires 16-bit PCM WAV audio.");
        var bytesPerFrame = channels * 2; var bytesPerWindow = Math.Max(bytesPerFrame, rate / 5 * bytesPerFrame); var buffer = new byte[bytesPerWindow]; var cues = new List<double>(); var history = new Queue<double>(); long frames = 0; double lastCue = -10;
        stream.Position = start;
        while (stream.Position < start + length)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, start + length - stream.Position)); if (read <= 0) break; double sum = 0; var samples = 0;
            for (var i = 0; i + 1 < read; i += 2) { var sample = (short)(buffer[i] | buffer[i + 1] << 8) / 32768.0; sum += sample * sample; samples++; }
            var rms = samples == 0 ? 0 : Math.Sqrt(sum / samples); history.Enqueue(rms); if (history.Count > 100) history.Dequeue();
            var seconds = frames / (double)rate; var baseline = history.Count < 8 ? sensitivity * .4 : Math.Max(sensitivity * .3, history.Average() * 1.8);
            if (rms > sensitivity && rms > baseline && seconds - lastCue > 7) { cues.Add(Math.Max(0, seconds - .8)); lastCue = seconds; }
            frames += read / bytesPerFrame;
        }
        return cues;
    }

    private void DrawCueMarkers()
    {
        if (_ruler is null) return;
        foreach (var item in _ruler.Children.OfType<Border>().Where(b => Equals(b.Tag, "cue-marker")).ToArray()) _ruler.Children.Remove(item);
        foreach (var cue in _cues)
        {
            var marker = new Border { Tag = "cue-marker", Width = 9, Height = 10, CornerRadius = new CornerRadius(2), Background = Brush(cue.Decision switch { "Keep" => "#65D27C", "Skip" => "#747B81", _ => "#D5FF5B" }), ToolTip = $"{FormatTime(cue.TimeSeconds)} · {cue.SourceLabel} · {cue.Decision ?? "Review"}" };
            Canvas.SetLeft(marker, Math.Clamp(cue.TimeSeconds * _pixelsPerSecond, 0, Math.Max(0, _ruler.Width - 10))); Canvas.SetTop(marker, 19);
            marker.MouseLeftButtonDown += (_, e) => { e.Handled = true; CueList.SelectedItem = cue; PlayCue(cue); };
            _ruler.Children.Add(marker);
        }
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return; _pixelsPerSecond = ZoomSlider.Value; _zoomTimer.Stop(); _zoomTimer.Start();
    }
    private void UpdateClock()
    {
        if (VideoPreview.Source is null || !VideoPreview.NaturalDuration.HasTimeSpan) return;
        var seconds = VideoPreview.Position.TotalSeconds; CurrentTime.Text = FormatTime(seconds);
        if (!_seeking) SeekSlider.Value = Math.Min(SeekSlider.Maximum, seconds);
        UpdatePlayhead(seconds);
        if (PlayButton.Content?.ToString() == "Ⅱ" && seconds >= _clipEndSeconds)
        {
            VideoPreview.Pause(); foreach (var player in _stemPlayers) try { player.Pause(); } catch { }
            PlayButton.Content = "▶"; SeekTo(_clipEndSeconds);
        }
    }
    private void UpdatePlayhead(double seconds)
    {
        if (_playhead is not null) _playhead.Margin = new Thickness(118 + Math.Clamp(seconds, 0, _durationSeconds) * _pixelsPerSecond, 0, 0, 0);
    }
    private void SetStemPositions(TimeSpan position) { foreach (var player in _stemPlayers) try { player.Position = position; } catch { } }

    private void ApplyTrackVolumes()
    {
        for (var i = 0; i < _stemPlayers.Count && i < _stemVolumes.Count; i++) { _stemPlayers[i].Volume = _stemVolumes[i].Value; _stemPlayers[i].IsMuted = _isMuted || _stemMutes[i].IsChecked == true; }
    }
    private void StopStemPlayers()
    {
        foreach (var player in _stemPlayers) { try { player.Stop(); } catch { } RootGrid.Children.Remove(player); }
        _stemPlayers.Clear(); _stemMutes.Clear(); _stemVolumes.Clear();
    }

    private void EnsureStemPlayers()
    {
        var sources = _audioTracks.Where(t => !string.IsNullOrWhiteSpace(t.WavPath) && File.Exists(t.WavPath)).Select(t => t.WavPath!).ToArray();
        var same = sources.Length == _stemPlayers.Count && sources.Select((source, i) => _stemPlayers[i].Tag as string == source).All(matches => matches);
        if (same) return;
        StopStemPlayers();
        foreach (var source in sources)
        {
            var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop, Width = 1, Height = 1, Opacity = .01, IsHitTestVisible = false, Tag = source };
            player.Source = new Uri(source); player.MediaOpened += (_, _) => { try { player.Position = VideoPreview.Position; } catch { } };
            RootGrid.Children.Add(player); Grid.SetRow(player, 0); Grid.SetColumnSpan(player, 2); _stemPlayers.Add(player);
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_captureProcess is not null) { await StopCaptureAsync(); return; }
        if (!await EnsureEngineAsync()) return;
        if (SystemAudioCheck.IsChecked != true && MicCheck.IsChecked != true) { StatusText.Text = "Choose at least one audio source."; return; }
        var dialog = new SaveFileDialog { Title = "Save screen recording", FileName = $"ReelGood-{DateTime.Now:yyyyMMdd-HHmmss}.mp4", Filter = "MP4 video|*.mp4" };
        if (dialog.ShowDialog(this) != true) return;
        _captureOutputPath = dialog.FileName;
        var args = new List<string> { "-y", "-f", "gdigrab", "-framerate", "60", "-i", "desktop" };
        var nextInput = 1; var audioTrack = 0; var audioMaps = new List<(int Input, string Title)>();
        if (SystemAudioCheck.IsChecked == true) { args.AddRange(["-f", "wasapi", "-loopback", "1", "-i", "default"]); audioMaps.Add((nextInput++, "System audio")); }
        if (MicCheck.IsChecked == true) { args.AddRange(["-f", "wasapi", "-i", "default"]); audioMaps.Add((nextInput++, "Microphone")); }
        args.AddRange(["-map", "0:v:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        foreach (var (input, title) in audioMaps) { args.AddRange(["-map", $"{input}:a:0", $"-metadata:s:a:{audioTrack}", $"title={title}", $"-c:a:{audioTrack}", "aac", $"-b:a:{audioTrack}", "160k"]); audioTrack++; }
        args.AddRange(["-movflags", "+faststart", _captureOutputPath]);
        try
        {
            _captureProcess = StartFfmpeg(args); RecordButton.Content = "■  Stop recording"; StatusText.Text = "Recording desktop with separate system and microphone tracks. Stop to finish.";
        }
        catch (Exception ex) { _captureProcess = null; StatusText.Text = $"Could not start recording: {ex.Message}"; }
    }

    private async Task StopCaptureAsync()
    {
        var process = _captureProcess; _captureProcess = null;
        if (process is null) return;
        try { await process.StandardInput.WriteLineAsync("q"); await process.WaitForExitAsync(); }
        catch { }
        RecordButton.Content = "●  Record";
        if (process.ExitCode == 0 && _captureOutputPath is not null && File.Exists(_captureOutputPath))
        { StatusText.Text = "Recording saved."; if (!_isClosing) await LoadVideoAsync(_captureOutputPath); }
        else StatusText.Text = "Recording stopped. Check FFmpeg and Windows audio input settings.";
        process.Dispose();
    }

    private void AddCueAtPlayhead_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null) { StatusText.Text = "Import a video before adding a cue."; return; }
        AddCue(VideoPreview.Position.TotalSeconds, "Manual bookmark");
        StatusText.Text = $"Added a review marker at {FormatTime(VideoPreview.Position.TotalSeconds)}.";
    }

    private async void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null) { StatusText.Text = "Import a video before exporting."; return; }
        if (!await EnsureEngineAsync()) return;
        if (_clipEndSeconds - _clipStartSeconds < .25) { StatusText.Text = "Choose a longer trim range."; return; }
        var dialog = new SaveFileDialog { Title = "Export trimmed video", FileName = $"{Path.GetFileNameWithoutExtension(_videoPath)}_edit.mp4", Filter = "MP4 video|*.mp4" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true, $"Exporting {FormatTime(_clipStartSeconds)}–{FormatTime(_clipEndSeconds)}…");
        try
        {
            var length = _clipEndSeconds - _clipStartSeconds;
            await RunFfmpegAsync(["-y", "-hide_banner", "-loglevel", "error", "-ss", _clipStartSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", _videoPath, "-t", length.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:v:0", "-map", "0:a?", "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac", "-movflags", "+faststart", dialog.FileName]);
            StatusText.Text = $"Exported trimmed clip: {dialog.FileName}";
        }
        catch (Exception ex) { StatusText.Text = $"Export failed: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private void AddCue(double seconds, string label)
    {
        if (_cues.Any(c => c.SourceLabel == label && Math.Abs(c.TimeSeconds - seconds) < 1)) return;
        _cues.Add(new CueItem(seconds, label)); CueSummary.Text = $"{_cues.Count} moments · review each cue"; DrawCueMarkers();
    }

    private void SaveFeedback()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var feedback = _cues.Where(c => c.Decision is not null).Select(c => new { c.TimeSeconds, c.SourceLabel, c.Decision, video = _videoPath });
            File.WriteAllText(Path.Combine(_settingsDirectory, "review-feedback.json"), JsonSerializer.Serialize(feedback, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { StatusText.Text = $"Could not save review choice: {ex.Message}"; }
    }

    private async void ApplyPreference_Click(object sender, RoutedEventArgs e)
    {
        var preference = PreferenceBox.Text.Trim();
        if (preference.Length == 0) { PreferenceStatus.Text = "Enter a preference first."; return; }
        var confirm = MessageBox.Show(this, $"Save this preference for future cue reviews?\n\n{preference}\n\nThis saves your review guidance locally. The current scanner still detects sound-level changes; it does not understand comedy or learn model weights.", "Approve preference change", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) { PreferenceStatus.Text = "Preference change dismissed."; return; }
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            await File.WriteAllTextAsync(Path.Combine(_settingsDirectory, "preferences.json"), JsonSerializer.Serialize(new { preference, approvedAt = DateTimeOffset.Now }, new JsonSerializerOptions { WriteIndented = true }));
            PreferenceStatus.Text = "Saved locally with your approval. The current cue detector remains sound-level based.";
        }
        catch (Exception ex) { PreferenceStatus.Text = $"Could not save preference: {ex.Message}"; }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        Mouse.OverrideCursor = busy ? Cursors.Wait : null; if (message is not null) StatusText.Text = message;
    }
    private async Task<bool> EnsureEngineAsync()
    {
        try { _ffmpegPath = await _engineInitialization; }
        catch (Exception ex)
        {
            EngineStatus.Text = "Built-in media engine unavailable";
            StatusText.Text = $"Could not prepare ReelGood's built-in FFmpeg tools: {ex.Message}";
            return false;
        }
        if (HasAdjacentProbe(_ffmpegPath))
        {
            EngineStatus.Text = $"Media engine ready · {Path.GetFileName(_ffmpegPath)}";
            return true;
        }
        EngineStatus.Text = "Built-in media engine unavailable";
        StatusText.Text = "ReelGood could not find its built-in FFmpeg tools. Reinstall the app to restore them.";
        return false;
    }

    private Task<string?> InitializeBundledEngineAsync() => Task.Run<string?>(() =>
    {
        var logFile = Path.Combine(_settingsDirectory, "engine-startup.log");
        void Log(string message)
        {
            try { Directory.CreateDirectory(_settingsDirectory); File.AppendAllText(logFile, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { }
        }
        try
        {
            var installFolder = Path.Combine(_settingsDirectory, "tools");
            var ffmpeg = Path.Combine(installFolder, "ffmpeg.exe");
            var ffprobe = Path.Combine(installFolder, "ffprobe.exe");
            var assembly = Assembly.GetExecutingAssembly();
            var resources = new[] { (Name: "ReelGood.tools.ffmpeg.exe", Path: ffmpeg), (Name: "ReelGood.tools.ffprobe.exe", Path: ffprobe) };

            var needsInstall = false;
            foreach (var item in resources)
            {
                using var embedded = assembly.GetManifestResourceStream(item.Name)
                    ?? throw new FileNotFoundException($"Embedded media tool {item.Name} is missing.");
                if (!File.Exists(item.Path) || new FileInfo(item.Path).Length != embedded.Length) needsInstall = true;
            }
            if (needsInstall)
            {
                Directory.CreateDirectory(installFolder);
                foreach (var item in resources)
                {
                    Log($"Extracting {item.Name}");
                    using (var source = assembly.GetManifestResourceStream(item.Name)
                        ?? throw new FileNotFoundException($"Embedded media tool {item.Name} is missing."))
                    {
                        using var target = new FileStream(item.Path, FileMode.Create, FileAccess.Write, FileShare.None);
                        source.CopyTo(target);
                    }
                    Log($"Extracted {item.Name}");
                    Log($"Installed {item.Name}");
                }
            }
            if (!HasAdjacentProbe(ffmpeg)) throw new FileNotFoundException("The built-in FFmpeg tools did not install correctly.");
            Log("Media tools ready");
            return ffmpeg;
        }
        catch (Exception ex)
        {
            Log($"Bundled media tool setup failed: {ex}");
            // Allow source builds without embedded tools to use an existing local installation.
            var fallback = FindFfmpeg();
            if (fallback is not null) return fallback;
            throw;
        }
    });

    private async Task RunFfmpegAsync(IEnumerable<string> args)
    {
        var result = await RunProcessAsync(_ffmpegPath!, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Stderr) ? "FFmpeg media operation failed." : result.Stderr.Trim());
    }
    private Process StartFfmpeg(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(_ffmpegPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); var process = new Process { StartInfo = start }; process.Start(); _ = process.StandardError.ReadToEndAsync(); return process;
    }
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(string file, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start }; process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); return (await stdout, await stderr, process.ExitCode);
    }
    private static string? FindFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) { var candidate = Path.Combine(folder.Trim('"'), "ffmpeg.exe"); if (HasAdjacentProbe(candidate)) return candidate; }
        return null;
    }
    private static bool HasAdjacentProbe(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "ffprobe.exe"));
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static string FormatTime(double seconds) { var t = TimeSpan.FromSeconds(Math.Max(0, seconds)); return t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}"; }
    private static string FormatBytes(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0.0} MB";

    private sealed class AudioTrack(int streamIndex, string label, string codec, int channels)
    {
        public int StreamIndex { get; } = streamIndex; public string Label { get; } = label; public string Codec { get; } = codec; public int Channels { get; } = channels; public string? WavPath { get; set; }
    }
    private sealed class CueItem(double timeSeconds, string sourceLabel) : INotifyPropertyChanged
    {
        public double TimeSeconds { get; } = timeSeconds; public string SourceLabel { get; } = sourceLabel;
        private string? _decision; public string? Decision { get => _decision; set { _decision = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); } }
        public string DisplayText => $"{FormatTime(TimeSeconds)}  ·  {SourceLabel}{(Decision is null ? "  ·  review" : $"  ·  {Decision}")}";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

