using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Games.RedEngine.Cyberpunk2077;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk.Loadouts;
using FluentAssertions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using Xunit.Abstractions;

namespace Apocrypha.DataModel.Synchronizer.Tests;

public class VrWorkflowTests(ITestOutputHelper output) : AVrIsolatedGameTest<VrWorkflowTests>(output)
{
    private const string Plugin = "red4ext/plugins/cyberpunkvrport/cyberpunkvrport.dll";
    private const string Config = "red4ext/plugins/cyberpunkvrport/config/vrport.ini";

    private async Task<Loadout.ReadOnly> Install(bool dependency = true)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, [Plugin], loadout, "VR");
        if (dependency) await AddModAsync(tx, ["red4ext/RED4ext.dll", "bin/x64/winmm.dll"], loadout, "RED4ext");
        await tx.Commit();
        return loadout.Rebase();
    }

    private async Task Toggle(Loadout.ReadOnly loadout, bool enabled)
    {
        var group = LoadoutItem.FindByLoadout(Connection.Db, loadout).Single(item => item.Name == "VR");
        using var tx = Connection.BeginTransaction();
        if (enabled) tx.Retract(group.Id, LoadoutItem.Disabled, Null.Instance);
        else tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    [Fact]
    public async Task NormalApplyCapturesDisablesRestoresAndReenablesPreferences()
    {
        var loadout = await Install();
        var flat = await SettingsFile.ReadAllTextAsync();
        await SynchronizerService.Synchronize(loadout);
        Lifecycle.HasSession(GameInstallation).Should().BeTrue();
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var config = root / Config; config.Parent.CreateDirectory();
        await config.WriteAllTextAsync("first_launch=0\nmy_height=1.7");
        await SettingsFile.WriteAllTextAsync("{\"graphics\":\"vr\",\"personal\":true}");
        await Toggle(loadout, false);
        RunCyberpunk2077Game.HasVrPayload(loadout.Rebase()).Should().BeFalse();
        await SynchronizerService.Synchronize(loadout);
        (root / Plugin).FileExists.Should().BeFalse();
        config.FileExists.Should().BeFalse();
        (await SettingsFile.ReadAllTextAsync()).Should().Be(flat);
        ReviewChoice.Calls.Should().Be(1);
        await SettingsFile.WriteAllTextAsync("{\"graphics\":\"new flat preferences\"}");
        await Toggle(loadout, true);
        await SynchronizerService.Synchronize(loadout);
        (root / Plugin).FileExists.Should().BeTrue();
        (await config.ReadAllTextAsync()).Should().Contain("my_height=1.7");
        (await SettingsFile.ReadAllTextAsync()).Should().Contain("new flat preferences");
        await SynchronizerService.UnManage(GameInstallation, runGc: false);
        (root / Plugin).FileExists.Should().BeFalse();
        (await SettingsFile.ReadAllTextAsync()).Should().Contain("new flat preferences");
        ReviewChoice.Calls.Should().Be(2);
    }

    [Fact]
    public async Task MissingDependencyBlocksDeploymentBeforeCapture()
    {
        var loadout = await Install(dependency: false);
        await FluentActions.Awaiting(() => Synchronizer.Synchronize(loadout)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("*requires RED4ext*");
        (GameInstallation.Locations[LocationId.Game].Path / Plugin).FileExists.Should().BeFalse();
        Lifecycle.HasSession(GameInstallation).Should().BeFalse();
    }

    [Fact]
    public async Task CancelledRemovalLeavesTheAppliedPayloadAndSettingsIntact()
    {
        var loadout = await Install();
        await SynchronizerService.Synchronize(loadout);
        await SettingsFile.WriteAllTextAsync("{\"graphics\":\"VR\"}");
        await Toggle(loadout, false);
        ReviewChoice.Choice = VrRemovalChoice.Cancel;
        await FluentActions.Awaiting(() => Synchronizer.Synchronize(loadout.Rebase())).Should().ThrowAsync<OperationCanceledException>();
        (GameInstallation.Locations[LocationId.Game].Path / Plugin).FileExists.Should().BeTrue();
        (await SettingsFile.ReadAllTextAsync()).Should().Contain("VR");
        Lifecycle.HasPendingRemoval(GameInstallation).Should().BeFalse();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRemovalReviewsLaterSettingsOrRuntimeChangesDuringNormalApply(bool newRuntimeFile)
    {
        var loadout = await Install();
        await SynchronizerService.Synchronize(loadout);
        await SettingsFile.WriteAllTextAsync("{\"graphics\":\"VR\"}");
        Lifecycle.PlanRemoval(GameInstallation, Lifecycle.Inspect(GameInstallation), restoreSettings: true);
        await Toggle(loadout, false);
        if (newRuntimeFile)
        {
            var config = GameInstallation.Locations[LocationId.Game].Path / Config;
            config.Parent.CreateDirectory();
            await config.WriteAllTextAsync("new preference after approval");
        }
        else await SettingsFile.WriteAllTextAsync("{\"graphics\":\"newer user choice\"}");
        ReviewChoice.Choice = VrRemovalChoice.KeepSettings;
        await SynchronizerService.Synchronize(loadout);
        ReviewChoice.Calls.Should().Be(1);
        Lifecycle.HasPendingRemoval(GameInstallation).Should().BeFalse();
        (GameInstallation.Locations[LocationId.Game].Path / Plugin).FileExists.Should().BeFalse();
        (await SettingsFile.ReadAllTextAsync()).Should().Contain(newRuntimeFile ? "VR" : "newer user choice");
        if (newRuntimeFile)
            (GameInstallation.Locations[LocationId.Game].Path / Config).FileExists.Should().BeFalse();
    }

}
