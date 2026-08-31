using System.Runtime.InteropServices;
using System.Windows.Input;
using VoltManager.Models;

namespace VoltManager.Services;

internal sealed class GlobalHotkeyService : IDisposable
{
    public const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, string> _commands = new();
    private IntPtr _hwnd;

    public IReadOnlyDictionary<string, bool> Rebind(IntPtr hwnd, GlobalHotkeySettings settings)
    {
        UnregisterAll();
        _hwnd = hwnd;
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (hwnd == IntPtr.Zero || !settings.Enabled) return result;

        Register(1, RemoteCommandProtocol.PowerSaverKey, settings.PowerSaver, result);
        Register(2, RemoteCommandProtocol.BalancedKey, settings.Balanced, result);
        Register(3, RemoteCommandProtocol.PerformanceKey, settings.Performance, result);
        Register(4, RemoteCommandProtocol.AutoKey, settings.Auto, result);
        Register(5, RemoteCommandProtocol.KeepAwakeToggleKey, settings.KeepAwakeToggle, result);
        return result;
    }

    public bool TryGetCommand(int id, out string command)
        => _commands.TryGetValue(id, out command!);

    internal static bool TryParseGesture(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = ModNoRepeat;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false; // avoid stealing ordinary single-key input globally

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win":
                case "windows": modifiers |= ModWin; break;
                default: return false;
            }
        }

        string keyText = parts[^1];
        Key key;
        if (keyText.Length == 1 && keyText[0] is >= '0' and <= '9')
            key = (Key)((int)Key.D0 + (keyText[0] - '0'));
        else if (keyText.Length == 1 && char.IsLetter(keyText[0]))
            key = (Key)((int)Key.A + (char.ToUpperInvariant(keyText[0]) - 'A'));
        else if (!Enum.TryParse(keyText, ignoreCase: true, out key) || key == Key.None)
            return false;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return false;
        virtualKey = (uint)vk;
        return true;
    }

    private void Register(int id, string command, string gesture, Dictionary<string, bool> result)
    {
        bool ok = TryParseGesture(gesture, out uint modifiers, out uint key)
            && RegisterHotKey(_hwnd, id, modifiers, key);
        result[command] = ok;
        if (ok)
            _commands[id] = command;
        else
            Logger.Warn($"Global hotkey unavailable: {command} = '{gesture}'.");
    }

    private void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (int id in _commands.Keys)
                UnregisterHotKey(_hwnd, id);
        }
        _commands.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _hwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
