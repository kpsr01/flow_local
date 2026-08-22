using System.ComponentModel;
using System.Runtime.InteropServices;
using FlowLocal.Core;

namespace FlowLocal.App;

internal sealed class ClipboardPasteInsertion
{
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(350);
    private readonly ClipboardTransaction _clipboard;
    private readonly Func<bool> _sendPaste;

    internal ClipboardPasteInsertion()
        : this(new ClipboardTransaction(), SendControlV)
    {
    }

    internal ClipboardPasteInsertion(ClipboardTransaction clipboard, Func<bool> sendPaste)
    {
        _clipboard = clipboard;
        _sendPaste = sendPaste;
    }

    internal async Task<InsertionAttempt> CopyOnlyAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _clipboard.StageAsync(text, cancellationToken).ConfigureAwait(false);
            return InsertionAttempt.Inserted();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return InsertionAttempt.Failed($"Could not copy dictated text to the clipboard: {exception.Message}");
        }
    }

    internal async Task<InsertionAttempt> InsertAsync(
        ActiveTarget target,
        string text,
        bool retainClipboard,
        CancellationToken cancellationToken)
    {
        ClipboardStage stage;
        try
        {
            stage = await _clipboard.StageAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return InsertionAttempt.Failed($"Could not stage dictated text on the clipboard: {exception.Message}");
        }

        if (!target.IsInjectionSafe || target.IsPasswordField == true)
            return InsertionAttempt.Failed("Paste is blocked for protected or higher/unknown integrity targets; dictated text remains on the clipboard.");

        bool pasted;
        try
        {
            pasted = _sendPaste();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return InsertionAttempt.UnknownSideEffect($"Could not send Ctrl+V; dictated text remains on the clipboard: {exception.Message}");
        }

        if (!pasted)
            return InsertionAttempt.Failed("Could not send Ctrl+V; dictated text remains on the clipboard.");

        if (!retainClipboard && !target.IsTerminal)
        {
            try
            {
                await _clipboard.RestoreAsync(stage, RestoreDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return InsertionAttempt.UnknownSideEffect($"Text was pasted, but the previous clipboard could not be restored: {exception.Message}");
            }
        }

        return InsertionAttempt.Inserted();
    }

    private static bool SendControlV()
    {
        var inputs = new[]
        {
            Input.Keyboard(VkControl, 0),
            Input.Keyboard((ushort)'V', 0),
            Input.Keyboard((ushort)'V', KeyeventfKeyup),
            Input.Keyboard(VkControl, KeyeventfKeyup)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private const ushort VkControl = 0x11;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;

        internal static Input Keyboard(ushort virtualKey, uint flags) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } }
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
