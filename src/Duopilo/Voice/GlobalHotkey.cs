using System.Runtime.InteropServices;
using Duopilo.Hosting;
using Duopilo.Localization;

namespace Duopilo.Voice;

/// <summary>Tastenkürzel wie "Ctrl+Alt+K" oder "Shift+F12" → Modifikatoren und virtueller Tastencode (Windows).</summary>
public sealed record HotkeyBinding(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["pause"] = 0x13, ["scrolllock"] = 0x91, ["numlock"] = 0x90, ["tab"] = 0x09,
        ["backspace"] = 0x08, ["enter"] = 0x0D, ["escape"] = 0x1B, ["esc"] = 0x1B, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27,
        ["down"] = 0x28, ["multiply"] = 0x6A, ["add"] = 0x6B, ["subtract"] = 0x6D, ["divide"] = 0x6F,
    };

    /// <summary>Liest ein Tastenkürzel. Leer = keins (false, ohne Fehler).</summary>
    public static bool TryParse(string? text, out HotkeyBinding? binding, out string? error)
    {
        binding = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control" or "strg":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift" or "umschalt":
                    modifiers |= ModShift;
                    continue;
                case "win" or "windows":
                    modifiers |= ModWin;
                    continue;
            }

            if (key is not null)
            {
                error = Loc.T("voice.hotkey.moreThanOneKey", text);
                return false;
            }

            key = KeyCode(raw);
            if (key is null)
            {
                error = Loc.T("voice.hotkey.unknownKey", text, raw);
                return false;
            }
        }

        if (key is null)
        {
            error = Loc.T("voice.hotkey.noKey", text);
            return false;
        }

        binding = new HotkeyBinding(modifiers, key.Value, text.Trim());
        return true;
    }

    private static uint? KeyCode(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return char.ToUpperInvariant(key[0]);
        }

        if ((key.StartsWith('F') || key.StartsWith('f')) && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        if (key.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[6..], out var n) && n is >= 0 and <= 9)
        {
            return (uint)(0x60 + n);
        }

        return NamedKeys.TryGetValue(key, out var code) ? code : null;
    }
}

/// <summary>
/// Systemweites Tastenkürzel (RegisterHotKey) auf einem eigenen Thread mit Nachrichtenschleife – wirkt auch,
/// wenn MSFS im Vordergrund ist. Ruft den Rückruf auf diesem Thread auf.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4B50; // "KP"
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint WmUser = 0x0400;
    private const uint ModNoRepeat = 0x4000;
    private const uint PmNoRemove = 0x0000;

    private readonly Thread _thread;
    private readonly TaskCompletionSource<string?> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;

    private GlobalHotkey(HotkeyBinding binding, Action onPressed)
    {
        _thread = new Thread(() => Run(binding, onPressed)) { IsBackground = true, Name = AppNames.Product + "-Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Registriert das Tastenkürzel. Rückgabe: Hotkey oder Fehlermeldung (z. B. schon von einem anderen Programm belegt).</summary>
    public static (GlobalHotkey? Hotkey, string? Error) Register(HotkeyBinding binding, Action onPressed)
    {
        var hotkey = new GlobalHotkey(binding, onPressed);
        hotkey._thread.Start();
        var error = hotkey._registered.Task.Wait(TimeSpan.FromSeconds(5)) ? hotkey._registered.Task.Result : Loc.T("voice.hotkey.timeout");
        if (error is not null)
        {
            hotkey.Dispose();
            return (null, error);
        }

        return (hotkey, null);
    }

    private void Run(HotkeyBinding binding, Action onPressed)
    {
        _threadId = GetCurrentThreadId();

        // Nachrichtenwarteschlange anlegen, bevor jemand PostThreadMessage schickt
        PeekMessage(out _, IntPtr.Zero, WmUser, WmUser, PmNoRemove);
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, binding.Modifiers | ModNoRepeat, binding.VirtualKey))
        {
            _registered.TrySetResult(Loc.T("voice.hotkey.taken", binding.Text, Marshal.GetLastWin32Error()));
            return;
        }

        _registered.TrySetResult(null);
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmHotkey && msg.wParam == (UIntPtr)HotkeyId)
                {
                    try
                    {
                        onPressed();
                    }
                    catch (Exception)
                    {
                        // Der Rückruf darf die Schleife nicht beenden
                    }
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
