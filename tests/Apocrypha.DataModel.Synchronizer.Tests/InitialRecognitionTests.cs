using Apocrypha.Abstractions.Games.FileHashes;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Games.TestFramework;
using Apocrypha.Sdk;
using Apocrypha.Sdk.FileStore;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk.Loadouts;
using Apocrypha.StandardGameLocators.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using Xunit.Abstractions;

namespace Apocrypha.DataModel.Synchronizer.Tests;

public class InitialRecognitionTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<InitialRecognitionTests>(helper)
{
    private sealed class RecognitionState
    {
        public bool Known;
        public bool Fail;
        public int Calls;
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.AddSingleton<RecognitionState>();
        services.AddSingleton<IFileHashesService>(provider =>
        {
            var real = new StubbedFileHasherService(provider, provider.GetRequiredService<IFileStore>(),
                provider.GetRequiredService<TemporaryFileManager>());
            var state = provider.GetRequiredService<RecognitionState>();
            var hashes = Substitute.For<IFileHashesService>();
            hashes.GetFileHashesDb().Returns(_ => real.GetFileHashesDb());
            LocatorIdsWithGameStore ids = (GameStore.Unknown, new[] { LocatorId.From("StubbedGameState.zip") });
            hashes.GetGameFiles(ids)
                .ReturnsForAnyArgs(call => real.GetGameFiles(call.Arg<LocatorIdsWithGameStore>()));
            hashes.TryGetVanityVersion(ids, out _)
                .ReturnsForAnyArgs(call =>
                {
                    call[1] = state.Known ? VanityVersion.From("1.0.Stubbed") : VanityVersion.DefaultValue;
                    return state.Known;
                });
            return hashes;
        });
        services.AddSingleton<ILocalGameVersionRecognizer>(provider =>
        {
            var state = provider.GetRequiredService<RecognitionState>();
            var recognizer = Substitute.For<ILocalGameVersionRecognizer>();
            recognizer.CanRecognize(Arg.Any<GameInstallation>()).Returns(true);
            recognizer.RecognizeAsync(Arg.Any<GameInstallation>(), Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Loadout.All(provider.GetRequiredService<NexusMods.MnemonicDB.Abstractions.IConnection>().Db)
                        .Should().BeEmpty("recognition must precede loadout creation and the initial sync");
                    state.Calls++;
                    if (state.Fail) throw new OperationCanceledException();
                    state.Known = true;
                    return Task.FromResult(new LocalRecognitionResult
                    {
                        DepotsProcessed = 1, DepotsRecognized = 1, DepotsSkippedNoManifest = 0,
                        TotalVerifiedFiles = 4, TotalMissingFiles = 0, TotalModifiedFiles = 0,
                    });
                });
            return recognizer;
        });
        return services;
    }

    [Fact]
    public async Task UnknownVersionIsRecognizedBeforeFirstSync()
    {
        var state = ServiceProvider.GetRequiredService<RecognitionState>();
        var loadout = await CreateLoadout();
        state.Calls.Should().Be(1);
        loadout.GameVersion.Should().Be(VanityVersion.From("1.0.Stubbed"));
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath().Should().BeEmpty();
    }

    [Fact]
    public async Task KnownVersionDoesNotRunRecognition()
    {
        var state = ServiceProvider.GetRequiredService<RecognitionState>();
        state.Known = true;
        await CreateLoadout();
        state.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CancelledRecognitionDoesNotRegisterOrSyncGame()
    {
        var state = ServiceProvider.GetRequiredService<RecognitionState>();
        state.Fail = true;
        Func<Task> create = async () => await CreateLoadout();
        await create.Should().ThrowAsync<OperationCanceledException>();
        Loadout.All(Connection.Db).Should().BeEmpty();
        GameInstallMetadata.All(Connection.Db).Should().BeEmpty();
    }
}
