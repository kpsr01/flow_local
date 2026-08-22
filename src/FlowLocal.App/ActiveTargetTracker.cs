using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using FlowLocal.Core;

namespace FlowLocal.App;

public static class TargetPolicy
{
    private static readonly HashSet<string> TerminalExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe", "wt", "wt.exe",
        "windowsterminal", "windowsterminal.exe", "conhost", "conhost.exe", "wsl", "wsl.exe",
        "bash", "bash.exe", "ubuntu", "ubuntu.exe"
    };

    public static bool IsInjectionSafe(int? currentRid, int? targetRid) =>
        currentRid.HasValue && targetRid.HasValue && targetRid.Value <= currentRid.Value;

    public static bool IsTerminal(string executableName, string? windowClassName) =>
        TerminalExecutables.Contains(executableName) ||
        string.Equals(windowClassName, "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(windowClassName, "CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase);

    public static bool IsSameProcessIdentity(
        int expectedPid,
        int actualPid,
        DateTimeOffset? expectedStart,
        DateTimeOffset? actualStart) =>
        expectedPid == actualPid && expectedStart.HasValue && actualStart.HasValue && expectedStart.Value == actualStart.Value;
}

public sealed class ActiveTargetTracker : IActiveTargetTracker
{
    public Task<ActiveTarget> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = NativeMethods.GetForegroundWindow();
        uint processId = 0;
        var windowThreadId = handle == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(handle, out processId);
        if (handle == 0 || windowThreadId == 0)
            throw new InvalidOperationException("No foreground window could be captured.");

        var gui = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        var focusedChild = NativeMethods.GetGUIThreadInfo(windowThreadId, ref gui) ? gui.FocusWindow : 0;

        using var process = Process.GetProcessById(checked((int)processId));
        var startedAt = TryGetProcessStartTime(process);
        var executablePath = TryGetExecutablePath(process);
        var className = GetWindowClass(handle);
        var currentIntegrity = TryGetIntegrityRid(Process.GetCurrentProcess());
        var targetIntegrity = TryGetIntegrityRid(process);
        var automation = TryGetFocusedAutomationMetadata(handle, focusedChild);

