using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Apocrypha.App;
using Apocrypha.Backend;
using Apocrypha.DataModel;
using Apocrypha.Games.RedEngine.Cyberpunk2077;
using NexusMods.Paths;
using Apocrypha.Sdk;
using Apocrypha.Sdk.Settings;
using Apocrypha.StandardGameLocators.TestHelpers;
using Apocrypha.UI.Tests.Framework;
using Apocrypha.SingleProcess;
using Apocrypha.Abstractions.Games.FileHashes;
using Apocrypha.Sdk.FileStore;
using Microsoft.Extensions.Hosting;
using Xunit.DependencyInjection.Logging;

namespace Apocrypha.UI.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"Apocrypha.UI.Tests.Tests-{Guid.NewGuid()}";
        
        var path = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine("temp").Combine(Guid.NewGuid().ToString());
        path.CreateDirectory();

        services.AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.61"))
                .AddApp(addStandardGameLocators: false, startupMode: new StartupMode()
                {
                    ShowUI = false,
                    ExecuteCli = false,
                    RunAsMain = true,
                })
                .AddSingleton<IFileHashesService, HostedStubFileHashesService>()
                .OverrideSettingsForTests<CliSettings>(_ => new CliSettings
                {
                    // Persisted desktop settings can name the real user's runtime file.
                    // Tests must never open it, even when XDG_RUNTIME_DIR is overridden.
                    SyncFile = path / "test-sync.sync",
                    StartCliBackend = false,
                })
                .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations = [
                        new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives"),
                    ],
                })
                .AddSingleton<AvaloniaApp>()
                .AddLogging(builder => builder.AddXunitOutput().SetMinimumLevel(LogLevel.Debug))
                .Validate();
    }

    private sealed class HostedStubFileHashesService(IServiceProvider provider, IFileStore store, TemporaryFileManager temporaryFiles)
        : StubbedFileHasherService(provider, store, temporaryFiles), IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken) => await GetFileHashesDb();
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

