using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SonosDesktopBridge;

/// <summary>Small always-on-top, click-through volume indicator shown at the bottom of the screen.</summary>
internal sealed class OsdWindow : Window
{
    private const double BarWidth = 296;
    private readonly TextBlock _room = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly TextBlock _source = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _value = new() { FontSize = 34, FontWeight = FontWeights.Light, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _unit = new() { Text = "%", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 7) };
    private readonly Border _fill = new() { Height = 10, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.White };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1800) };
    private int _generation;

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Opacity = 0;

        var track = new Border { Height = 10, Width = BarWidth, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)) };
        var bar = new Grid { Width = BarWidth, Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(track);
        bar.Children.Add(_fill);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_room);
        texts.Children.Add(_source);
        var number = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        number.Children.Add(_value);
        number.Children.Add(_unit);
        var top = new DockPanel { Width = BarWidth };
        DockPanel.SetDock(number, Dock.Right);
        top.Children.Add(number);
        top.Children.Add(texts);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bar);

        Content = new Border
        {
            Margin = new Thickness(24),
            Padding = new Thickness(22, 18, 22, 22),
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x16, 0x17)),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = stack
        };

        SourceInitialized += (_, _) =>
        {
            // Never take focus and let every click pass through to the window below.
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, style | 0x08000000 | 0x00000080 | 0x00000020);
        };
        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
    }

    public void ShowVolume(string room, string source, int volume, bool muted)
    {
        _room.Text = room;
        _source.Text = source;
        _value.Text = muted ? "Muet" : volume.ToString();
        _unit.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        _fill.Width = Math.Max(10, BarWidth * Math.Clamp(volume, 0, 100) / 100.0);
        _fill.Opacity = muted ? 0.35 : 1;

        _generation++;
        if (!IsVisible) { Opacity = 0; Show(); }
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 36;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _hide.Stop();
        _hide.Start();
    }

    private void FadeOut()
    {
        var generation = _generation;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) => { if (generation == _generation) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
