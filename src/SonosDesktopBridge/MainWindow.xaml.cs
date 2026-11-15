using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SonosDesktopBridge;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Room> _rooms = new();
    private readonly ObservableCollection<AudioOutput> _outputs = new();
    private readonly SonosController _sonos = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _outputTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _keyFlush = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer _scanRetry = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly VolumeKeyHook? _hook;
    private readonly OsdWindow _osd = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripItem? _trayOpen, _trayMute, _trayQuit;
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _roomSliders = new();
    private readonly Dictionary<string, CancellationTokenSource> _roomDelay = new();
    private CancellationTokenSource? _sliderDelay;

    private static readonly int[] MaxValues = { 100, 90, 80, 70, 60, 50, 40 };
    private static readonly int[] DefaultVolumeValues = { 0, 5, 10, 15, 20, 25, 30, 40, 50 };
    private static readonly string[] LangModes = { "auto", "fr", "en" };
    private const int WizLast = 4;

    private string _roomSignature = "";
    private string _groupSignature = "";
    private string _groupLabel = "";
    private string _sourceLabel = "";
    private string? _outputId;
    private string? _lineInSuggestion;
    private string _statusKey = "st.none", _statusColor = "#D5A85C";
    private string? _hintKey;
    private object[] _hintArgs = Array.Empty<object>();
    private bool _loadingRooms, _loading, _mute, _sliderPending, _exiting, _syncing, _busy, _loadingOutputs;
    private bool _isLineIn, _wizSync, _closeHintShown, _refreshingCombos, _defaultVolumeDone, _mutedBySleep;
    private int _roomPending, _pendingDelta, _volume, _wizStep;

    /// <summary>True until the first-run setup has been completed or skipped.</summary>
    internal bool NeedsSetup => !_settings.SetupDone;

    private static string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    public MainWindow()
    {
        Loc.Set(_settings.Language);
        InitializeComponent();
        DevicePicker.ItemsSource = _rooms;
        OutputPicker.ItemsSource = _outputs;
        WizRooms.ItemsSource = _rooms;
        WizOutputs.ItemsSource = _outputs;
        _poll.Tick += async (_, _) => await RefreshStateAsync();
        _outputTimer.Tick += (_, _) => EvaluateOutput();
        _keyFlush.Tick += async (_, _) => await FlushKeysAsync();
        // The Sonos may not be reachable yet (PC just booted, speaker waking up): keep trying quietly.
        _scanRetry.Tick += async (_, _) => { _scanRetry.Stop(); await ScanAsync(); };
        Closing += MainWindow_Closing;
        BuildDots();

        StepPicker.SelectedIndex = Math.Clamp(_settings.Step, 1, 5) - 1;
        MaxPicker.SelectedIndex = Math.Max(0, Array.IndexOf(MaxValues, _settings.MaxVolume));
        DefVolPicker.SelectedIndex = Math.Max(0, Array.IndexOf(DefaultVolumeValues, _settings.DefaultVolume));
        SleepPicker.SelectedIndex = Math.Clamp(_settings.SleepAction, 0, 1);
        LangPicker.SelectedIndex = Math.Max(0, Array.IndexOf(LangModes, _settings.Language));
        InterceptBox.IsChecked = _settings.InterceptKeys;
        MediaBox.IsChecked = _settings.MediaKeys;
        OnlyLineInBox.IsChecked = _settings.OnlyLineIn;
        OsdBox.IsChecked = _settings.ShowOsd;
        LockRoomBox.IsChecked = _settings.LockRoom;
        LockOutputBox.IsChecked = _settings.LockOutput;
        AutostartBox.IsChecked = Autostart.IsEnabled;
        if (Installer.IsInstalledCopy) InstallButton.Visibility = Visibility.Collapsed; else UninstallButton.Visibility = Visibility.Collapsed;
        SetStatus("st.none", "#D5A85C");

        // Nothing is assumed: the Sonos room and the Windows output are chosen once, then remembered (and lockable).
        _outputId = _settings.OutputId;
        LoadOutputs();
        ApplyLocks();

        try
        {
            _hook = new VolumeKeyHook { KeyDown = OnHookKey };
        }
        catch (Exception ex)
        {
            InterceptBox.IsEnabled = false;
            InterceptBox.IsChecked = false;
            SetHint("hint.connfail", ex.Message);
        }
        BuildTray();
        Loc.Changed += () => Dispatcher.BeginInvoke(new Action(ApplyLanguage));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        ApplyLanguage();
        _outputTimer.Start();
        EvaluateOutput();
        if (Installer.IsInstalledCopy) { WizInstall.IsChecked = false; WizInstall.Visibility = Visibility.Collapsed; WizInstallSep.Visibility = Visibility.Collapsed; }
        if (NeedsSetup) ShowWizard(0);
        // Discovery starts right away, even if the window is never shown (silent start in the notification area).
        Dispatcher.BeginInvoke(new Action(async () => await ScanAsync()), DispatcherPriority.Background);
    }

    // Dark title bar to match the dark UI (Windows 10 20H1+ / Windows 11).
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var on = 1;
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        }
        catch { }
    }

    // ---- Language ----

    /// <summary>Re-applies every text that is produced in code (XAML texts update themselves).</summary>
    private void ApplyLanguage()
    {
        AboutVersion.Text = Loc.T("about.version", AppVersion);
        InstallText.Text = Loc.T(Installer.IsInstalledCopy ? "app.installed" : "app.portable");
        MuteButton.Content = Loc.T(_mute ? "btn.unmute" : "btn.mute");
        SetStatus(_statusKey, _statusColor);
        if (_hintKey is not null) DeviceHint.Text = Loc.T(_hintKey, _hintArgs);
        if (_trayOpen is not null) { _trayOpen.Text = Loc.T("tray.open"); _trayMute!.Text = Loc.T("tray.mute"); _trayQuit!.Text = Loc.T("tray.quit"); }

        // Combo boxes cache the text of their selected item: re-select to refresh it.
        _refreshingCombos = true;
        foreach (var c in new[] { MaxPicker, DefVolPicker, SleepPicker, LangPicker })
        {
            var i = c.SelectedIndex; c.SelectedIndex = -1; c.SelectedIndex = i;
        }
        _refreshingCombos = false;

        LoadOutputs();
        _groupSignature = "";
        EvaluateOutput();
        UpdateWizardHints();
        if (Wizard.Visibility == Visibility.Visible) GoToStep(_wizStep);
        if (_sonos.IsConnected) _ = RefreshStateAsync();
    }

    private void SetStatus(string key, string color)
    {
        _statusKey = key; _statusColor = color;
        StatusText.Text = Loc.T(key);
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void SetHint(string key, params object[] args)
    {
        _hintKey = key; _hintArgs = args;
        DeviceHint.Text = Loc.T(key, args);
    }

    // ---- Tray ----

    private void BuildTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "Sonos Desktop Bridge",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        _trayOpen = _tray.ContextMenuStrip.Items.Add(Loc.T("tray.open"), null, (_, _) => ShowFromTray());
        _trayMute = _tray.ContextMenuStrip.Items.Add(Loc.T("tray.mute"), null, async (_, _) => await ToggleMuteAsync());
        _trayQuit = _tray.ContextMenuStrip.Items.Add(Loc.T("tray.quit"), null, (_, _) => { _exiting = true; Close(); });
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
    }

    private static System.Drawing.Icon TrayIcon()
    {
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    internal void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            // Closing the window keeps the bridge running in the notification area.
            e.Cancel = true;
            Hide();
            if (!_closeHintShown && _tray is not null)
            {
                _closeHintShown = true;
                _tray.ShowBalloonTip(3500, "Sonos Desktop Bridge", Loc.T("tray.hint"), System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poll.Stop();
        _outputTimer.Stop();
        _scanRetry.Stop();
        _hook?.Dispose();
        _osd.Close();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
    }

    // ---- Sleep / wake ----

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Only a real suspend (not "screen off", not lock), and only if Sonos is playing the PC's sound.
            if (_settings.SleepAction == 1 && _sonos.IsConnected && _isLineIn && !_mute)
            {
                try
                {
                    // Off the UI context so the blocking wait cannot deadlock; Windows only gives us a moment.
                    Task.Run(() => _sonos.SetMuteAsync(true)).Wait(2500);
                    _mutedBySleep = true;
                    _mute = true;
                }
                catch { }
            }
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _ = ResumeAsync();
        }
    }

    private async Task ResumeAsync()
    {
        await Task.Delay(5000); // let the network come back
        _defaultVolumeDone = false;
        for (var i = 0; i < 4; i++)
        {
            try
            {
                if (_mutedBySleep)
                {
                    await _sonos.SetMuteAsync(false);
                    _mutedBySleep = false; _mute = false;
                    MuteButton.Content = Loc.T("btn.mute");
                }
                await RefreshStateAsync(force: true);
                return;
            }
            catch { await Task.Delay(4000); }
        }
    }

    private async Task ApplyDefaultVolumeAsync(SonosState state)
    {
        // Safety: only when Sonos plays the PC's sound, so music playing for someone else is never touched.
        if (_settings.DefaultVolume <= 0 || !state.IsLineIn) return;
        var target = Math.Min(_settings.DefaultVolume, _settings.MaxVolume);
        if (state.Volume == target) return;
        try { await _sonos.SetVolumeAsync(target); ShowVolume(target); } catch { }
    }

    // ---- Windows output awareness ----

    private void LoadOutputs()
    {
        _loadingOutputs = true;
        _outputs.Clear();
        foreach (var o in AudioOutputs.List()) _outputs.Add(o);
        // Keep the saved device visible even when it is not connected right now (e.g. a headset that is off).
        if (_outputId is not null && !_outputs.Any(o => o.Id == _outputId) && !string.IsNullOrEmpty(_settings.OutputName))
            _outputs.Add(new AudioOutput(_outputId, _settings.OutputName + Loc.T("out.unavailable"), false));
        OutputPicker.SelectedItem = _outputs.FirstOrDefault(o => o.Id == _outputId);
        WizOutputs.SelectedItem = OutputPicker.SelectedItem;
        _loadingOutputs = false;
    }

    /// <summary>Greys out the room / output lists once a value is saved and its lock is on.</summary>
    private void ApplyLocks()
    {
        DevicePicker.IsEnabled = !(LockRoomBox.IsChecked == true && _settings.RoomName is not null);
        OutputPicker.IsEnabled = !(LockOutputBox.IsChecked == true && _settings.OutputId is not null);
    }

    private void OutputPicker_DropDownOpened(object? sender, EventArgs e)
    {
        // Devices come and go (Bluetooth headsets): refresh the list each time it is opened.
        if (!_loadingOutputs) LoadOutputs();
    }

    private void OutputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOutputs || OutputPicker.SelectedItem is not AudioOutput { Available: true } output) return;
        _outputId = output.Id;
        _settings.OutputId = output.Id;
        _settings.OutputName = output.Name;
        _settings.Save();
        ApplyLocks();
        EvaluateOutput();
    }

    /// <summary>True when the current default Windows output is the one configured for the Sonos.</summary>
    private bool IsSonosOutput(string? currentId)
    {
        if (currentId is null || _settings.OutputId is null) return false;
        if (string.Equals(currentId, _settings.OutputId, StringComparison.OrdinalIgnoreCase)) return true;

        // Fallback: Windows sometimes gives a device a new id (driver reinstall). If the saved device is
        // gone and the active one carries the same name, adopt it.
        var all = AudioOutputs.List();
        if (all.Any(o => string.Equals(o.Id, _settings.OutputId, StringComparison.OrdinalIgnoreCase))) return false;
        var name = all.FirstOrDefault(o => o.Id == currentId)?.Name;
        if (name is null || name != _settings.OutputName) return false;
        _outputId = _settings.OutputId = currentId;
        _settings.Save();
        return true;
    }

    /// <summary>Decides whether the volume and media keys drive Sonos or are left to Windows.</summary>
    private void EvaluateOutput()
    {
        var current = AudioOutputs.DefaultId();
        var configured = _settings.OutputId is not null;
        var onSonosOutput = IsSonosOutput(current);
        var sourceOk = OnlyLineInBox.IsChecked != true || _isLineIn;
        var wanted = InterceptBox.IsChecked == true && _sonos.IsConnected && configured;
        var active = wanted && onSonosOutput && sourceOk;
        if (_hook is not null)
        {
            _hook.Enabled = active;
            // Media keys go to Sonos only when Sonos plays its own music; on the PC's line-in they stay with Windows players.
            _hook.MediaEnabled = MediaBox.IsChecked == true && _sonos.IsConnected && configured && onSonosOutput && !_isLineIn;
        }
        if (wanted && onSonosOutput) AudioOutputs.EnsureFull(current!);

        if (!configured)
        {
            OutputHint.Text = Loc.T("out.pick");
            SetKeys("amber", Loc.T("keys.pick"), Loc.T("keys.pick.sub"));
        }
        else if (!_sonos.IsConnected)
        {
            OutputHint.Text = Loc.T("out.wait");
            SetKeys("amber", Loc.T("keys.wait"), Loc.T("keys.wait.sub"));
        }
        else if (InterceptBox.IsChecked != true)
        {
            OutputHint.Text = Loc.T("out.off");
            SetKeys("gray", Loc.T("keys.off"), Loc.T("keys.off.sub"));
        }
        else if (!onSonosOutput)
        {
            var name = AudioOutputs.List().FirstOrDefault(o => o.Id == current)?.Name ?? Loc.T("out.another");
            OutputHint.Text = Loc.T("out.other", name, _settings.OutputName ?? "");
            SetKeys("gray", Loc.T("keys.win"), Loc.T("keys.win.out", name));
        }
        else if (!sourceOk)
        {
            OutputHint.Text = Loc.T("out.src");
            SetKeys("gray", Loc.T("keys.win"), Loc.T("keys.win.src"));
        }
        else
        {
            OutputHint.Text = Loc.T("out.ok");
            SetKeys("green", Loc.T("keys.on"), Loc.T("keys.on.sub", _groupLabel.Length > 0 ? _groupLabel : _settings.RoomName ?? ""));
        }
    }

    private void SetKeys(string kind, string title, string sub)
    {
        KeysStatusText.Text = title;
        KeysStatusSub.Text = sub;
        KeysDot.Fill = (Brush)new BrushConverter().ConvertFromString(kind == "green" ? "#78B88A" : kind == "amber" ? "#D5A85C" : "#6B6B70")!;
    }

    // ---- Keys & OSD ----

    private int Step => StepPicker.SelectedIndex + 1;

    private void OnHookKey(int vk)
    {
        if (vk is VolumeKeyHook.MediaPlayPause or VolumeKeyHook.MediaNext or VolumeKeyHook.MediaPrev or VolumeKeyHook.MediaStop)
        {
            _ = MediaKeyAsync(vk);
            return;
        }
        OnVolumeKey(vk);
    }

    private void OnVolumeKey(int vk)
    {
        if (vk == VolumeKeyHook.VolumeMute)
        {
            _ = ToggleMuteAsync();
            return;
        }
        _pendingDelta += vk == VolumeKeyHook.VolumeUp ? Step : -Step;
        if (!_keyFlush.IsEnabled) _keyFlush.Start();
    }

    private async Task MediaKeyAsync(int vk)
    {
        if (!_sonos.IsConnected) return;
        try
        {
            string text;
            switch (vk)
            {
                case VolumeKeyHook.MediaPlayPause:
                    if (await _sonos.IsPlayingAsync()) { await _sonos.PauseAsync(); text = Loc.T("osd.pause"); }
                    else { await _sonos.PlayAsync(); text = Loc.T("osd.play"); }
                    break;
                case VolumeKeyHook.MediaNext: await _sonos.NextAsync(); text = Loc.T("osd.next"); break;
                case VolumeKeyHook.MediaPrev: await _sonos.PreviousAsync(); text = Loc.T("osd.prev"); break;
                default: await _sonos.PauseAsync(); text = Loc.T("osd.pause"); break;
            }
            if (OsdBox.IsChecked == true) _osd.ShowMessage(_groupLabel, _sourceLabel, text);
        }
        catch { SetStatus("st.unreach", "#C96B6B"); }
    }

    private async Task FlushKeysAsync()
    {
        _keyFlush.Stop();
        var delta = _pendingDelta;
        _pendingDelta = 0;
        if (delta == 0 || !_sonos.IsConnected) return;
        var max = _settings.MaxVolume;
        if (delta > 0) delta = Math.Min(delta, Math.Max(0, max - _volume));
        try
        {
            var volume = delta == 0 ? _volume : await _sonos.AdjustVolumeAsync(delta);
            if (volume > max) { await _sonos.SetVolumeAsync(max); volume = max; }
            ShowVolume(volume);
            if (_mute) { _mute = false; MuteButton.Content = Loc.T("btn.mute"); }
            ShowOsd();
        }
        catch { SetStatus("st.unreach", "#C96B6B"); }
    }

    private void ShowOsd()
    {
        if (OsdBox.IsChecked == true) _osd.ShowVolume(_groupLabel, _sourceLabel, _volume, _mute);
    }

    private void ShowVolume(int volume)
    {
        _volume = volume;
        _loading = true;
        VolumeSlider.Value = volume;
        VolumeValue.Text = volume.ToString();
        _loading = false;
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _refreshingCombos) return;
        _settings.InterceptKeys = InterceptBox.IsChecked == true;
        _settings.MediaKeys = MediaBox.IsChecked == true;
        _settings.OnlyLineIn = OnlyLineInBox.IsChecked == true;
        _settings.ShowOsd = OsdBox.IsChecked == true;
        _settings.LockRoom = LockRoomBox.IsChecked == true;
        _settings.LockOutput = LockOutputBox.IsChecked == true;
        _settings.Step = Step;
        _settings.MaxVolume = MaxValues[Math.Max(0, MaxPicker.SelectedIndex)];
        _settings.DefaultVolume = DefaultVolumeValues[Math.Max(0, DefVolPicker.SelectedIndex)];
        _settings.SleepAction = Math.Max(0, SleepPicker.SelectedIndex);
        _settings.Language = LangModes[Math.Max(0, LangPicker.SelectedIndex)];
        _settings.Save();
        Loc.Set(_settings.Language);
        ApplyLocks();
        EvaluateOutput();
        if (_syncing) return;
        _syncing = true;
        try
        {
            var want = AutostartBox.IsChecked == true;
            if (want != Autostart.IsEnabled) Autostart.Set(want);
        }
        catch (Exception ex) { SetHint("hint.autofail", ex.Message); }
        finally { _syncing = false; }
    }

    // ---- Discovery & rooms ----

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        SetStatus("st.searching", "#D5A85C");
        SetHint("hint.searching");
        try
        {
            var ips = await SonosDiscovery.FindIpsAsync();
            if (!string.IsNullOrEmpty(_settings.LastIp) && !ips.Contains(_settings.LastIp)) ips.Add(_settings.LastIp);
            if (ips.Count == 0)
            {
                SetHint("hint.noplayers");
                SetStatus("st.notfound", "#C96B6B");
                _scanRetry.Start();
                return;
            }
            var rooms = await _sonos.LoadRoomsAsync(ips);
            try { _lineInSuggestion = await _sonos.FindLineInRoomAsync(); } catch { _lineInSuggestion = null; }

            // The saved room always wins when locked. Nothing is ever picked automatically.
            var target = _settings.RoomName;
            if (LockRoomBox.IsChecked != true && DevicePicker.SelectedItem is Room selected) target = selected.Name;
            _rooms.Clear();
            foreach (var room in rooms) _rooms.Add(room);

            var match = target is null ? null : _rooms.FirstOrDefault(r => r.Name == target);
            if (match is not null)
            {
                SetHint("hint.rooms", _rooms.Count);
                DevicePicker.SelectedItem = match;
            }
            else if (target is null)
            {
                SetHint("hint.pickroom");
                SetStatus("st.config", "#D5A85C");
            }
            else
            {
                SourceText.Text = Loc.T("src.locked", target);
                SetHint("hint.roomgone", target);
                SetStatus("st.roomgone", "#C96B6B");
                _scanRetry.Start();
            }
            ApplyLocks();
            UpdateWizardHints();
        }
        catch (Exception ex)
        {
            SetHint("hint.scanfail", ex.Message);
            SetStatus("st.error", "#C96B6B");
            _scanRetry.Start();
        }
    }

    private async void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not Room room) return;
        _poll.Stop();
        VolumeSlider.IsEnabled = false;
        try
        {
            _sonos.SelectRoom(room.Name);
            _groupSignature = "";
            _settings.RoomName = room.Name;
            _settings.LastIp = room.Ip;
            _settings.Save();
            ApplyLocks();
            _wizSync = true; WizRooms.SelectedItem = room; _wizSync = false;
            UpdateWizardNav();
            await RefreshStateAsync(force: true);
            if (!_sonos.IsConnected) return;
            VolumeSlider.IsEnabled = true;
            _poll.Start();
            EvaluateOutput();
        }
        catch (Exception ex)
        {
            SetHint("hint.connfail", ex.Message);
            SetStatus("st.connfail", "#C96B6B");
        }
    }

    private async Task RefreshStateAsync(bool force = false)
    {
        if (!_sonos.IsConnected && !force) return;
        if (!force && (_sliderPending || _keyFlush.IsEnabled || _busy || _roomPending > 0)) return;
        try
        {
            var state = await _sonos.RefreshAsync();
            ShowVolume(state.Volume);
            _mute = state.Muted;
            MuteButton.Content = Loc.T(_mute ? "btn.unmute" : "btn.mute");
            _isLineIn = state.IsLineIn;
            _groupLabel = state.GroupLabel;
            _sourceLabel = state.Source;
            SourceText.Text = $"{state.GroupLabel} · {state.Source}";
            SetHint("hint.control", state.GroupLabel);
            SetStatus("st.connected", "#78B88A");
            RebuildGroupPanel();
            UpdateRoomSliders(state.Rooms);
            if (!_defaultVolumeDone) { _defaultVolumeDone = true; await ApplyDefaultVolumeAsync(state); }
        }
        catch (Exception ex)
        {
            SetStatus("st.unreach", "#C96B6B");
            if (force) SetHint("hint.connfail", ex.Message);
        }
    }

    // ---- Linking rooms ----

    private void RebuildGroupPanel()
    {
        var group = _sonos.CurrentGroup;
        var self = _sonos.RoomName;
        if (group is null) return;
        var inGroup = group.Rooms.Select(r => r.Name).ToHashSet();
        var others = _sonos.AllRooms().Where(r => r.Name != self).ToList();
        var signature = string.Join("|", others.Select(r => r.Name + ":" + inGroup.Contains(r.Name))) + "#" + self;
        if (signature == _groupSignature) return;
        _groupSignature = signature;

        GroupPanel.Children.Clear();
        GroupHint.Visibility = others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < others.Count; i++)
        {
            if (i > 0) GroupPanel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Panel2") });
            var box = new CheckBox { Content = others[i].Name, Tag = others[i].Name, IsChecked = inGroup.Contains(others[i].Name), Margin = new Thickness(0, 14, 0, 14) };
            box.Click += GroupToggle_Click;
            GroupPanel.Children.Add(box);
        }
    }

    private async void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var name = (string)box.Tag;
        var join = box.IsChecked == true;
        _busy = true;
        try
        {
            if (join) await _sonos.JoinAsync(name); else await _sonos.LeaveAsync(name);
            await Task.Delay(700);
        }
        catch (Exception ex) { SetHint(join ? "hint.linkfail" : "hint.unlinkfail", ex.Message); }
        finally { _busy = false; }
        _groupSignature = "";
        await RefreshStateAsync();
    }

    // ---- Tabs ----

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomeTab is null || RoomsTab is null || SettingsTab is null) return; // fires while the window is being built
        var tag = (string)((FrameworkElement)sender).Tag;
        HomeTab.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        RoomsTab.Visibility = tag == "rooms" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTab.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    // ---- First-run setup wizard ----

    private void BuildDots()
    {
        for (var i = 0; i <= WizLast; i++)
            WizardDots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(4, 0, 4, 0), Fill = new SolidColorBrush(Color.FromRgb(58, 58, 61)) });
    }

    private void ShowWizard(int step)
    {
        if (step == 0)
        {
            // First run: sensible defaults. Later runs: mirror the current settings.
            var first = NeedsSetup;
            WizAutostart.IsChecked = first || AutostartBox.IsChecked == true;
            WizOsd.IsChecked = first || OsdBox.IsChecked == true;
            WizLock.IsChecked = first || (LockRoomBox.IsChecked == true && LockOutputBox.IsChecked == true);
        }
        Wizard.Visibility = Visibility.Visible;
        GoToStep(step);
    }

    private void GoToStep(int step)
    {
        _wizStep = step;
        StepWelcome.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepRoom.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepOutput.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepPrefs.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        WizardBack.Visibility = step == 0 ? Visibility.Hidden : Visibility.Visible;
        WizardNext.Content = Loc.T(step == 0 ? "w.start" : step == WizLast ? "w.finish" : "w.continue");
        for (var i = 0; i < WizardDots.Children.Count; i++)
            ((System.Windows.Shapes.Ellipse)WizardDots.Children[i]).Fill = new SolidColorBrush(i == step ? Colors.White : Color.FromRgb(58, 58, 61));
        if (step == 1) UpdateWizardHints();
        if (step == 2) { LoadOutputs(); UpdateWizardOutputHint(); }
        if (step == 4) BuildSummary();
        UpdateWizardNav();
    }

    private void UpdateWizardNav()
    {
        if (WizardNext is null) return;
        WizardNext.IsEnabled = _wizStep switch
        {
            1 => DevicePicker.SelectedItem is Room,
            2 => _settings.OutputId is not null,
            _ => true
        };
    }

    private void UpdateWizardHints()
    {
        if (WizRoomHint is null) return;
        WizRoomHint.Text = _rooms.Count == 0 ? Loc.T("w.hint.none")
            : _lineInSuggestion is not null ? Loc.T("w.hint.sugg", _lineInSuggestion)
            : Loc.T("w.hint.tip");
        UpdateWizardNav();
    }

    private void UpdateWizardOutputHint()
    {
        var current = AudioOutputs.DefaultId();
        var name = AudioOutputs.List().FirstOrDefault(o => o.Id == current)?.Name;
        WizOutputHint.Text = name is null ? "" : Loc.T("w.out.active", name);
        WizUseCurrent.IsEnabled = name is not null;
    }

    private void BuildSummary()
    {
        static string YesNo(bool? v) => Loc.T(v == true ? "yes" : "no");
        WizSummary.Text = Loc.T("w.sum.room", _settings.RoomName ?? "—") + "\n" +
                          Loc.T("w.sum.out", _settings.OutputName ?? "—") + "\n" +
                          Loc.T("w.sum.auto", YesNo(WizAutostart.IsChecked)) + "\n" +
                          Loc.T("w.sum.osd", YesNo(WizOsd.IsChecked)) + "\n" +
                          Loc.T("w.sum.lock", YesNo(WizLock.IsChecked));
    }

    private void WizardNext_Click(object sender, RoutedEventArgs e)
    {
        if (_wizStep < WizLast) GoToStep(_wizStep + 1); else FinishWizard();
    }

    private void WizardBack_Click(object sender, RoutedEventArgs e)
    {
        if (_wizStep > 0) GoToStep(_wizStep - 1);
    }

    private void WizardSkip_Click(object sender, RoutedEventArgs e) => CloseWizard();

    private void WizRooms_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_wizSync || WizRooms.SelectedItem is not Room room) return;
        if (!ReferenceEquals(DevicePicker.SelectedItem, room)) DevicePicker.SelectedItem = room;
        UpdateWizardNav();
    }

    private void WizOutputs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOutputs || WizOutputs.SelectedItem is not AudioOutput { Available: true } output) return;
        if (!ReferenceEquals(OutputPicker.SelectedItem, output)) OutputPicker.SelectedItem = output;
        UpdateWizardNav();
    }

    private void WizUseCurrent_Click(object sender, RoutedEventArgs e)
    {
        var current = AudioOutputs.DefaultId();
        var item = _outputs.FirstOrDefault(o => o.Id == current);
        if (item is not null) WizOutputs.SelectedItem = item;
    }

    private void CloseWizard()
    {
        _settings.SetupDone = true;
        _settings.Save();
        Wizard.Visibility = Visibility.Collapsed;
        ApplyLocks();
        EvaluateOutput();
    }

    private void FinishWizard()
    {
        AutostartBox.IsChecked = WizAutostart.IsChecked;
        OsdBox.IsChecked = WizOsd.IsChecked;
        LockRoomBox.IsChecked = WizLock.IsChecked;
        LockOutputBox.IsChecked = WizLock.IsChecked;
        _settings.SetupDone = true;
        _settings.Save();
        if (WizInstall.Visibility == Visibility.Visible && WizInstall.IsChecked == true && !Installer.IsInstalledCopy)
        {
            Install_Click(this, new RoutedEventArgs()); // installs, starts the installed copy and exits
            return;
        }
        CloseWizard();
        _tray?.ShowBalloonTip(3500, "Sonos Desktop Bridge", Loc.T("tray.ready"), System.Windows.Forms.ToolTipIcon.Info);
    }

    private void RestartSetup_Click(object sender, RoutedEventArgs e) => ShowWizard(0);

    // ---- Per-room volume ----

    private void UpdateRoomSliders(List<RoomVolume> rooms)
    {
        var signature = string.Join("|", rooms.Select(r => r.Name));
        _loadingRooms = true;
        if (signature != _roomSignature)
        {
            _roomSignature = signature;
            _roomSliders.Clear();
            RoomVolumePanel.Children.Clear();
            RoomVolumeHint.Visibility = rooms.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
            if (rooms.Count >= 2)
            {
                foreach (var room in rooms)
                {
                    var name = new TextBlock { Text = room.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
                    var value = new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("TextMuted") };
                    DockPanel.SetDock(value, Dock.Right);
                    var header = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                    header.Children.Add(value);
                    header.Children.Add(name);
                    var slider = new Slider { Minimum = 0, Maximum = 100, Tag = room.Name };
                    slider.ValueChanged += RoomSlider_ValueChanged;
                    RoomVolumePanel.Children.Add(header);
                    RoomVolumePanel.Children.Add(slider);
                    _roomSliders[room.Name] = (slider, value);
                }
            }
        }
        foreach (var room in rooms)
        {
            if (room.Volume < 0 || !_roomSliders.TryGetValue(room.Name, out var item)) continue;
            item.Slider.Value = room.Volume;
            item.Value.Text = room.Volume + " %";
        }
        _loadingRooms = false;
    }

    private async void RoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingRooms || !_sonos.IsConnected || !IsLoaded) return;
        var slider = (Slider)sender;
        var name = (string)slider.Tag;
        var volume = (int)Math.Round(slider.Value);
        if (_roomSliders.TryGetValue(name, out var item)) item.Value.Text = volume + " %";

        if (_roomDelay.TryGetValue(name, out var previous)) previous.Cancel();
        var source = _roomDelay[name] = new CancellationTokenSource();
        _roomPending++;
        var ok = false;
        try
        {
            await Task.Delay(180, source.Token);
            await _sonos.SetRoomVolumeAsync(name, volume);
            ok = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetHint("hint.setfail", ex.Message); }
        finally { _roomPending--; }
        if (ok) await RefreshStateAsync(); // updates the group volume shown above
    }

    // ---- Install ----

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Installer.Install();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex) { InstallText.Text = Loc.T("app.installfail", ex.Message); }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("uninstall.confirm"), "Sonos Desktop Bridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Installer.Uninstall();
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex) { InstallText.Text = Loc.T("app.uninstallfail", ex.Message); }
    }

    // ---- Volume controls ----

    private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !_sonos.IsConnected || !IsLoaded) return;
        VolumeValue.Text = ((int)Math.Round(VolumeSlider.Value)).ToString();
        _sliderPending = true;
        _sliderDelay?.Cancel();
        _sliderDelay = new CancellationTokenSource();
        var source = _sliderDelay;
        var token = source.Token;
        var ok = false;
        try
        {
            await Task.Delay(180, token);
            _volume = (int)Math.Round(VolumeSlider.Value);
            await _sonos.SetVolumeAsync(_volume);
            ok = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetHint("hint.setfail", ex.Message); }
        finally
        {
            if (ReferenceEquals(_sliderDelay, source)) _sliderPending = false;
        }
        if (ok && !_sliderPending) await RefreshStateAsync();
    }

    private void Step_Click(object sender, RoutedEventArgs e) =>
        OnVolumeKey((string?)((FrameworkElement)sender).Tag == "up" ? VolumeKeyHook.VolumeUp : VolumeKeyHook.VolumeDown);

    private async Task ToggleMuteAsync()
    {
        if (!_sonos.IsConnected) return;
        try
        {
            await _sonos.SetMuteAsync(!_mute);
            _mute = !_mute;
            MuteButton.Content = Loc.T(_mute ? "btn.unmute" : "btn.mute");
            ShowOsd();
        }
        catch (Exception ex) { SetHint("hint.mutefail", ex.Message); }
    }

    private async void Mute_Click(object sender, RoutedEventArgs e) => await ToggleMuteAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();
}
