using System.Collections.Frozen;
using Apocrypha.Backend;
using Apocrypha.Sdk.Hashes;
using NSubstitute;
using Apocrypha.Abstractions.Games.FileHashes;
using Apocrypha.Abstractions.Games.FileHashes.Models;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Abstractions.Loadouts.Synchronizers;
using Apocrypha.Games.TestFramework;
using Apocrypha.Sdk.Games;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using Xunit.Abstractions;

namespace Apocrypha.DataModel.Synchronizer.Tests;

public class InstalledManifestHashTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<InstalledManifestHashTests>(helper)
{
    [Fact]
    public async Task QuickHashFromAnotherVersionCannotMisclassifyAnOriginalFile()
    {
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var file = root / "archive/pc/ep1/voice.archive";
        file.Parent.CreateDirectory();
        var content = new byte[1024 * 1024];
        content[content.Length / 2] = 42;
        await File.WriteAllBytesAsync(file.ToString(), content);
        var currentHash = content.xxHash3();
        await using var stream = file.Read();
        var sampled = await MultiHasher.HashStream(stream);
        using var tx = Connection.BeginTransaction();
        var staleHash = Hash.From(123UL);
        var relation = new HashRelation.New(tx)
        {
            MinimalHash = sampled.MinimalHash, Size = sampled.Size, XxHash3 = staleHash,
            XxHash64 = sampled.XxHash64, Sha1 = sampled.Sha1, Md5 = sampled.Md5, Crc32 = sampled.Crc32,
        };
        _ = new PathHashRelation.New(tx) { Path = "archive/pc/ep1/voice.archive", HashId = relation };
        await tx.Commit();
        var hashes = Substitute.For<IFileHashesService>();
        hashes.Current.Returns(Connection.Db);
        GamePath path = (LocationId.Game, "archive/pc/ep1/voice.archive");
        hashes.GetGameFiles((GameStore.Unknown, new[] { LocatorId.From("test") })).ReturnsForAnyArgs(new[]
        {
            new GameFileRecord { Path = path, Hash = currentHash, Size = sampled.Size, MinimalHash = sampled.MinimalHash },
        });
        var services = new ServiceCollection().AddLogging().AddSingleton(hashes).AddGameServices();
        await using var provider = services.BuildServiceProvider();
        var index = await provider.GetRequiredService<IGameLocationsService>().IndexGame(GameInstallation,
            FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);
        index.NewFiles[path].Hash.Should().Be(currentHash).And.NotBe(staleHash);
    }
}
