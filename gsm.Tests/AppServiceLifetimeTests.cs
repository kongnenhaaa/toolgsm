using Microsoft.Extensions.DependencyInjection;

namespace gsm.Tests;

public sealed class AppServiceLifetimeTests
{
    [Fact]
    public void DisposeServiceProviderOnce_DisposesResolvedSingletonExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DisposableProbe>();
        ServiceProvider? provider = services.BuildServiceProvider();
        DisposableProbe probe = provider.GetRequiredService<DisposableProbe>();

        App.DisposeServiceProviderOnce(ref provider);
        App.DisposeServiceProviderOnce(ref provider);

        Assert.Null(provider);
        Assert.Equal(1, probe.DisposeCount);
    }

    [Fact]
    public void WaitForTaskBounded_CompletesOrTimesOutWithoutUnboundedShutdown()
    {
        Assert.True(App.WaitForTaskBounded(
            Task.CompletedTask,
            TimeSpan.FromMilliseconds(50)));

        var pending = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(App.WaitForTaskBounded(
            pending.Task,
            TimeSpan.FromMilliseconds(20)));
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
