using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace SonosDesktopBridge;

internal sealed class AppSettings
{
    public string? RoomName { get; set; }
    public string? LastIp { get; set; }
    public string? OutputId { get; set; }
    public int Step { get; set; } = 2;
    public bool InterceptKeys { get; set; } = true;
    public bool OnlyLineIn { get; set; }
    public bool ShowOsd { get; set; } = true;

    private static string PathFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonosDesktopBridge", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this));
        }
        catch { /* Settings are a convenience only. */ }
    }
}

internal sealed record AudioOutput(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Windows playback devices (Core Audio).</summary>
internal static class AudioOutputs
{
    public static List<AudioOutput> List()
    {
        var list = new List<AudioOutput>();
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            {
                list.Add(new AudioOutput(d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch { }
        return list;
    }

    public static string? DefaultId()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var d = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            return d.ID;
        }
        catch { return null; }
    }

    /// <summary>Keeps a device at 100 % and unmuted so the Sonos volume is the only one that matters.</summary>
    public static void EnsureFull(string id)
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var d = enumerator.GetDevice(id);
            var v = d.AudioEndpointVolume;
            if (v.Mute) v.Mute = false;
            if (v.MasterVolumeLevelScalar < 0.995f) v.MasterVolumeLevelScalar = 1f;
        }
        catch { }
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SonosDesktopBridge";

    public static bool IsEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) is not null; }
    }

    public static void Set(bool enabled, string? exePath = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{exePath ?? Environment.ProcessPath}\" --tray");
        else k.DeleteValue(ValueName, false);
    }
}

/// <summary>Global low-level hook that swallows the Windows volume keys.</summary>
internal sealed class VolumeKeyHook : IDisposable
{
    public const int VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF;

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly HookProc _proc;
    private IntPtr _hook;

    /// <summary>Called on key-down with the virtual key code. Must return quickly.</summary>
    public Action<int>? KeyDown { get; set; }

    public VolumeKeyHook()
    {
        _proc = Callback; // keep a reference so the GC does not collect the delegate
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Impossible d'installer le hook clavier.");
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (Enabled && vk is VolumeMute or VolumeDown or VolumeUp)
            {
                var msg = (int)wParam;
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN) KeyDown?.Invoke(vk);
                return (IntPtr)1; // swallow both down and up so Windows never changes its own volume
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>When false, volume keys pass through to Windows untouched.</summary>
    public volatile bool Enabled;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
