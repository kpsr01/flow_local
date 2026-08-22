using System.ComponentModel;
using System.Runtime.InteropServices;
using FlowLocal.Core;

namespace FlowLocal.App;

internal sealed class SendInputInsertion
{
    private readonly Func<InputNativeMethods.Input[], uint> _send;

    internal SendInputInsertion()
        : this(inputs => InputNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<InputNativeMethods.Input>()))
    {
    }

    internal SendInputInsertion(Func<InputNativeMethods.Input[], uint> send)
    {
        _send = send;
    }
    internal InsertionAttempt TryInsert(ActiveTarget target, string text)
    {
        var rejection = RejectionReason(target);
        if (rejection is not null)
            return InsertionAttempt.Unsupported(rejection);

        var inputs = new InputNativeMethods.Input[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = Unicode(text[index], keyUp: false);
            inputs[index * 2 + 1] = Unicode(text[index], keyUp: true);
        }

        return Send(inputs, _send);
    }

    internal static InsertionAttempt SendPasteChord()
    {
        var inputs = new[]
        {
            VirtualKey(InputNativeMethods.VkControl, keyUp: false),
            VirtualKey(InputNativeMethods.VkV, keyUp: false),
            VirtualKey(InputNativeMethods.VkV, keyUp: true),
            VirtualKey(InputNativeMethods.VkControl, keyUp: true)
        };
        return Send(inputs, inputs => InputNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<InputNativeMethods.Input>()));
    }

    private static string? RejectionReason(ActiveTarget target)
    {
        if (target.IsTerminal)
            return "SendInput is disabled for terminal targets.";
        if (target.IsPasswordField != false)
            return "SendInput is disabled for protected or unknown fields.";
        if (!target.IsInjectionSafe || target.CurrentIntegrityRid is null || target.TargetIntegrityRid is null ||
            target.TargetIntegrityRid > target.CurrentIntegrityRid)
            return "SendInput is disabled because the target integrity level is unsafe or unknown.";
        return null;
    }

    private static InputNativeMethods.Input Unicode(char codeUnit, bool keyUp) => new()
    {
        Type = InputNativeMethods.InputKeyboard,
        Data = new InputNativeMethods.InputUnion
        {
            Keyboard = new InputNativeMethods.KeyboardInput
            {
                ScanCode = codeUnit,
                Flags = InputNativeMethods.KeyEventUnicode | (keyUp ? InputNativeMethods.KeyEventKeyUp : 0)
            }
        }
    };

    private static InputNativeMethods.Input VirtualKey(ushort key, bool keyUp) => new()
    {
        Type = InputNativeMethods.InputKeyboard,
        Data = new InputNativeMethods.InputUnion
        {
            Keyboard = new InputNativeMethods.KeyboardInput
            {
                VirtualKey = key,
                Flags = keyUp ? InputNativeMethods.KeyEventKeyUp : 0
            }
        }
    };

    private static InsertionAttempt Send(
        InputNativeMethods.Input[] inputs,
        Func<InputNativeMethods.Input[], uint> send)
    {
        if (inputs.Length == 0)
            return InsertionAttempt.Inserted();

        var sent = send(inputs);
        if (sent == inputs.Length)
            return InsertionAttempt.Inserted();

        var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return sent == 0
            ? InsertionAttempt.Failed($"SendInput emitted 0 of {inputs.Length} events: {error}")
            : InsertionAttempt.UnknownSideEffect($"SendInput emitted {sent} of {inputs.Length} events: {error}");
    }
}