        return Task.FromResult(new ActiveTarget(
            process.Id,
            handle,
            process.ProcessName,
            GetWindowTitle(handle),
            DateTimeOffset.UtcNow,
            windowThreadId,
            focusedChild,
            startedAt,
            executablePath,
            className,
            currentIntegrity,
            targetIntegrity,
            TargetPolicy.IsInjectionSafe(currentIntegrity, targetIntegrity),
            TargetPolicy.IsTerminal(process.ProcessName, className),
            automation.AutomationId,
            automation.ControlType,
            automation.Name,
            automation.IsPassword));
    }

    public async Task<bool> RestoreAndValidateAsync(ActiveTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateIdentity(target, out var windowThreadId) ||
            (target.WindowThreadId != 0 && windowThreadId != target.WindowThreadId))
            return false;

        var foregroundSet = NativeMethods.SetForegroundWindow(target.WindowHandle);
        if (!foregroundSet || !await WaitForForegroundAsync(target.WindowHandle, cancellationToken).ConfigureAwait(false))
            return false;

        if (!TryValidateIdentity(target, out windowThreadId) ||
            (target.WindowThreadId != 0 && windowThreadId != target.WindowThreadId))
            return false;

        if (target.FocusedChildWindowHandle != 0 &&
            NativeMethods.IsWindow(target.FocusedChildWindowHandle) &&
            NativeMethods.GetWindowThreadProcessId(target.FocusedChildWindowHandle, out var childPid) == windowThreadId &&
            childPid == (uint)target.ProcessId)
        {
            var currentThreadId = NativeMethods.GetCurrentThreadId();
            var attached = currentThreadId != windowThreadId && NativeMethods.AttachThreadInput(currentThreadId, windowThreadId, true);
            try
            {
                NativeMethods.SetFocus(target.FocusedChildWindowHandle);
            }
            finally
            {
                if (attached)
                    NativeMethods.AttachThreadInput(currentThreadId, windowThreadId, false);
            }
        }

        RestoreAutomationFocus(target);
        return NativeMethods.GetForegroundWindow() == target.WindowHandle;
    }

    private static bool TryValidateIdentity(ActiveTarget target, out uint windowThreadId)
    {
        windowThreadId = 0;
        if (target.WindowHandle == 0 || !NativeMethods.IsWindow(target.WindowHandle))
            return false;

        windowThreadId = NativeMethods.GetWindowThreadProcessId(target.WindowHandle, out var processId);
        if (windowThreadId == 0 || processId != (uint)target.ProcessId)
            return false;

        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            return TargetPolicy.IsSameProcessIdentity(
                target.ProcessId,
                process.Id,
                target.ProcessStartTime,
                TryGetProcessStartTime(process));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForForegroundAsync(nint handle, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.GetForegroundWindow() == handle)
                return true;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        return NativeMethods.GetForegroundWindow() == handle;
    }

    private static void RestoreAutomationFocus(ActiveTarget target)
    {
        if (target.IsPasswordField == true ||
            string.IsNullOrEmpty(target.FocusedAutomationId) && string.IsNullOrEmpty(target.FocusedControlType))
            return;

        try
        {
            var root = AutomationElement.FromHandle(target.WindowHandle);
            var conditions = new List<Condition>();
            if (!string.IsNullOrEmpty(target.FocusedAutomationId))
                conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, target.FocusedAutomationId));
            if (!string.IsNullOrEmpty(target.FocusedControlType))
            {
                var type = ControlType.LookupById(int.Parse(target.FocusedControlType, System.Globalization.CultureInfo.InvariantCulture));
                conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            }
            if (conditions.Count == 0)
                return;

            var match = root.FindFirst(TreeScope.Descendants,
                conditions.Count == 1 ? conditions[0] : new AndCondition(conditions.ToArray()));
            match?.SetFocus();
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or FormatException or COMException)
        {
        }
    }

    private static (string? AutomationId, string? ControlType, string? Name, bool? IsPassword) TryGetFocusedAutomationMetadata(
        nint windowHandle,
        nint focusedChild)
    {
        try
        {
            var request = new CacheRequest();
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.IsPasswordProperty);

            AutomationElement? element;
            using (request.Activate())
                element = AutomationElement.FocusedElement;
            if (element is null || !IsDescendantOfWindow(element, windowHandle, focusedChild))
                return default;

            return (
                (string)element.GetCachedPropertyValue(AutomationElement.AutomationIdProperty),
                ((ControlType)element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty)).Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (string)element.GetCachedPropertyValue(AutomationElement.NameProperty),
                (bool)element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty));
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return default;
        }
    }

    private static bool IsDescendantOfWindow(AutomationElement element, nint windowHandle, nint focusedChild)
    {
        var nativeHandle = (nint)element.Current.NativeWindowHandle;
        if (nativeHandle == windowHandle || focusedChild != 0 && nativeHandle == focusedChild)
            return true;

        var walker = TreeWalker.ControlViewWalker;
        for (var current = element; current is not null; current = walker.GetParent(current))
        {
            if ((nint)current.Current.NativeWindowHandle == windowHandle)
                return true;
        }
        return false;
    }

    private static DateTimeOffset? TryGetProcessStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static int? TryGetIntegrityRid(Process process)
    {
        if (!NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TokenQuery, out var token))
            return null;

        try
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, 0, 0, out var length);
            if (length == 0)
                return null;

            var buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
                    return null;

                var label = Marshal.PtrToStructure<NativeMethods.TokenMandatoryLabel>(buffer);
                var countPointer = NativeMethods.GetSidSubAuthorityCount(label.Label.Sid);
                if (countPointer == 0)
                    return null;
                var count = Marshal.ReadByte(countPointer);
                return count == 0 ? null : Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static string GetWindowTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length == 0)
            return string.Empty;
        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(handle, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    private static string GetWindowClass(nint handle)
    {
        var buffer = new char[256];
        var copied = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }
}
