using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlowLocal.App;
using Xunit.Sdk;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class WindowsTargetSmokeTests
{
    [Fact]
    public async Task CaptureAndRestore_OwnedWpfTextBox_PreservesIdentityAndMetadata()
    {
        if (!Environment.UserInteractive || GetProcessWindowStation() == 0)
            throw SkipException.ForSkip("An interactive Windows desktop is required.");

        var ready = new TaskCompletionSource<(Window Window, TextBox Target, Button Other, nint Handle)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? ownedWindow = null;
        Dispatcher? ownedDispatcher = null;
        var thread = new Thread(() =>
        {
            var textBox = new TextBox { Text = "untouched" };
            AutomationProperties.SetAutomationId(textBox, "OwnedTextTarget");
            var other = new Button { Content = "Other" };
            AutomationProperties.SetAutomationId(other, "OtherTarget");
            var window = new Window
            {
                Title = $"FlowLocal target smoke {Guid.NewGuid():N}",
                Width = 320,
                Height = 120,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Children = { textBox, other }
                }
            };
            ownedWindow = window;
            ownedDispatcher = window.Dispatcher;
            window.Loaded += async (_, _) =>
            {
                window.UpdateLayout();
                textBox.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                textBox.Focus();
                ready.TrySetResult((window, textBox, other, new WindowInteropHelper(window).Handle));
            };
            window.Closed += (_, _) =>
            {
                closed.TrySetResult();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try
        {
            var targetUi = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ownedWindow = targetUi.Window;
            ownedDispatcher = targetUi.Window.Dispatcher;
            var handle = targetUi.Handle;
            await ownedWindow.Dispatcher.InvokeAsync(() =>
            {
                ownedWindow.Activate();
                Assert.True(SetForegroundWindow(handle));
                targetUi.Target.Focus();
                ownedWindow.UpdateLayout();
            }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => Assert.True(SpinWait.SpinUntil(
                () => GetForegroundWindow() == handle
                    && AutomationElement.FocusedElement?.Current.AutomationId == "OwnedTextTarget",
                TimeSpan.FromSeconds(5))));

            var tracker = new ActiveTargetTracker();
            var captured = await tracker.CaptureAsync(CancellationToken.None);

            Assert.Equal(Environment.ProcessId, captured.ProcessId);
            Assert.Equal(handle, captured.WindowHandle);
            var windowTitle = await ownedWindow.Dispatcher.InvokeAsync(() => ownedWindow.Title).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(windowTitle, captured.WindowTitle);
            Assert.NotEqual(0u, captured.WindowThreadId);
            Assert.Equal(Process.GetCurrentProcess().StartTime.ToUniversalTime(), captured.ProcessStartTime?.UtcDateTime);
            Assert.Equal(Process.GetCurrentProcess().ProcessName, captured.ExecutableName, ignoreCase: true);
            Assert.Equal("OwnedTextTarget", captured.FocusedAutomationId);
            Assert.False(captured.IsPasswordField);
            await ownedWindow.Dispatcher.InvokeAsync(() => Assert.True(targetUi.Other.Focus()), DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("OtherTarget", AutomationElement.FocusedElement?.Current.AutomationId);
            Assert.True(await tracker.RestoreAndValidateAsync(captured, CancellationToken.None));
            Assert.Equal("OwnedTextTarget", AutomationElement.FocusedElement?.Current.AutomationId);
            var targetText = await ownedWindow.Dispatcher.InvokeAsync(() => targetUi.Target.Text).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("untouched", targetText);
        }
        finally
        {
            try
            {
                if (ownedDispatcher is not null)
                {
                    try
                    {
                        await ownedDispatcher.InvokeAsync(() => ownedWindow?.Close()).Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        if (!ownedDispatcher.HasShutdownStarted && !ownedDispatcher.HasShutdownFinished)
                            ownedDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
            }
            catch (Exception) when (ownedDispatcher?.HasShutdownStarted is true || ownedDispatcher?.HasShutdownFinished is true)
            {
            }
            finally
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The WPF smoke-test thread did not exit after cleanup.");
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();
}
