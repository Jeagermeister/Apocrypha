using Apocrypha.Abstractions.Loadouts.Synchronizers;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Abstractions.Games.FileHashes;
using Apocrypha.StandardGameLocators.TestHelpers;
using Apocrypha.App.UI.Controls.GameWidget;
using Apocrypha.App.UI.Dialog;
using Apocrypha.App.UI.Dialog.Enums;
using Apocrypha.App.UI.Overlays;
using Apocrypha.App.UI.Pages.MyGames;
using Apocrypha.App.UI.Windows;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk;
using Apocrypha.UI.Sdk.Dialog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Apocrypha.UI.Tests;

[Collection("Game registration")]
public class InitialManagementCancellationTests(IServiceProvider provider) : AUiTest(provider)
{
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DialogCancelUnregistersWithoutRunningArchiveCleanupOrChangingFiles(bool dismissDialog)
    {
        var registry = Provider.GetRequiredService<IGameRegistry>();
        var installation = registry.LocateGameInstallations().Single();
        var root = installation.Locations[LocationId.Game].Path;
        var extra = root / "config.ini";
        await extra.WriteAllTextAsync("preserve me");

        var windows = Substitute.For<IWindowManager>();
        AffectedFile[]? presentedFiles = null;
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>())
            .Returns(call =>
            {
                var dialog = call.ArgAt<IDialog>(0);
                var content = (DialogStandardContentViewModel)((Apocrypha.App.UI.Dialog.Dialog)dialog).ViewModel.ContentViewModel!;
                presentedFiles = content.AffectedFiles;
                return Task.FromResult(new StandardDialogResult { ButtonId = dismissDialog ? ButtonDefinitionId.None : ButtonDefinitionId.Cancel });
            });
        var sync = Substitute.For<ISynchronizerService>();
        var realSync = Provider.GetRequiredService<ISynchronizerService>();
        sync.UnManage(installation, Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(call => realSync.UnManage(installation, call.ArgAt<bool>(1), call.ArgAt<bool>(2)));

        var vm = new MyGamesViewModel(windows, Provider,
            Provider.GetRequiredService<NexusMods.MnemonicDB.Abstractions.IConnection>(),
            Provider.GetRequiredService<ILogger<MyGamesViewModel>>(),
            Provider.GetRequiredService<IOverlayController>(), Provider.GetRequiredService<IOSInterop>(), sync, registry);
        var widget = Substitute.For<IGameWidgetViewModel>();
        await vm.AddGameHandler(installation, widget);

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
        await sync.Received(1).UnManage(installation, runGc: false, cleanGameFolder: false);
        Assert.NotNull(presentedFiles);
        Assert.Contains(presentedFiles!, f => f.Path == "config.ini" && f.Bytes == 11 && f.Classification == "Modified or unverified original");
        Assert.Equal("preserve me", await extra.ReadAllTextAsync());
        Assert.Equal(GameWidgetState.DetectedGame, widget.State);
        Assert.False(Provider.GetRequiredService<ILoadoutManager>().GetCurrentlyActiveLoadout(installation).HasValue);
    }
}
