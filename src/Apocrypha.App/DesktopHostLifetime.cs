using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Apocrypha.UI.Tests")]

namespace Apocrypha.App;

internal static class DesktopHostLifetime
{
    internal static CancellationTokenRegistration Link(IHostApplicationLifetime host, IClassicDesktopStyleApplicationLifetime desktop,
        Action<Action>? dispatch = null)
    {
        dispatch ??= action => Dispatcher.UIThread.Post(action);
        return host.ApplicationStopping.Register(() => dispatch(() => desktop.Shutdown()));
    }
}
