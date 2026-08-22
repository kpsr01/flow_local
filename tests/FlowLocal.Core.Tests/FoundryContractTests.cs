using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class FoundryContractTests
{
    [Fact]
    public void Backend_ExposesInitialStatusWithoutInitializingModel()
    {
        using var service = new FoundryLocalAsrService();

        Assert.Equal("nemotron-speech-streaming-en-0.6b", FoundryLocalAsrService.ModelAlias);
        Assert.Equal(FoundryLocalState.NotInstalled, service.Status.State);
        Assert.Null(service.Status.ModelId);
        Assert.Null(service.Status.Provider);
        Assert.Null(service.Status.FailureMessage);
    }

    [Fact]
    public void Status_DefaultsOptionalDetailsToAbsent()
    {
        var status = new FoundryLocalStatus(FoundryLocalState.NotInstalled);

        Assert.Equal(FoundryLocalState.NotInstalled, status.State);
        Assert.Null(status.ModelId);
        Assert.Null(status.Provider);
        Assert.Null(status.FailureMessage);
    }

    [Fact]
    public void Status_PreservesSelectedModelAndProvider()
    {
        var status = new FoundryLocalStatus(
            FoundryLocalState.Ready,
            "nemotron-speech-streaming-en-0.6b",
            "WinML");

        Assert.Equal(FoundryLocalState.Ready, status.State);
        Assert.Equal("nemotron-speech-streaming-en-0.6b", status.ModelId);
        Assert.Equal("WinML", status.Provider);
        Assert.Null(status.FailureMessage);
    }

    [Fact]
    public void States_ExposeCompleteLifecycle()
    {
        Assert.Equal(
            [
                FoundryLocalState.NotInstalled,
                FoundryLocalState.Initializing,
                FoundryLocalState.Downloading,
                FoundryLocalState.Loading,
                FoundryLocalState.Ready,
                FoundryLocalState.Failed
            ],
            Enum.GetValues<FoundryLocalState>());
    }
}
