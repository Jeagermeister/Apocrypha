using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Apocrypha.Abstractions.Games;
using Apocrypha.Abstractions.Games.FileHashes;
using Apocrypha.App.UI.Controls.GameWidget;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk.Settings;
using Apocrypha.UI.Sdk.Icons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NSubstitute;
using ReactiveUI;

namespace Apocrypha.UI.Tests;

[Collection("Game registration")]
public class GameWidgetThreadingTests(IServiceProvider provider) : AUiTest(provider)
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerVersionCompletionUpdatesTheRealBoundTextBlockOnUi(bool useHashDatabaseFallback)
    {
        var fixture = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().Single();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseVersion = new ManualResetEventSlim();
        var hashesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashResult = new TaskCompletionSource<IDb>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashes = Substitute.For<IFileHashesService>();
        hashes.GetFileHashesDb().Returns(_ =>
        {
            hashesStarted.TrySetResult();
            return new ValueTask<IDb>(hashResult.Task);
        });
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Worker-discovered game");
        game.TileImage.Returns(fixture.Game.TileImage);
        game.GetPublicVersion(Arg.Any<GameInstallation>()).Returns(_ =>
        {
            started.TrySetResult(Dispatcher.UIThread.CheckAccess());
            if (!releaseVersion.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Version fixture was not released");
            return useHashDatabaseFallback ? null : "2.5";
        });
        var installation = fixture with { LocatorResult = new GameLocatorResult
        {
            Game = game, Path = fixture.LocatorResult.Path, Locator = fixture.LocatorResult.Locator,
            LocatorIds = fixture.LocatorResult.LocatorIds, Store = GameStore.Steam,
            StoreIdentifier = "fixture", SteamBuildId = "threading-fixture",
        }};
        var vm = new GameWidgetViewModel(Provider.GetRequiredService<ILogger<GameWidgetViewModel>>(),
            Provider.GetRequiredService<ISettingsManager>(), hashes);
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlUpdates = new ConcurrentQueue<bool>();
        var reactiveErrors = new ConcurrentQueue<Exception>();
        Window? window = null;
        TextBlock? text = null;
        var oldErrorHandler = RxApp.DefaultExceptionHandler;
        IDisposable? observation = null;
        try
        {
            await OnUi(() =>
            {
                RxApp.DefaultExceptionHandler = Observer.Create<Exception>(reactiveErrors.Enqueue);
                observation = vm.WhenAnyValue(value => value.Version).Where(value => value.Length != 0)
                    .Subscribe(_ => published.TrySetResult(Dispatcher.UIThread.CheckAccess()));
                var view = new Apocrypha.App.UI.Controls.GameWidget.GameWidget { ViewModel = vm };
                window = new Window { Content = view, Width = 500, Height = 500 };
                window.Show();
                text = view!.FindControl<TextBlock>("VersionTextBlock")!;
                text.PropertyChanged += (_, change) =>
                {
                    if (change.Property == TextBlock.TextProperty) controlUpdates.Enqueue(Dispatcher.UIThread.CheckAccess());
                };
                // Discovery itself is synchronous here and happens on the UI thread. Only
                // the pending version work completes later on a worker, as in the live failure.
                vm.Installation = installation;
            });
            Assert.False(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.Run(releaseVersion.Set);
            if (useHashDatabaseFallback)
            {
                await hashesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(() => hashResult.SetResult(Provider.GetRequiredService<IConnection>().Db));
            }
            Assert.True(await published.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Version was published outside Avalonia's UI dispatcher");
            await EventuallyOnUi(() =>
            {
                Assert.Contains(useHashDatabaseFallback ? "Public version unknown" : "Version: 2.5", text!.Text);
                Assert.Contains("Steam build threading-fixture", text.Text);
                Assert.Equal(vm.Version, text.Text);
                Assert.Contains("Installed manifests:", ToolTip.GetTip(text)?.ToString());
                Assert.Contains(vm.Version, ToolTip.GetTip(text)?.ToString());
                return Task.CompletedTask;
            });
            Assert.NotEmpty(controlUpdates);
            Assert.All(controlUpdates, Assert.True);
            Assert.Empty(reactiveErrors);
        }
        finally
        {
            releaseVersion.Set();
            hashResult.TrySetResult(Provider.GetRequiredService<IConnection>().Db);
            await OnUi(() => { observation?.Dispose(); window?.Hide(); RxApp.DefaultExceptionHandler = oldErrorHandler; });
        }
    }
    [Fact(Timeout = 15000)]
    public async Task WorkerInstallationAndManagementUpdatesPublishAllRelatedWidgetStateOnUi()
    {
        var fixture = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().Single();
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Background discovery");
        game.TileImage.Returns(fixture.Game.TileImage);
        game.GetPublicVersion(Arg.Any<GameInstallation>()).Returns("2.5");
        var installation = fixture with { LocatorResult = new GameLocatorResult
        {
            Game = game, Path = fixture.LocatorResult.Path, Locator = fixture.LocatorResult.Locator,
            LocatorIds = fixture.LocatorResult.LocatorIds, Store = GameStore.GOG, StoreIdentifier = "fixture",
        }};
        using var managed = new BehaviorSubject<bool>(false);
        var vm = new GameWidgetViewModel(Provider.GetRequiredService<ILogger<GameWidgetViewModel>>(),
            Provider.GetRequiredService<ISettingsManager>(), Substitute.For<IFileHashesService>())
            { IsManagedObservable = managed };
        var properties = new[] { nameof(vm.Name), nameof(vm.Store), nameof(vm.GameStoreIcon), nameof(vm.Version), nameof(vm.State) };
        var notifications = new ConcurrentQueue<(string Property, bool OnUi)>();
        vm.PropertyChanged += (_, change) =>
        {
            if (properties.Contains(change.PropertyName)) notifications.Enqueue((change.PropertyName!, Dispatcher.UIThread.CheckAccess()));
        };
        Window? window = null;
        Apocrypha.App.UI.Controls.GameWidget.GameWidget? view = null;
        try
        {
            await OnUi(() =>
            {
                view = new Apocrypha.App.UI.Controls.GameWidget.GameWidget { ViewModel = vm };
                window = new Window { Content = view, Width = 500, Height = 500 };
                window.Show();
            });
            await Task.Run(() => { vm.Installation = installation; managed.OnNext(true); });
            await EventuallyOnUi(() =>
            {
                Assert.Equal("Background discovery", vm.Name);
                Assert.Equal(GameStore.GOG.Value, vm.Store);
                Assert.Equal(IconValues.GOG, vm.GameStoreIcon);
                Assert.Equal(vm.GameStoreIcon, view!.FindControl<UnifiedIcon>("GameStoreIcon")!.Value);
                Assert.Equal("Background discovery", ToolTip.GetTip(view!.FindControl<Border>("ImageSectionBorder")!));
                Assert.Equal(GameStore.GOG.Value, ToolTip.GetTip(view!.FindControl<Border>("StoreBackground")!));
                Assert.Contains("Version: 2.5", view!.FindControl<TextBlock>("VersionTextBlock")!.Text);
                Assert.Equal(GameWidgetState.ManagedGame, vm.State);
                Assert.True(view!.FindControl<Grid>("ManagedGameGrid")!.IsVisible);
                return Task.CompletedTask;
            });
            foreach (var property in properties) Assert.Contains(notifications, change => change.Property == property);
            Assert.All(notifications, change => Assert.True(change.OnUi, change.Property));
        }
        finally { await OnUi(() => window?.Hide()); }
    }

}
