using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Apocrypha.UI.Tests;

public class HostShutdownTests
{
    // This test needs only the dispatcher, not the UI suite's full app/game locators.
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact(Timeout = 30000)]
    public async Task HostStoppingDispatchesDesktopShutdown()
    {
        using var stopping = new CancellationTokenSource();
        var host = Substitute.For<IHostApplicationLifetime>();
        host.ApplicationStopping.Returns(stopping.Token);
        var desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        var dispatched = new ConcurrentQueue<Action>();

        using var registration = Apocrypha.App.DesktopHostLifetime.Link(host, desktop, dispatched.Enqueue);

        desktop.DidNotReceive().Shutdown(Arg.Any<int>());
        // The signal callback must queue shutdown, rather than running desktop code
        // on the worker that requests host shutdown.
        await Task.Run(stopping.Cancel);
        desktop.DidNotReceive().Shutdown(Arg.Any<int>());
        Assert.True(dispatched.TryDequeue(out var shutdown));
        shutdown();
        desktop.Received(1).Shutdown(0);
    }
}
