using FluentAssertions;
using Apocrypha.Games.TestFramework;
using Apocrypha.Sdk.Games;
using NexusMods.Hashing.xxHash3;
using Xunit.Abstractions;

namespace Apocrypha.DataModel.Synchronizer.Tests;

public class InitialManagementTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<InitialManagementTests>(helper)
{
    [Fact(Timeout = 30000)]
    public async Task CancelPreservesFilesAndAllowsManagingAgain()
    {
        var root = GameInstallation.Locations[LocationId.Game].Path;
        for (var i = 0; i < 184; i++)
        {
            var file = root / $"unrecognized/file{i}.txt";
            file.Parent.CreateDirectory();
            // Match the observed 184 paths / 124 archived content hashes, including
            // deduplication rather than assuming every path has unique bytes.
            await file.WriteAllTextAsync($"external content {i % 124}");
        }
        var loadout = await CreateLoadout();
        var before = root.EnumerateFiles(recursive: true)
            .ToDictionary(p => p, p => File.ReadAllBytes(p.ToString()).xxHash3());

        using var status = (await SynchronizerService.StatusForLoadout(loadout)).Subscribe(_ => { });
        await SynchronizerService.UnManage(GameInstallation, runGc: false, cleanGameFolder: false);

        root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => File.ReadAllBytes(p.ToString()).xxHash3())
            .Should().BeEquivalentTo(before);
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).HasValue.Should().BeFalse();
        await CreateLoadout();
    }
}
