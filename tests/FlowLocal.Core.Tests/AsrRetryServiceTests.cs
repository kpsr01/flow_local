using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class AsrRetryServiceTests
{
    [Fact]
    public async Task Retry_ReplaysExactPcmInOrderIntoFreshSession_AndReturnsResult()
    {
        var pcm = Enumerable.Range(0, 65_538).Select(index => (byte)(index * 31)).ToArray();
        var path = CreateWave(pcm);
        var original = new AsrSessionOptions(Guid.NewGuid(), 16_000, 16, 1);
        var asr = new RecordingAsrService(new AsrResult("recovered"));

        try
        {
            var result = await AsrRetryService.RetryAsync(asr, path, original, CancellationToken.None);

            Assert.Equal("recovered", result.Text);
            var started = Assert.Single(asr.StartedSessions);
            Assert.NotEqual(original.SessionId, started.SessionId);
            Assert.Equal(original.SampleRate, started.SampleRate);
            Assert.Equal(original.BitsPerSample, started.BitsPerSample);
            Assert.Equal(original.Channels, started.Channels);
            Assert.Equal(pcm, asr.Audio.SelectMany(chunk => chunk).ToArray());
            Assert.Equal([32_768, 32_768, 2], asr.Audio.Select(chunk => chunk.Length));
            Assert.Equal(1, asr.CompleteCalls);
            Assert.Equal(0, asr.CancelCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RetryFailure_StopsAfterOneReplayAttempt_AndCancelsSession()
    {
        var path = CreateWave([0, 1, 2, 3]);
        var asr = new RecordingAsrService(new InvalidOperationException("retry failed"));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AsrRetryService.RetryAsync(asr, path, new AsrSessionOptions(Guid.NewGuid()), CancellationToken.None));

            Assert.Equal("retry failed", exception.Message);
            Assert.Single(asr.StartedSessions);
            Assert.Single(asr.Audio);
            Assert.Equal(1, asr.CompleteCalls);
            Assert.Equal(1, asr.CancelCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateWave(byte[] pcm)
    {
        var path = Path.Combine(Path.GetTempPath(), $"FlowLocal-{Guid.NewGuid():N}.wav");
        using (var wave = new PcmWaveFile(path))
            wave.Write(pcm);
        return path;
    }

    private sealed class RecordingAsrService(object completion) : IAsrService
    {
        public List<AsrSessionOptions> StartedSessions { get; } = [];
        public List<byte[]> Audio { get; } = [];
        public int CompleteCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken)
        {
            StartedSessions.Add(options);
            return Task.CompletedTask;
        }

        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken)
        {
            Audio.Add(pcmAudio.ToArray());
            return Task.CompletedTask;
        }

        public Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return completion is Exception exception
                ? Task.FromException<AsrResult>(exception)
                : Task.FromResult((AsrResult)completion);
        }

        public Task CancelSessionAsync(CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }
}
