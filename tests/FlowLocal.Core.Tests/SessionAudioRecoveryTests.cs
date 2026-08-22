using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class SessionAudioRecoveryTests
{
    [Fact]
    public void CreateRecordingPath_UsesCanonicalSessionPath_AndSuccessfulRecordingIsRetained()
    {
        var sessionId = Guid.NewGuid();
        var path = AsrRetryService.CreateRecordingPath(sessionId);

        try
        {
            using (var wave = new PcmWaveFile(path))
                wave.Write([0, 1, 2, 3]);

            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlowLocal",
                    "Recordings",
                    $"{sessionId:N}.wav"),
                path);
            Assert.True(File.Exists(path));
            Assert.Equal([0, 1, 2, 3], PcmWaveFile.ReadData(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeleteRecording_RemovesCancelledSessionRecording()
    {
        var path = AsrRetryService.CreateRecordingPath(Guid.NewGuid());
        using (var wave = new PcmWaveFile(path))
            wave.Write([4, 5, 6, 7]);

        AsrRetryService.DeleteRecording(path);

        Assert.False(File.Exists(path));
    }
}
