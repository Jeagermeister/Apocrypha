using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Abstractions.Loadouts.Synchronizers;
using Apocrypha.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using Apocrypha.Games.TestFramework;
using Apocrypha.Sdk.Games;
using FluentAssertions;
using Xunit.Abstractions;

namespace Apocrypha.DataModel.Synchronizer.Tests;

public class UnmanagedOwnershipTests(ITestOutputHelper output) : ACyberpunkIsolatedGameTest<UnmanagedOwnershipTests>(output)
{
    [Fact]
    public async Task ArbitraryRuntimeFilesSurviveManagementSwitchingAndUnmanagement()
    {
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var extra = root / "unrelated/runtime-state.dat";
        extra.Parent.CreateDirectory();
        await extra.WriteAllTextAsync("user runtime state");
        var first = await CreateLoadout();
        LoadoutItem.FindByLoadout(Connection.Db, first).OfTypeLoadoutItemWithTargetPath()
            .Should().NotContain(file => file.TargetPath.Item3.ToString() == "unrelated/runtime-state.dat");
        await extra.WriteAllTextAsync("newer runtime state");
        await SynchronizerService.Synchronize(first);
        var second = await CreateLoadout();
        await SynchronizerService.Synchronize(second);
        await SynchronizerService.UnManage(GameInstallation, runGc: false);
        (await extra.ReadAllTextAsync()).Should().Be("newer runtime state");
        await CreateLoadout();
        (await extra.ReadAllTextAsync()).Should().Be("newer runtime state");
    }

    [Fact]
    public async Task UnmanagedCollisionRefusesBeforeAnyModFileIsWritten()
    {
        var loadout = await CreateLoadout();
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var extra = root / "bin/conflict.txt";
        extra.Parent.CreateDirectory(); await extra.WriteAllTextAsync("user content");
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ["bin/conflict.txt", "bin/new-mod-file.txt"], loadout, "Conflicting mod");
            await tx.Commit();
        }
        await FluentActions.Awaiting(() => Synchronizer.Synchronize(loadout.Rebase())).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("*unmanaged*bin/conflict.txt*");
        (await extra.ReadAllTextAsync()).Should().Be("user content");
        (root / "bin/new-mod-file.txt").FileExists.Should().BeFalse();
        // A refused activation must not leave a marker that grants ownership on retry.
        await FluentActions.Awaiting(async () => await LoadoutManager.ActivateLoadout(loadout)).Should()
            .ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(async () => await LoadoutManager.ActivateLoadout(loadout)).Should()
            .ThrowAsync<InvalidOperationException>();
        (await extra.ReadAllTextAsync()).Should().Be("user content");
    }

    [Fact]
    public async Task ModifiedOriginalIsNotSilentlyPromotedOrDeletedOnUnmanage()
    {
        var loadout = await CreateLoadout();
        var config = GameInstallation.Locations[LocationId.Game].Path / "config.ini";
        await config.WriteAllTextAsync("my preferences");
        await SynchronizerService.Synchronize(loadout);
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Should().Contain(file => file.TargetPath.Item3.ToString() == "config.ini");
        await SynchronizerService.UnManage(GameInstallation, runGc: false);
        (await config.ReadAllTextAsync()).Should().Be("my preferences");
    }
    [Fact]
    public async Task FileCreatedAfterPreviewCannotBeOverwritten()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ["bin/late.txt"], loadout, "Mod");
            await tx.Commit();
        }
        loadout = loadout.Rebase();
        var tree = await Synchronizer.BuildSyncTree(loadout);
        Synchronizer.ProcessSyncTree(tree);
        var file = GameInstallation.Locations[LocationId.Game].Path / "bin/late.txt";
        file.Parent.CreateDirectory(); await file.WriteAllTextAsync("arrived after preview");
        await FluentActions.Awaiting(() => Synchronizer.RunActions(tree, loadout)).Should().ThrowAsync<IOException>();
        (await file.ReadAllTextAsync()).Should().Be("arrived after preview");
    }

    [Fact]
    public async Task OversizedBackupRefusesReplacementRatherThanDiscardingOriginal()
    {
        var loadout = await CreateLoadout();
        var path = new GamePath(LocationId.Game, "large-original.bin");
        var file = GameInstallation.Locations.ToAbsolutePath(path);
        await file.WriteAllTextAsync("only original copy");
        var hash = "only original copy".xxHash3AsUtf8();
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [path] = new SyncNode
            {
                Disk = new SyncNodePart { Hash = hash, Size = Size.GB * 6, LastModifiedTicks = 0 },
                Actions = Actions.BackupFile | Actions.DeleteFromDisk | Actions.ExtractToDisk,
            },
        };
        await FluentActions.Awaiting(() => ((ALoadoutSynchronizer)Synchronizer).ActionBackupNewFiles(GameInstallation, loadout.InstallationId, tree))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*safety limit*");
        (await file.ReadAllTextAsync()).Should().Be("only original copy");
        (await FileStore.HaveFile(hash)).Should().BeFalse();
    }

}
