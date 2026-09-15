using System.Windows;
using FlowLocal.App;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class OverlayWindowSmokeTests
{
    [Fact]
    public Task CompletedPillCollapsesToVisibleIdleMic() => DictationStyleIntegrationTests.RunStaAsync(async () =>
    {
        var window = new OverlayWindow();
        try
        {
            window.ShowOverlay();
            window.ShowCompleted();

            await Task.Delay(1600);

            Assert.True(window.IsVisible);
            Assert.Equal(Visibility.Visible, window.MiniDot.Visibility);
        }
        finally
        {
            window.Close();
        }
    });
}
