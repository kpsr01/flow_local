using System.Windows;
using System.Windows.Controls;
using FlowLocal.App;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class UpdateUiSmokeTests
{
    [Fact]
    public Task ShowsUpdatePageAndRequestsCheck() => DictationStyleIntegrationTests.RunStaAsync(async () =>
    {
        var window = new MainWindow();
        var requests = 0;
        window.CheckForUpdatesRequested += (_, _) => requests++;

        try
        {
            window.Show();
            window.Navigate("updates");
            window.SetUpdateStatus("Checking for updates…", busy: true);

            Assert.Equal("Updates", window.PageTitle.Text);
            Assert.Equal(Visibility.Visible, window.PageUpdates.Visibility);
            Assert.Equal("Checking for updates…", window.UpdateStatusText.Text);
            Assert.False(window.CheckUpdatesButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.UpdateProgress.Visibility);

            window.SetUpdateStatus("Ready to check for updates.", busy: false);
            window.CheckUpdatesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, requests);
        }
        finally
        {
            window.Hide();
        }

        await Task.CompletedTask;
    });
}
