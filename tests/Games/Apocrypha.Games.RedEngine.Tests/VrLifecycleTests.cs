using System.Collections.Immutable;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using Apocrypha.Sdk.Games;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;

namespace Apocrypha.Games.RedEngine.Tests;

public sealed class VrLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "apocrypha-vr-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _prefix;
    private readonly string _settings;
    private readonly CyberpunkVrLifecycle _lifecycle;
    private readonly GameInstallation _installation;
    private const string Original = "{\"graphics\":\"flat\",\"language\":\"English\"}";
    private const string Vr = "{\"graphics\":\"VR\",\"language\":\"English\",\"myEdit\":true}";
    private const string Config = "red4ext/plugins/cyberpunkvrport/config/vrport.ini";
    private const string Log = "red4ext/plugins/cyberpunkvrport/log/cyberpunkvrport.log";

    public VrLifecycleTests()
    {
        _root = Path.Combine(_directory, "game"); _prefix = Path.Combine(_directory, "prefix");
        Directory.CreateDirectory(_root); Directory.CreateDirectory(_prefix);
        _settings = Path.Combine(_prefix, "drive_c/users/custom-player/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_settings)!); File.WriteAllText(_settings, Original);
        File.WriteAllText(Path.Combine(_prefix, "user.reg"), "[Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Explorer\\\\Shell Folders]\n\"Local AppData\"=\"C:\\\\users\\\\custom-player\\\\AppData\\\\Local\"\n");
        var fs = FileSystem.Shared;
        var prefix = Substitute.For<ILinuxCompatabilityDataProvider>();
        prefix.WinePrefixDirectoryPath.Returns(fs.FromUnsanitizedFullPath(_prefix));
        _installation = new GameInstallation(new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(), Locator = Substitute.For<IGameLocator>(),
            Path = fs.FromUnsanitizedFullPath(_root), Store = GameStore.Steam, StoreIdentifier = "1091500",
            LocatorIds = [], LinuxCompatabilityDataProvider = prefix,
        }, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, fs.FromUnsanitizedFullPath(_root))));
        _lifecycle = new CyberpunkVrLifecycle(Path.Combine(_directory, "recovery"));
    }
    [Fact]
    public async Task ApprovedRemovalResumesFromDiskJournalAfterServiceRestart()
    {
        await _lifecycle.Prepare(_installation);
        File.WriteAllText(_settings, Vr); Write(Config, "saved height");
        _lifecycle.PlanRemoval(_installation, _lifecycle.Inspect(_installation), restoreSettings: true);
        var restarted = new CyberpunkVrLifecycle(Path.Combine(_directory, "recovery"));
        await restarted.CompletePendingRemoval(_installation);
        File.ReadAllText(_settings).Should().Be(Original);
        File.Exists(Path.Combine(_root, Config)).Should().BeFalse();
        restarted.HasPendingRemoval(_installation).Should().BeFalse();
        await restarted.Prepare(_installation);
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("saved height");
    }

    [Fact]
    public async Task RecoveryNeverOverwritesSettingsEditedAfterApproval()
    {
        await _lifecycle.Prepare(_installation);
        File.WriteAllText(_settings, Vr); Write(Config, "keep height");
        _lifecycle.PlanRemoval(_installation, _lifecycle.Inspect(_installation), restoreSettings: true);
        File.WriteAllText(_settings, "{\"newer\":true}");
        var restarted = new CyberpunkVrLifecycle(Path.Combine(_directory, "recovery"));
        await FluentActions.Awaiting(() => restarted.CompletePendingRemoval(_installation)).Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_settings).Should().Be("{\"newer\":true}");
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("keep height");
        restarted.HasPendingRemoval(_installation).Should().BeTrue();
    }

    private string Write(string path, string contents)
    {
        var full = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, contents); return full;
    }

    [Fact]
    public void UsesTheActiveRegistryUserRatherThanSteamuser() => CyberpunkVrLifecycle.FindSettings(_installation).Should().Be(_settings);

    [Fact]
    public void MissingRegistryNeverFallsBackToAnotherProfile()
    {
        File.WriteAllText(Path.Combine(_prefix, "user.reg"), "[unrelated]\n");
        var action = () => CyberpunkVrLifecycle.FindSettings(_installation);
        action.Should().Throw<InvalidOperationException>();
        File.ReadAllText(_settings).Should().Be(Original);
    }

    [Fact]
    public async Task UpdatesDoNotReplaceTheCapturedSettingsAndCleanupPreservesUnownedFiles()
    {
        var existing = Write(Config, "user owned before capture");
        var unknown = Write("red4ext/plugins/cyberpunkvrport/my-private-note.txt", "private");
        await _lifecycle.Prepare(_installation);
        var captured = _lifecycle.Inspect(_installation).Session;
        File.WriteAllText(_settings, Vr); Write(Log, "generated log");
        await _lifecycle.Prepare(_installation); // Updating/relaunching must not reset the restoration point.
        _lifecycle.Inspect(_installation).Session.CapturedAt.Should().Be(captured.CapturedAt);
        var result = await _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), false, true);
        File.ReadAllText(_settings).Should().Be(Vr);
        File.ReadAllText(existing).Should().Be("user owned before capture");
        File.ReadAllText(unknown).Should().Be("private");
        File.Exists(Path.Combine(_root, Log)).Should().BeFalse();
        result.ArchivedFiles.Should().ContainSingle();
        await _lifecycle.UndoRemoval(_installation);
        File.ReadAllText(Path.Combine(_root, Log)).Should().Be("generated log");
    }

    [Fact]
    public async Task ExplicitRestorationKeepsTheCurrentSettingsAndCanBeUndoneAndReinstalled()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_settings)!, "UserSettings.pre-vr-2000.json"), "{\"veryOld\":true}");
        await _lifecycle.Prepare(_installation);
        File.WriteAllText(_settings, Vr); Write(Config, "my height and preferences");
        await _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), true, true);
        File.ReadAllText(_settings).Should().Be(Original);
        await _lifecycle.UndoRemoval(_installation);
        await _lifecycle.UndoRemoval(_installation); // recovery is resumable/idempotent
        File.ReadAllText(_settings).Should().Be(Vr);
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("my height and preferences");
        await _lifecycle.Prepare(_installation); // reinstallation captures the user's current state
        _lifecycle.Inspect(_installation).Session.OriginalHash.Should().Be(CyberpunkVrLifecycle.Hash(_settings));
    }

    [Fact]
    public async Task ChangedSettingsOrRuntimeFilesRequireAnewReview()
    {
        await _lifecycle.Prepare(_installation);
        Write(Config, "old"); var review = _lifecycle.Inspect(_installation);
        File.WriteAllText(_settings, Vr);
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, review, true, true)).Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_settings).Should().Be(Vr);
        review = _lifecycle.Inspect(_installation); Write(Config, "new user edit");
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, review, false, true)).Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("new user edit");
    }

    [Fact]
    public async Task CancelledOperationDoesNotChangeFiles()
    {
        await _lifecycle.Prepare(_installation); Write(Config, "keep"); File.WriteAllText(_settings, Vr);
        var review = _lifecycle.Inspect(_installation);
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, review, true, true, new CancellationToken(true)))
            .Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(_settings).Should().Be(Vr); File.ReadAllText(Path.Combine(_root, Config)).Should().Be("keep");
    }

    [Fact]
    public async Task InterruptedCleanupKeepsRecoveryAndResumesWithoutLosingFiles()
    {
        await _lifecycle.Prepare(_installation); Write(Config, "preferences"); Write(Log, "old log");
        var review = _lifecycle.Inspect(_installation); Write(Log, "updated log");
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, review, false, true)).Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(_root, Config)).Should().BeFalse();
        _lifecycle.Inspect(_installation).Session.ArchivedFiles.Should().ContainSingle();
        await _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), false, true);
        await _lifecycle.UndoRemoval(_installation);
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("preferences");
        File.ReadAllText(Path.Combine(_root, Log)).Should().Be("updated log");
    }

    [Fact]
    public async Task SymbolicLinksAndModifiedRecoveryCopiesAreRefused()
    {
        await _lifecycle.Prepare(_installation);
        var outside = Path.Combine(_directory, "outside.txt"); File.WriteAllText(outside, "private");
        var file = Write(Config, "temporary"); File.Delete(file); File.CreateSymbolicLink(file, outside);
        var inspect = () => _lifecycle.Inspect(_installation);
        inspect.Should().Throw<InvalidOperationException>(); File.ReadAllText(outside).Should().Be("private");
        File.Delete(file); var review = _lifecycle.Inspect(_installation);
        File.WriteAllText(Path.Combine(review.RecoveryDirectory, review.Session.OriginalFile), "{\"altered\":true}");
        File.WriteAllText(_settings, Vr);
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), true, false))
            .Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_settings).Should().Be(Vr);
    }
    [Fact]
    public async Task PortPresetBackupsRequireAnExactChoiceAndKeepAnUndoCopy()
    {
        var directory = Path.GetDirectoryName(_settings)!;
        var old = Path.Combine(directory, "UserSettings.pre-vr-old.json");
        var chosen = Path.Combine(directory, "UserSettings.pre-vr-chosen.json");
        File.WriteAllText(old, "{\"older\":true}");
        File.WriteAllText(chosen, Original);
        File.WriteAllText(Path.Combine(directory, "UserSettings.pre-vr-broken.json"), "invalid");
        File.WriteAllText(_settings, Vr);
        var backups = CyberpunkVrLifecycle.PortBackups(_installation);
        backups.Should().HaveCount(2);
        var selection = backups.Single(b => b.Path == Path.GetFileName(chosen));
        await _lifecycle.RestorePortBackup(_installation, selection, CyberpunkVrLifecycle.Hash(_settings));
        File.ReadAllText(_settings).Should().Be(Original);
        File.ReadAllText(old).Should().Be("{\"older\":true}");
        await _lifecycle.UndoRemoval(_installation);
        File.ReadAllText(_settings).Should().Be(Vr);
    }

    [Fact]
    public async Task CleanupIsBlockedUntilThePluginWasActuallyRemoved()
    {
        await _lifecycle.Prepare(_installation);
        Write("red4ext/plugins/cyberpunkvrport/cyberpunkvrport.dll", "plugin");
        File.WriteAllText(_settings, Vr); Write(Config, "keep");
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), true, true))
            .Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_settings).Should().Be(Vr);
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("keep");
    }

    [Fact]
    public async Task MigratedLegacyPreferencesRemainUserOwned()
    {
        var legacy = Write("bin/x64/vrport.ini", "my seated height");
        await _lifecycle.Prepare(_installation);
        var destination = Write(Config, "placeholder"); File.Delete(destination); File.Move(legacy, destination);
        File.WriteAllText(destination, "my adjusted seated height");
        var review = _lifecycle.Inspect(_installation);
        review.NewRuntimeFiles.Should().BeEmpty(); review.PreservedExistingFiles.Should().Contain(Config);
        await _lifecycle.FinishRemoval(_installation, review, false, true);
        File.ReadAllText(destination).Should().Be("my adjusted seated height");
    }

    [Fact]
    public async Task EditedUndoSettingsCopyCannotReplaceCurrentSettings()
    {
        await _lifecycle.Prepare(_installation); File.WriteAllText(_settings, Vr);
        var session = await _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), true, false);
        var directory = _lifecycle.Inspect(_installation).RecoveryDirectory;
        File.WriteAllText(Path.Combine(directory, session.SettingsRecovery!), "{\"tampered\":true}");
        await FluentActions.Awaiting(() => _lifecycle.UndoRemoval(_installation)).Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_settings).Should().Be(Original);
    }

    [Fact]
    public async Task ResumedCleanupRestoresTheLatestReviewedPreference()
    {
        await _lifecycle.Prepare(_installation); Write(Config, "first"); Write(Log, "old log");
        var review = _lifecycle.Inspect(_installation); Write(Log, "new log");
        await FluentActions.Awaiting(() => _lifecycle.FinishRemoval(_installation, review, false, true)).Should().ThrowAsync<InvalidOperationException>();
        Write(Config, "latest preference");
        await _lifecycle.FinishRemoval(_installation, _lifecycle.Inspect(_installation), false, true);
        await _lifecycle.UndoRemoval(_installation);
        File.ReadAllText(Path.Combine(_root, Config)).Should().Be("latest preference");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
