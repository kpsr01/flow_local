using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FlowLocal.App;

public sealed class GlobalShortcutService : IDisposable
{
    private static readonly IReadOnlyList<string> DefaultModifiers = ["Ctrl", "Win"];

    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private nint _hook;
    private readonly bool[] _modifierDown = new bool[8];
    private ModifierSet _required = ModifierSet.FromNames(DefaultModifiers);
    private bool _pressed;
    private bool _disposed;

    public GlobalShortcutService()
    {
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, 0, 0);
        if (_hook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? Cancelled;

    /// <summary>Configures the push-to-talk chord from modifier names (Ctrl, Alt, Shift, Win).
    /// Left and right variants of each modifier are equivalent.</summary>
    public void Configure(IReadOnlyList<string>? modifiers)
    {
        var required = ModifierSet.FromNames(
            modifiers is null || modifiers.Count == 0 ? DefaultModifiers : modifiers);
        var wasPressed = _pressed;
        _required = required;
        if (wasPressed && !IsChordSatisfied())
        {
            // The new chord is no longer held; finish any in-flight push-to-talk session.
            _pressed = false;
            Post(Released);
        }
    }

    public IReadOnlyList<string> CurrentModifiers => _required.Names;

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var key = (int)Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam).VirtualKeyCode;
            var down = wParam == NativeMethods.WmKeyDown || wParam == NativeMethods.WmSysKeyDown;
            var up = wParam == NativeMethods.WmKeyUp || wParam == NativeMethods.WmSysKeyUp;

            if (down || up)
            {
                SetModifier(key, down);

                if (down && key == NativeMethods.VkEscape && _pressed)
                {
                    Post(Cancelled);
                }
                else if (!_pressed && IsChordSatisfied())
                {
                    _pressed = true;
                    Post(Pressed);
                }
                else if (_pressed && !IsChordSatisfied())
                {
                    _pressed = false;
                    Post(Released);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsChordSatisfied()
    {
        var groups = _required.Groups;
        for (var group = 0; group < groups.Length; group++)
        {
            if (!groups[group]) continue;
            if (!(_modifierDown[group * 2] || _modifierDown[group * 2 + 1])) return false;
        }
        return true;
    }

    private void Post(EventHandler? handler)
    {
        if (handler is not null)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => handler(this, EventArgs.Empty));
        }
    }

    private void SetModifier(int key, bool down)
    {
        var slot = key switch
        {
            NativeMethods.VkLControl => 0,
            NativeMethods.VkRControl => 1,
            0xA4 => 2,
            0xA5 => 3,
            0xA0 => 4,
            0xA1 => 5,
            NativeMethods.VkLWin => 6,
            NativeMethods.VkRWin => 7,
            _ => -1
        };
        if (slot >= 0) _modifierDown[slot] = down;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        GC.SuppressFinalize(this);
    }

    private readonly struct ModifierSet(bool[] groups, string[] names)
    {
        // Group order: Ctrl, Alt, Shift, Win — two tracked keys per group (left/right).
        public bool[] Groups { get; } = groups;
        public string[] Names { get; } = names;

        public static ModifierSet FromNames(IReadOnlyList<string> names)
        {
            var groups = new bool[4];
            foreach (var name in names)
            {
                var index = name.Trim().ToLowerInvariant() switch
                {
                    "ctrl" or "control" => 0,
                    "alt" or "menu" => 1,
                    "shift" => 2,
                    "win" or "windows" => 3,
                    _ => -1
                };
                if (index >= 0) groups[index] = true;
            }

            var selected = new List<string>();
            if (groups[0]) selected.Add("Ctrl");
            if (groups[1]) selected.Add("Alt");
            if (groups[2]) selected.Add("Shift");
            if (groups[3]) selected.Add("Win");
            return new ModifierSet(groups, [.. selected]);
        }
    }
}
