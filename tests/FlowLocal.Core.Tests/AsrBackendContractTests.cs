using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class AsrBackendContractTests
{
    [Fact]
    public void Backend_ExposesInitialStatusWithoutInitializingModel()
    {
        using var service = new MoonshineAsrService();

        Assert.Equal("moonshine-streaming-medium", MoonshineAsrService.ModelName);
        Assert.Equal(AsrBackendState.NotInstalled, service.Status.State);
        Assert.Null(service.Status.ModelId);
        Assert.Null(service.Status.Provider);
        Assert.Null(service.Status.FailureMessage);
    }

    [Fact]
    public void Status_DefaultsOptionalDetailsToAbsent()
    {
        var status = new AsrBackendStatus(AsrBackendState.NotInstalled);

        Assert.Equal(AsrBackendState.NotInstalled, status.State);
        Assert.Null(status.ModelId);
        Assert.Null(status.Provider);
        Assert.Null(status.FailureMessage);
    }

    [Fact]
    public void Status_PreservesSelectedModelAndProvider()
    {
        var status = new AsrBackendStatus(
            AsrBackendState.Ready,
            "moonshine-streaming-medium",
            "CPU");

        Assert.Equal(AsrBackendState.Ready, status.State);
        Assert.Equal("moonshine-streaming-medium", status.ModelId);
        Assert.Equal("CPU", status.Provider);
        Assert.Null(status.FailureMessage);
    }

    [Fact]
    public void States_ExposeCompleteLifecycle()
    {
        Assert.Equal(
            [
                AsrBackendState.NotInstalled,
                AsrBackendState.Initializing,
                AsrBackendState.Downloading,
                AsrBackendState.Loading,
                AsrBackendState.Ready,
                AsrBackendState.Failed
            ],
            Enum.GetValues<AsrBackendState>());
    }
}
