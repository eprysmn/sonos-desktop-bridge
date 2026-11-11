using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SonosDesktopBridge;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Room> _rooms = new();
    private readonly ObservableCollection<AudioOutput> _outputs = new();
    private readonly SonosController _sonos = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _outputTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _keyFlush = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly VolumeKeyHook? _hook;
    private readonly OsdWindow _osd = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _roomSliders = new();
    private readonly Dictionary<string, CancellationTokenSource> _roomDelay = new();
    private string _roomSignature = "";
    private bool _loadingRooms;
    private int _roomPending;
    private bool _loading;
    private bool _mute;
    private bool _sliderPending;
    private bool _exiting;
    private bool _syncing;
    private bool _busy;
    private bool _loadingOutputs;
    private bool _isLineIn;
    private int _pendingDelta;
    private int _volume;
    private string _groupLabel = "";
    private string _sourceLabel = "";
    private string _groupSignature = "";
    private string? _outputId;
    private CancellationTokenSource? _sliderDelay;

    public MainWindow()
    {
        InitializeComponent();
        DevicePicker.ItemsSource = _rooms;
        OutputPicker.ItemsSource = _outputs;
        _poll.Tick += async (_, _) => await RefreshStateAsync();
        _outputTimer.Tick += (_, _) => EvaluateOutput();
        _keyFlush.Tick += async (_, _) => await FlushKeysAsync();
        Loaded += async (_, _) => await ScanAsync();
        Closing += MainWindow_Closing;

        StepPicker.SelectedIndex = Math.Clamp(_settings.Step, 1, 5) - 1;
        InterceptBox.IsChecked = _settings.InterceptKeys;
        OnlyLineInBox.IsChecked = _settings.OnlyLineIn;
        OsdBox.IsChecked = _settings.ShowOsd;
        AutostartBox.IsChecked = Autostart.IsEnabled;
        if (Installer.IsInstalledCopy)
        {
            InstallText.Text = "Installée : visible dans le menu Démarrer et dans Paramètres > Applications.";
            InstallButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            InstallText.Text = "Version portable. Installe-la pour l'avoir dans le menu Démarrer et dans la liste des applications Windows.";
            UninstallButton.Visibility = Visibility.Collapsed;
        }

        // First run: assume the current Windows output is the Sonos one, but only start pinning it
        // at 100 % once the user has chosen it explicitly in the list.
        _outputId = _settings.OutputId ?? AudioOutputs.DefaultId();
        LoadOutputs();

        try
        {
            _hook = new VolumeKeyHook { KeyDown = OnVolumeKey };
        }
        catch (Exception ex)
        {
            InterceptBox.IsEnabled = false;
            InterceptBox.IsChecked = false;
            DeviceHint.Text = ex.Message;
        }
        BuildTray();
        if (Environment.GetCommandLineArgs().Contains("--tray"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }
        _outputTimer.Start();
        EvaluateOutput();
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
        _tray.ContextMenuStrip.Items.Add("Ouvrir", null, (_, _) => ShowFromTray());
        _tray.ContextMenuStrip.Items.Add("Quitter", null, (_, _) => { _exiting = true; Close(); });
        _tray.DoubleClick += (_, _) => ShowFromTray();
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
            return;
        }
        _poll.Stop();
        _outputTimer.Stop();
        _hook?.Dispose();
        _osd.Close();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
    }

    // ---- Windows output awareness ----

    private void LoadOutputs()
    {
        _loadingOutputs = true;
        _outputs.Clear();
        foreach (var o in AudioOutputs.List()) _outputs.Add(o);
        OutputPicker.SelectedItem = _outputs.FirstOrDefault(o => o.Id == _outputId);
        _loadingOutputs = false;
    }

    private void OutputPicker_DropDownOpened(object? sender, EventArgs e)
    {
        // Devices come and go (Bluetooth headsets): refresh the list each time it is opened.
        if (!_loadingOutputs) LoadOutputs();
    }

    private void OutputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOutputs || OutputPicker.SelectedItem is not AudioOutput output) return;
        _outputId = output.Id;
        _settings.OutputId = output.Id;
        _settings.Save();
        EvaluateOutput();
    }

    /// <summary>Decides whether the volume keys drive Sonos or are left to Windows.</summary>
    private void EvaluateOutput()
    {
        var current = AudioOutputs.DefaultId();
        var onSonosOutput = _outputId is not null && string.Equals(current, _outputId, StringComparison.OrdinalIgnoreCase);
        var sourceOk = OnlyLineInBox.IsChecked != true || _isLineIn;
        var wanted = InterceptBox.IsChecked == true && _sonos.IsConnected;
        var active = wanted && onSonosOutput && sourceOk;
        if (_hook is not null) _hook.Enabled = active;
        if (onSonosOutput && wanted && _settings.OutputId is not null) AudioOutputs.EnsureFull(current!);

        if (!_sonos.IsConnected) OutputHint.Text = "En attente de la connexion Sonos.";
        else if (InterceptBox.IsChecked != true) OutputHint.Text = "Interception des touches désactivée.";
        else if (!onSonosOutput)
        {
            var name = _outputs.FirstOrDefault(o => o.Id == current)?.Name ?? AudioOutputs.List().FirstOrDefault(o => o.Id == current)?.Name ?? "autre sortie";
            OutputHint.Text = $"Sortie Windows active : {name}. Le volume reste géré par Windows.";
        }
        else if (!sourceOk) OutputHint.Text = "Sonos n'est pas sur l'entrée source locale : les touches restent à Windows.";
        else OutputHint.Text = _settings.OutputId is null
            ? "Les touches pilotent Sonos. Choisis ta sortie dans la liste pour que Windows reste automatiquement à 100 %."
            : "Cette sortie est active : les touches de volume pilotent Sonos et Windows reste à 100 %.";
    }

    // ---- Keys & OSD ----

    private int Step => StepPicker.SelectedIndex + 1;

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

    private async Task FlushKeysAsync()
    {
        _keyFlush.Stop();
        var delta = _pendingDelta;
        _pendingDelta = 0;
        if (delta == 0 || !_sonos.IsConnected) return;
        try
        {
            var volume = await _sonos.AdjustVolumeAsync(delta);
            ShowVolume(volume);
            if (_mute) { _mute = false; MuteButton.Content = "Muet"; }
            ShowOsd();
        }
        catch { SetStatus("Sonos injoignable", "#C96B6B"); }
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
        if (!IsLoaded) return;
        _settings.InterceptKeys = InterceptBox.IsChecked == true;
        _settings.OnlyLineIn = OnlyLineInBox.IsChecked == true;
        _settings.ShowOsd = OsdBox.IsChecked == true;
        _settings.Step = Step;
        _settings.Save();
        EvaluateOutput();
        if (_syncing) return;
        _syncing = true;
        try
        {
            var want = AutostartBox.IsChecked == true;
            if (want != Autostart.IsEnabled) Autostart.Set(want);
        }
        catch (Exception ex) { DeviceHint.Text = "Démarrage automatique impossible : " + ex.Message; }
        finally { _syncing = false; }
    }

    // ---- Discovery & rooms ----

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        SetStatus("Recherche…", "#D5A85C");
        DeviceHint.Text = "Découverte des lecteurs Sonos sur le réseau local…";
        try
        {
            var ips = await SonosDiscovery.FindIpsAsync();
            if (!string.IsNullOrEmpty(_settings.LastIp) && !ips.Contains(_settings.LastIp)) ips.Add(_settings.LastIp);
            if (ips.Count == 0)
            {
                DeviceHint.Text = "Aucun lecteur détecté. Vérifie que le PC et les Sonos sont sur le même réseau local et que le pare-feu autorise la découverte réseau.";
                SetStatus("Introuvable", "#C96B6B");
                return;
            }
            var rooms = await _sonos.LoadRoomsAsync(ips);
            var current = (DevicePicker.SelectedItem as Room)?.Name ?? _settings.RoomName ?? await _sonos.FindLineInRoomAsync();
            _rooms.Clear();
            foreach (var room in rooms) _rooms.Add(room);
            DeviceHint.Text = $"{_rooms.Count} pièce(s) détectée(s).";
            DevicePicker.SelectedItem = _rooms.FirstOrDefault(r => r.Name == current) ?? _rooms[0];
        }
        catch (Exception ex)
        {
            DeviceHint.Text = "Échec de la découverte : " + ex.Message;
            SetStatus("Erreur", "#C96B6B");
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
            await RefreshStateAsync(force: true);
            if (!_sonos.IsConnected) return;
            VolumeSlider.IsEnabled = true;
            _poll.Start();
            EvaluateOutput();
        }
        catch (Exception ex)
        {
            DeviceHint.Text = "Impossible de piloter cette pièce : " + ex.Message;
            SetStatus("Échec connexion", "#C96B6B");
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
            MuteButton.Content = _mute ? "Réactiver" : "Muet";
            _isLineIn = state.IsLineIn;
            _groupLabel = state.GroupLabel;
            _sourceLabel = state.Source;
            SourceText.Text = $"{state.GroupLabel} · {state.Source}";
            DeviceHint.Text = $"Contrôle du groupe « {state.GroupLabel} ».";
            SetStatus("Connecté", "#78B88A");
            RebuildGroupPanel();
            UpdateRoomSliders(state.Rooms);
        }
        catch (Exception ex)
        {
            SetStatus("Sonos injoignable", "#C96B6B");
            if (force) DeviceHint.Text = "Impossible de piloter cette pièce : " + ex.Message;
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
        catch (Exception ex) { DeviceHint.Text = (join ? "Liaison impossible : " : "Déliaison impossible : ") + ex.Message; }
        finally { _busy = false; }
        _groupSignature = "";
        await RefreshStateAsync();
    }

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
        catch (Exception ex) { DeviceHint.Text = "Erreur de réglage : " + ex.Message; }
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
        catch (Exception ex) { InstallText.Text = "Installation impossible : " + ex.Message; }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Désinstaller Sonos Desktop Bridge ?", "Sonos Desktop Bridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Installer.Uninstall();
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex) { InstallText.Text = "Désinstallation impossible : " + ex.Message; }
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
        catch (Exception ex) { DeviceHint.Text = "Erreur de réglage : " + ex.Message; }
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
            MuteButton.Content = _mute ? "Réactiver" : "Muet";
            ShowOsd();
        }
        catch (Exception ex) { DeviceHint.Text = "Erreur de sourdine : " + ex.Message; }
    }

    private async void Mute_Click(object sender, RoutedEventArgs e) => await ToggleMuteAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }
}
