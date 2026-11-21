using System.IO.Compression;
using Apocrypha.Abstractions.Diagnostics;
using Apocrypha.Games.RedEngine.Cyberpunk2077;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Emitters;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Games.TestFramework;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk.Library;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using Xunit.Abstractions;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using NSubstitute;

namespace Apocrypha.DataModel.Synchronizer.Tests;

// Opt-in local integration tests: these packages are not redistributed by this repository.
// Only the fixture installation is changed. No executable from the packages is run.
public class VrPortPackageTests : AVrIsolatedGameTest<VrPortPackageTests>
{
    private readonly ITestOutputHelper _output;
    public VrPortPackageTests(ITestOutputHelper helper) : base(helper) => _output = helper;
    private static string Package(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        Skip.If(string.IsNullOrEmpty(path), $"Set {variable} to the official release ZIP to run this isolated package test.");
        return path!;
    }

    private static Dictionary<string, Hash> Payload(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        return zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => (Entry: e, Path: e.FullName.Replace('\\', '/')))
            .Where(e => e.Path.StartsWith("bin/x64/", StringComparison.Ordinal)
                        || e.Path.StartsWith("red4ext/", StringComparison.Ordinal)
                        || e.Path.StartsWith("r6/", StringComparison.Ordinal)
                        || e.Path.StartsWith("engine/", StringComparison.Ordinal)
                        || e.Path.StartsWith("archive/pc/mod/", StringComparison.Ordinal))
            .ToDictionary(e => e.Path, e =>
            {
                using var source = e.Entry.Open();
                using var bytes = new MemoryStream();
                source.CopyTo(bytes);
                return bytes.ToArray().xxHash3();
            });
    }

    private void AssertPayload(Dictionary<string, Hash> expected)
    {
        var root = GameInstallation.Locations[LocationId.Game].Path;
        foreach (var (path, hash) in expected)
        {
            var file = root / path;
            file.FileExists.Should().BeTrue(path);
            File.ReadAllBytes(file.ToString()).xxHash3().Should().Be(hash, path);
        }
    }

    [SkippableFact(Timeout = 180000)]
    public async Task OfficialPackagesDeployAndRemoveWithoutChangingBaseFiles()
    {
        var package = Package("APOCRYPHA_VR_PACKAGE");
        var dependency = Package("APOCRYPHA_RED4EXT_PACKAGE");
        var loadout = await CreateLoadout();
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var baseline = root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => File.ReadAllBytes(p.ToString()).xxHash3());
        var red4ext = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(dependency));
        var dep = await LoadoutManager.InstallItem(red4ext.AsLibraryFile().AsLibraryItem(), loadout);
        var archive = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(package));
        var mod = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout);
        await SynchronizerService.Synchronize(loadout);
        var payload = Payload(package);
        AssertPayload(payload);
        AssertPayload(Payload(dependency));
        _output.WriteLine($"Verified exact deployed content of {payload.Count} VR payload files plus {Payload(dependency).Count} RED4ext files.");
        // Root README/uninstall documents are library metadata, not deployed runtime files.
        await LoadoutManager.RemoveItems([mod.LoadoutItemGroup!.Value, dep.LoadoutItemGroup!.Value]);
        await SynchronizerService.Synchronize(loadout);
        root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => File.ReadAllBytes(p.ToString()).xxHash3())
            .Should().BeEquivalentTo(baseline);
    }

    [SkippableFact(Timeout = 180000)]
    public async Task UpdateAndRemovalArchiveNewRuntimeFilesThroughNormalApply()
    {
        var package = Package("APOCRYPHA_VR_PACKAGE");
        var previous = Package("APOCRYPHA_VR_PREVIOUS_PACKAGE");
        var loadout = await CreateLoadout();
        var red4ext = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(Package("APOCRYPHA_RED4EXT_PACKAGE")));
        await LoadoutManager.InstallItem(red4ext.AsLibraryFile().AsLibraryItem(), loadout);
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var oldArchive = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(previous));
        var old = await LoadoutManager.InstallItem(oldArchive.AsLibraryFile().AsLibraryItem(), loadout);
        await SynchronizerService.Synchronize(loadout);
        AssertPayload(Payload(previous));
        // Simulate only documented runtime output; this does not execute the Windows plugin.
        var runtimeFiles = new[] { "bin/x64/vrport.ini", "bin/x64/vrport-launcher.ini",
            "red4ext/plugins/cyberpunkvrport/log/cyberpunkvrport.log" };
        foreach (var path in runtimeFiles)
        {
            var file = root / path;
            file.Parent.CreateDirectory();
            await file.WriteAllTextAsync("runtime preference or output");
        }
        var archive = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(package));
        await LoadoutManager.ReplaceItems(loadout, [old.LoadoutItemGroup!.Value], archive.AsLibraryFile().AsLibraryItem());
        await SynchronizerService.Synchronize(loadout);
        var payload = Payload(package);
        AssertPayload(payload);
        foreach (var oldPath in Payload(previous).Keys.Except(payload.Keys))
            (root / oldPath).FileExists.Should().BeFalse(oldPath);
        _output.WriteLine($"Replaced {Payload(previous).Count} old payload paths with {payload.Count} new paths; obsolete payload removed.");
        // Simulate 0.2.0's source-defined migration from bin/x64 to its config
        // folder. The plugin itself is never executed by this test.
        var config = root / "red4ext/plugins/cyberpunkvrport/config";
        config.CreateDirectory();
        foreach (var name in new[] { "vrport.ini", "vrport-launcher.ini" })
            File.Move((root / $"bin/x64/{name}").ToString(), (config / name).ToString());
        var generated = new[]
        {
            "red4ext/plugins/cyberpunkvrport/config/vrport.ini",
            "red4ext/plugins/cyberpunkvrport/config/vrport-launcher.ini",
            "red4ext/plugins/cyberpunkvrport/config/vrik_calibration.ini",
            "red4ext/plugins/cyberpunkvrport/lua/CyberpunkVRPort_VRIK/vrik_settings.ini",
            "red4ext/plugins/cyberpunkvrport/lua/CyberpunkVRPort_VRIK/vrik_recenter.ini",
            "red4ext/plugins/cyberpunkvrport/log/cyberpunkvrport.log",
        };
        foreach (var path in generated)
        {
            var file = root / path;
            file.Parent.CreateDirectory();
            await file.WriteAllTextAsync("runtime preference or output");
        }
        await SynchronizerService.Synchronize(loadout);
        var group = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup()
            .Single(g => g.AsLoadoutItem().Name == archive.AsLibraryFile().AsLibraryItem().Name);
        await LoadoutManager.RemoveItems([group]);
        await SynchronizerService.Synchronize(loadout);
        foreach (var path in generated) (root / path).FileExists.Should().BeFalse(path);
        ReviewChoice.Calls.Should().Be(1);
        Lifecycle.Inspect(GameInstallation).Session.Completed.Should().BeTrue();
        _output.WriteLine("Normal Apply archived generated runtime preferences and completed settings restoration.");
    }
    [SkippableFact(Timeout = 180000)]
    public async Task RealPackagesHaveAReversibleLifecycleInADisposableProtonPrefix()
    {
        var package = Package("APOCRYPHA_VR_PACKAGE");
        var dependency = Package("APOCRYPHA_RED4EXT_PACKAGE");
        var loadout = await CreateLoadout();
        var root = GameInstallation.Locations[LocationId.Game].Path;
        var baseline = root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => File.ReadAllBytes(p.ToString()).xxHash3());
        var settings = SettingsFile;
        var lifecycle = Lifecycle;
        var installation = GameInstallation;
        var original = await settings.ReadAllTextAsync();
        var archive = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(package));
        var depArchive = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(dependency));
        var dep = await LoadoutManager.InstallItem(depArchive.AsLibraryFile().AsLibraryItem(), loadout);
        var mod = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout);
        await SynchronizerService.Synchronize(loadout);
        AssertPayload(Payload(package)); AssertPayload(Payload(dependency));
        lifecycle.HasSession(installation).Should().BeTrue("normal Apply captures before deployment");
        // Simulate the release's first-launch settings merge and timestamped backup;
        // no Windows binary, game, or headset process is executed by this test.
        await (settings.Parent / "UserSettings.pre-vr-fixture.json").WriteAllTextAsync(original);
        await settings.WriteAllTextAsync("{\"graphics\":\"VR\",\"personal\":true,\"height\":1.7}");
        var vrSettings = await settings.ReadAllTextAsync();
        var config = root / "red4ext/plugins/cyberpunkvrport/config/vrport.ini";
        config.Parent.CreateDirectory(); await config.WriteAllTextAsync("first_launch=0\nmy_height=1.7");
        await SynchronizerService.Synchronize(loadout);
        await LoadoutManager.RemoveItems([mod.LoadoutItemGroup!.Value, dep.LoadoutItemGroup!.Value]);
        await SynchronizerService.Synchronize(loadout);
        lifecycle.Inspect(installation).Session.Completed.Should().BeTrue();
        ReviewChoice.Calls.Should().Be(1);
        (await settings.ReadAllTextAsync()).Should().Be(original);
        root.EnumerateFiles(recursive: true).ToDictionary(p => p, p => File.ReadAllBytes(p.ToString()).xxHash3()).Should().BeEquivalentTo(baseline);
        (settings.Parent / "UserSettings.pre-vr-fixture.json").FileExists.Should().BeTrue();
        await lifecycle.UndoRemoval(installation);
        (await settings.ReadAllTextAsync()).Should().Be(vrSettings);
        (await config.ReadAllTextAsync()).Should().Contain("my_height=1.7");
        await LoadoutManager.InstallItem(depArchive.AsLibraryFile().AsLibraryItem(), loadout);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout);
        await lifecycle.Prepare(installation);
        await SynchronizerService.Synchronize(loadout);
        AssertPayload(Payload(package)); AssertPayload(Payload(dependency));
        (await settings.ReadAllTextAsync()).Should().Be(vrSettings);
        (await config.ReadAllTextAsync()).Should().Contain("my_height=1.7");
        foreach (var (file, hash) in baseline) File.ReadAllBytes(file.ToString()).xxHash3().Should().Be(hash);
    }

    [SkippableFact(Timeout = 180000)]
    public async Task CurrentPackageDiagnosesOnlyRed4extAndClearsWhenInstalled()
    {
        var loadout = await CreateLoadout();
        var archive = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(Package("APOCRYPHA_VR_PACKAGE")));
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout);
        var emitter = Game.DiagnosticEmitters.OfType<PatternBasedDependencyEmitter>().Single();
        var before = await emitter.Diagnose(loadout.Rebase(), CancellationToken.None).ToListAsync();
        before.Should().ContainSingle();
        before.Single().Title.Should().Be("Missing Dependency");
        var dependency = await RegisterLocalArchive(FileSystem.FromUnsanitizedFullPath(Package("APOCRYPHA_RED4EXT_PACKAGE")));
        await LoadoutManager.InstallItem(dependency.AsLibraryFile().AsLibraryItem(), loadout);
        (await emitter.Diagnose(loadout.Rebase(), CancellationToken.None).ToListAsync()).Should().BeEmpty();
    }

}
