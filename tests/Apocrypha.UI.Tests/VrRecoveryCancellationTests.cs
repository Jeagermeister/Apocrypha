using Apocrypha.Abstractions.Loadouts.Synchronizers;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.App.UI.Dialog;
using Apocrypha.App.UI.Dialog.Enums;
using Apocrypha.App.UI.LeftMenu.Loadout;
using Apocrypha.App.UI.Windows;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using Apocrypha.Sdk.Games;
using Apocrypha.UI.Sdk.Dialog;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Cryptography;

namespace Apocrypha.UI.Tests;

[Collection("Game registration")]
public class VrRecoveryCancellationTests(IServiceProvider provider) : AUiTest(provider)
{
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DismissingOrCancellingCaptureLeavesFilesAndRecoveryStateUntouched(bool dismiss)
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().Single();
        try
        {
            var loadout = await Provider.GetRequiredService<ILoadoutManager>().CreateLoadout(installation);
            var root = installation.Locations[LocationId.Game].Path;
            var before = root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.ToString()))));
            var windows = Substitute.For<IWindowManager>();
            windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(Task.FromResult(
                new StandardDialogResult { ButtonId = dismiss ? ButtonDefinitionId.None : ButtonDefinitionId.Cancel }));
            var services = new ServiceCollection().AddSingleton(windows).AddSingleton(Provider.GetRequiredService<CyberpunkVrLifecycle>());
            await using var child = services.BuildServiceProvider();
            await CyberpunkVrRecoveryDialogs.Show(child, loadout);
            await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
            var after = root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.ToString()))));
            Assert.Equal(before.OrderBy(x => x.Key.ToString()), after.OrderBy(x => x.Key.ToString()));
            Assert.ThrowsAny<IOException>(() => Provider.GetRequiredService<CyberpunkVrLifecycle>().Inspect(installation));
        }
        finally
        {
            await Provider.GetRequiredService<ISynchronizerService>().UnManage(installation, runGc: false, cleanGameFolder: false);
        }
    }
}

[CollectionDefinition("Game registration", DisableParallelization = true)]
public sealed class GameRegistrationCollection;
