using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apocrypha.Sdk.Games;
using Apocrypha.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;

namespace Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;

/// <summary>Conservative, recoverable handling of VR runtime state. Package files remain owned by the overlay installer.</summary>
public sealed class CyberpunkVrLifecycle(string storageDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public sealed record FileState(string Path, string Hash, string? RecoveryPath = null);
    public sealed record Session(string GameRoot, string SettingsPath, DateTimeOffset CapturedAt,
        string OriginalHash, Dictionary<string, string> ExistingFiles, List<FileState> ArchivedFiles,
        string OriginalFile = "original-settings.json", bool Completed = false, string? SettingsRecovery = null, string? RestoredSettingsHash = null, string? SettingsRecoveryHash = null);
    public sealed record Review(Session Session, string CurrentSettingsHash, FileState[] NewRuntimeFiles,
        string[] PreservedExistingFiles, string RecoveryDirectory);
    private static readonly string[] RuntimePaths =
    [
        "bin/x64/vrport.ini", "bin/x64/vrport-launcher.ini", "bin/x64/cyberpunkvrport.log",
        "red4ext/plugins/CyberpunkVR_Stereo/vrik_calibration.ini",
        "red4ext/plugins/cyberpunkvrport/config/vrport.ini",
        "red4ext/plugins/cyberpunkvrport/config/vrport-launcher.ini",
        "red4ext/plugins/cyberpunkvrport/config/vrik_calibration.ini",
        "red4ext/plugins/cyberpunkvrport/lua/CyberpunkVRPort_VRIK/vrik_settings.ini",
        "red4ext/plugins/cyberpunkvrport/lua/CyberpunkVRPort_VRIK/vrik_recenter.ini",
        "red4ext/plugins/cyberpunkvrport/log/cyberpunkvrport.log",
    ];

    private static bool KnownRuntimePath(string path) => RuntimePaths.Contains(path, StringComparer.Ordinal)
        || Regex.IsMatch(path, "^(bin/x64|red4ext/plugins/cyberpunkvrport/grips)/CyberpunkVR_[^/]+Grip_[^/]+[.]ini$");

    public static bool IsVrPlugin(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return path.Equals("red4ext/plugins/cyberpunkvrport/cyberpunkvrport.dll", StringComparison.OrdinalIgnoreCase)
            || path.Equals("red4ext/plugins/cyberpunkvrport/CyberpunkVR_Stereo.dll", StringComparison.OrdinalIgnoreCase)
            || path.Equals("red4ext/plugins/CyberpunkVR_Stereo/CyberpunkVR_Stereo.dll", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasVrFilesOnDisk(GameInstallation installation) => new[]
    {
        "red4ext/plugins/cyberpunkvrport/cyberpunkvrport.dll",
        "red4ext/plugins/cyberpunkvrport/CyberpunkVR_Stereo.dll",
        "red4ext/plugins/CyberpunkVR_Stereo/CyberpunkVR_Stereo.dll",
    }.Any(path => File.Exists(SafePath(installation.LocatorResult.Path.ToString(), path)));

    // Read Wine's expanded HKCU shell folder, not a hard-coded steamuser directory.
    // No fallback to the host's AppData or to another Windows user profile.
    public static string FindSettings(GameInstallation installation)
    {
        var prefix = installation.LocatorResult.LinuxCompatabilityDataProvider?.WinePrefixDirectoryPath.ToString()
            ?? throw new InvalidOperationException("The active Proton prefix is unavailable. Settings have been left unchanged.");
        var reg = SafePath(prefix, "user.reg");
        string? section = null;
        foreach (var line in File.ReadLines(reg))
        {
            if (line.StartsWith('[')) section = line.Split(']')[0].Replace("\\\\", "\\");
            if (section?.Equals("[Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            var match = Regex.Match(line, "^\"Local AppData\"=\"(?<path>.*)\"$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var windows = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (!windows.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Local AppData is not on the prefix's C drive. Choose and verify the prefix before restoring settings.");
            return SafePath(prefix, "drive_c/" + windows[3..].Replace('\\', '/') + "/CD Projekt Red/Cyberpunk 2077/UserSettings.json");
        }
        throw new InvalidOperationException("The active Windows user's Local AppData could not be resolved from Wine. No settings were changed.");
    }

    public static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new InvalidOperationException("Unsafe lifecycle path.");
        var path = root;
        // Refuse links, including broken links. A user-owned link must never turn a
        // settings restore or generated-file cleanup into a write outside this root.
        if (new DirectoryInfo(root).LinkTarget is not null) throw new InvalidOperationException("Lifecycle root is a symbolic link.");
        foreach (var part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, part);
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new InvalidOperationException($"Symbolic link left untouched: {path}");
        }
        return path;
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private string DirectoryFor(GameInstallation installation)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.LocatorResult.Path.ToString())));
        return SafePath(storageDirectory, key);
    }
    private static void ValidJson(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must contain a JSON object.");
    }
    private static void Stopped()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("Cyberpunk2077", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close Cyberpunk 2077 before reviewing or changing VR settings.");
                }
                catch (System.ComponentModel.Win32Exception) { /* process exited */ }
            }
        }
    }
    private static IEnumerable<string> RuntimeFiles(string root)
    {
        foreach (var path in RuntimePaths) if (File.Exists(SafePath(root, path))) yield return path;
        // Legacy captured hand poses have a documented prefix, not arbitrary INI ownership.
        foreach (var folder in new[] { "bin/x64", "red4ext/plugins/cyberpunkvrport/grips" })
        {
            var bin = SafePath(root, folder);
            if (Directory.Exists(bin))
                foreach (var path in Directory.EnumerateFiles(bin, "CyberpunkVR_*Grip_*.ini"))
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    _ = SafePath(root, relative);
                    yield return relative;
                }
        }
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, File.Exists(path)
            ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
    private static void Save(string directory, Session session) =>
        WriteAtomic(SafePath(directory, "session.json"), JsonSerializer.SerializeToUtf8Bytes(session));

    public async Task Prepare(GameInstallation installation, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            Stopped(); token.ThrowIfCancellationRequested();
            var root = installation.LocatorResult.Path.ToString();
            var settings = FindSettings(installation);
            var directory = DirectoryFor(installation);
            Directory.CreateDirectory(directory);
            Session? previous = null;
            if (File.Exists(SafePath(directory, "session.json")))
            {
                previous = ReadSession(installation);
                if (!previous.Completed) return;
            }
            ValidJson(settings);
            // Re-enable restores only absent, verified runtime preferences. Keep the
            // player's current flat-game settings as the next restoration point.
            if (previous is not null) RestoreArchivedPreferences(directory, previous);

            var original = File.ReadAllBytes(settings);
            var originalHash = Convert.ToHexString(SHA256.HashData(original));
            var before = RuntimeFiles(root).ToDictionary(p => p, p => Hash(SafePath(root, p)), StringComparer.Ordinal);
            if (previous is not null)
                foreach (var file in previous.ArchivedFiles) before.Remove(file.Path);
            // The plugin migrates these files at startup. Treat the destination of
            // a pre-existing preference as user-owned too, even after it was moved.
            foreach (var (path, hash) in before.ToArray())
            {
                string? destination = path switch
                {
                    "bin/x64/vrport.ini" => "red4ext/plugins/cyberpunkvrport/config/vrport.ini",
                    "bin/x64/vrport-launcher.ini" => "red4ext/plugins/cyberpunkvrport/config/vrport-launcher.ini",
                    "red4ext/plugins/CyberpunkVR_Stereo/vrik_calibration.ini" => "red4ext/plugins/cyberpunkvrport/config/vrik_calibration.ini",
                    _ when path.StartsWith("bin/x64/CyberpunkVR_", StringComparison.Ordinal) => "red4ext/plugins/cyberpunkvrport/grips/" + Path.GetFileName(path),
                    _ => null,
                };
                if (destination is not null) before.TryAdd(destination, hash);
            }
            token.ThrowIfCancellationRequested();
            var originalFile = "original-settings-" + Guid.NewGuid().ToString("N") + ".json";
            WriteAtomic(SafePath(directory, originalFile), original);
            Save(directory, new Session(root, settings, DateTimeOffset.UtcNow, originalHash, before, [], originalFile));
        }
        finally { _gate.Release(); }
    }

    private Session ReadSession(GameInstallation installation)
    {
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllBytes(SafePath(DirectoryFor(installation), "session.json")))
            ?? throw new InvalidOperationException("The VR recovery journal is invalid.");
        if (session.GameRoot != installation.LocatorResult.Path.ToString() || session.SettingsPath != FindSettings(installation))
            throw new InvalidOperationException("The game or active Proton user changed. Recovery files have been preserved; no automatic restore is safe.");
        return session;
    }

    public Review Inspect(GameInstallation installation)
    {
        Stopped();
        var session = ReadSession(installation);
        var files = RuntimeFiles(session.GameRoot).ToArray();
        return new Review(session, Hash(session.SettingsPath),
            files.Where(p => !session.ExistingFiles.ContainsKey(p)).Select(p => new FileState(p, Hash(SafePath(session.GameRoot, p)))).ToArray(),
            files.Where(session.ExistingFiles.ContainsKey).ToArray(), DirectoryFor(installation));
    }

    /// <summary>Explicit choice only. Keeps the current settings and every archived runtime file in recovery storage.</summary>
    public async Task<Session> FinishRemoval(GameInstallation installation, Review review, bool restoreSettings,
        bool archiveNewRuntimeFiles, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            Stopped(); token.ThrowIfCancellationRequested();
            var session = ReadSession(installation);
            if (HasVrFilesOnDisk(installation)) throw new InvalidOperationException("Apply removal of the VR plugin before restoring settings or archiving runtime files.");
            if (session.CapturedAt != review.Session.CapturedAt) throw new InvalidOperationException("The recovery session changed; review it again.");
            if (Hash(session.SettingsPath) != review.CurrentSettingsHash) throw new InvalidOperationException("Game settings changed after review. Review them again before restoring.");
            var directory = DirectoryFor(installation);
            var recovery = SafePath(directory, "recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery);
            if (restoreSettings && review.CurrentSettingsHash != session.OriginalHash)
            {
                var original = SafePath(directory, session.OriginalFile);
                ValidJson(original);
                if (Hash(original) != session.OriginalHash) throw new InvalidOperationException("The captured settings backup changed. Nothing was restored.");
                File.Copy(session.SettingsPath, SafePath(recovery, "settings-before-restore.json"));
                session = session with { SettingsRecovery = Path.GetRelativePath(directory, SafePath(recovery, "settings-before-restore.json")), RestoredSettingsHash = session.OriginalHash, SettingsRecoveryHash = review.CurrentSettingsHash };
                Save(directory, session);
                token.ThrowIfCancellationRequested();
                if (Hash(session.SettingsPath) != review.CurrentSettingsHash) throw new InvalidOperationException("Game settings changed during review.");
                WriteAtomic(session.SettingsPath, File.ReadAllBytes(original));
            }
            if (archiveNewRuntimeFiles)
            {
                foreach (var file in review.NewRuntimeFiles)
                {
                    token.ThrowIfCancellationRequested();
                    // Do not trust paths from a caller or an edited journal as ownership evidence.
                    if (session.ExistingFiles.ContainsKey(file.Path) || !RuntimeFiles(session.GameRoot).Contains(file.Path))
                        throw new InvalidOperationException("The runtime-file review is stale.");
                    var source = SafePath(session.GameRoot, file.Path);
                    if (Hash(source) != file.Hash) throw new InvalidOperationException($"{file.Path} changed after review; it has been left in place.");
                    var destination = SafePath(recovery, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                    using (var copy = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) copy.Flush(flushToDisk: true);
                    if (Hash(destination) != file.Hash) throw new IOException("Recovery copy verification failed.");
                    // Persist recovery information BEFORE removing a byte from the game.
                    session.ArchivedFiles.Add(new FileState(file.Path, file.Hash, Path.GetRelativePath(directory, destination)));
                    Save(directory, session);
                    token.ThrowIfCancellationRequested();
                    if (Hash(source) != file.Hash) throw new InvalidOperationException("A runtime file changed while being archived.");
                    File.Delete(source);
                }
            }
            session = session with { Completed = true };
            Save(directory, session);
            return session;
        }
        finally { _gate.Release(); }
    }
    public static FileState[] PortBackups(GameInstallation installation)
    {
        var settings = FindSettings(installation);
        var directory = Path.GetDirectoryName(settings)!;
        var result = new List<FileState>();
        foreach (var path in Directory.EnumerateFiles(directory, "UserSettings.pre-vr-*.json").Order(StringComparer.Ordinal))
        {
            var safe = SafePath(directory, Path.GetFileName(path));
            try { ValidJson(safe); result.Add(new FileState(Path.GetFileName(path), Hash(safe))); }
            catch (JsonException) { /* Corrupt backups are never offered for restoration. */ }
        }
        return result.ToArray();
    }

    public async Task RestorePortBackup(GameInstallation installation, FileState selectedBackup, string expectedCurrentHash,
        CancellationToken token = default)
    {
        // Capture the state we are about to replace. Never select the newest backup
        // on the user's behalf: the caller must name the reviewed, hashed file.
        if (HasVrFilesOnDisk(installation)) throw new InvalidOperationException("Apply removal of the VR plugin before restoring settings.");
        await Prepare(installation, token);
        await _gate.WaitAsync(token);
        try
        {
            Stopped(); token.ThrowIfCancellationRequested();
            var session = ReadSession(installation);
            if (!PortBackups(installation).Contains(selectedBackup)) throw new InvalidOperationException("The selected preset backup changed; review it again.");
            if (Hash(session.SettingsPath) != expectedCurrentHash) throw new InvalidOperationException("Settings changed after review.");
            var source = SafePath(Path.GetDirectoryName(session.SettingsPath)!, selectedBackup.Path);
            var directory = DirectoryFor(installation);
            var recoveryName = "settings-before-selected-restore-" + Guid.NewGuid().ToString("N") + ".json";
            File.Copy(session.SettingsPath, SafePath(directory, recoveryName));
            session = session with { SettingsRecovery = recoveryName, RestoredSettingsHash = selectedBackup.Hash, SettingsRecoveryHash = expectedCurrentHash, Completed = true };
            Save(directory, session);
            token.ThrowIfCancellationRequested();
            if (Hash(session.SettingsPath) != expectedCurrentHash || Hash(source) != selectedBackup.Hash)
                throw new InvalidOperationException("Settings or backup changed during restoration.");
            WriteAtomic(session.SettingsPath, File.ReadAllBytes(source));
        }
        finally { _gate.Release(); }
    }

    public async Task UndoRemoval(GameInstallation installation, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            Stopped(); token.ThrowIfCancellationRequested();
            var session = ReadSession(installation);
            var directory = DirectoryFor(installation);
            // Validate every destination before starting. Never replace a file created
            // by reinstallation or by the user after removal.
            var files = session.ArchivedFiles.GroupBy(f => f.Path).Select(group => group.Last()).ToArray();
            foreach (var file in files)
            {
                if (!KnownRuntimePath(file.Path)) throw new InvalidOperationException("Unknown recovery-file ownership.");
                var destination = SafePath(session.GameRoot, file.Path);
                if (File.Exists(destination) && Hash(destination) != file.Hash)
                    throw new InvalidOperationException($"{file.Path} already exists with different content. Recovery has not overwritten it.");
                if (file.RecoveryPath is null || Hash(SafePath(directory, file.RecoveryPath)) != file.Hash)
                    throw new InvalidOperationException("A runtime recovery copy changed or is missing.");
            }
            if (session.SettingsRecovery is not null)
            {
                var saved = SafePath(directory, session.SettingsRecovery);
                ValidJson(saved);
                if (session.SettingsRecoveryHash is null || Hash(saved) != session.SettingsRecoveryHash)
                    throw new InvalidOperationException("The settings recovery copy changed. No settings were overwritten.");
                var currentHash = Hash(session.SettingsPath);
                var restoredHash = session.RestoredSettingsHash ?? session.OriginalHash;
                if (currentHash != restoredHash && currentHash != Hash(saved))
                    throw new InvalidOperationException("Settings changed since restoration. Keep the current file and inspect the saved recovery copies.");
                token.ThrowIfCancellationRequested();
                if (currentHash == restoredHash) WriteAtomic(session.SettingsPath, File.ReadAllBytes(saved));
            }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var destination = SafePath(session.GameRoot, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Copy(SafePath(directory, file.RecoveryPath!), destination, overwrite: false);
            }
        }
        finally { _gate.Release(); }
    }

    private static void RestoreArchivedPreferences(string directory, Session session)
    {
        var files = session.ArchivedFiles.GroupBy(file => file.Path).Select(group => group.Last()).ToArray();
        foreach (var file in files)
        {
            if (!KnownRuntimePath(file.Path) || file.RecoveryPath is null || Hash(SafePath(directory, file.RecoveryPath)) != file.Hash)
                throw new InvalidOperationException("A saved VR preference is missing or corrupt. Re-enable stopped; recovery files were preserved.");
            var destination = SafePath(session.GameRoot, file.Path);
            if (File.Exists(destination) && Hash(destination) != file.Hash)
                throw new InvalidOperationException($"{file.Path} has newer content. Re-enable stopped without replacing it.");
        }
        foreach (var file in files)
        {
            var destination = SafePath(session.GameRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(SafePath(directory, file.RecoveryPath!), destination, overwrite: false);
        }
    }

    public sealed record RemovalIntent(Review Review, bool RestoreSettings);

    public bool HasSession(GameInstallation installation) => File.Exists(SafePath(DirectoryFor(installation), "session.json"));
    public bool HasPendingRemoval(GameInstallation installation) => File.Exists(SafePath(DirectoryFor(installation), "pending-removal.json"));

    public bool PendingRemovalNeedsReview(GameInstallation installation)
    {
        var path = SafePath(DirectoryFor(installation), "pending-removal.json");
        if (!File.Exists(path)) return false;
        var intent = JsonSerializer.Deserialize<RemovalIntent>(File.ReadAllBytes(path))
            ?? throw new InvalidOperationException("Invalid VR removal intent; recovery copies were preserved.");
        var current = Inspect(installation);
        if (current.Session.Completed) return false;
        if (current.CurrentSettingsHash != intent.Review.CurrentSettingsHash &&
            current.CurrentSettingsHash != current.Session.RestoredSettingsHash) return true;
        var approved = intent.Review.NewRuntimeFiles.ToDictionary(file => file.Path, file => file.Hash, StringComparer.Ordinal);
        return current.NewRuntimeFiles.Any(file => !approved.TryGetValue(file.Path, out var hash) || hash != file.Hash);
    }

    public void PlanRemoval(GameInstallation installation, Review review, bool restoreSettings)
    {
        Stopped();
        if (Hash(review.Session.SettingsPath) != review.CurrentSettingsHash)
            throw new InvalidOperationException("Settings changed during review. Apply again to review the new settings.");
        WriteAtomic(SafePath(DirectoryFor(installation), "pending-removal.json"),
            JsonSerializer.SerializeToUtf8Bytes(new RemovalIntent(review, restoreSettings)));
    }

    public async Task CompletePendingRemoval(GameInstallation installation)
    {
        var path = SafePath(DirectoryFor(installation), "pending-removal.json");
        if (!File.Exists(path)) return;
        var intent = JsonSerializer.Deserialize<RemovalIntent>(File.ReadAllBytes(path))
            ?? throw new InvalidOperationException("Invalid VR removal intent. Recovery copies have been preserved.");
        var session = ReadSession(installation);
        if (!session.Completed)
        {
            var current = Inspect(installation);
            // Resume only the exact reviewed transaction. A restore may already have
            // completed before interruption; never overwrite a later settings edit.
            if (current.CurrentSettingsHash != intent.Review.CurrentSettingsHash &&
                current.CurrentSettingsHash != session.RestoredSettingsHash)
                throw new InvalidOperationException("Settings changed since VR removal was approved. Recovery copies are preserved; review settings before retrying.");
            var remaining = intent.Review.NewRuntimeFiles.Where(file =>
                File.Exists(SafePath(session.GameRoot, file.Path))).ToArray();
            await FinishRemoval(installation, intent.Review with { CurrentSettingsHash = current.CurrentSettingsHash, NewRuntimeFiles = remaining },
                intent.RestoreSettings, archiveNewRuntimeFiles: true);
        }
        File.Delete(path);
    }

    public async Task ReconcileArchivedOverrides(GameInstallation installation, IConnection connection, Apocrypha.Sdk.Loadouts.Loadout.ReadOnly loadout)
    {
        // Resume the user's already-journaled archive intent after interruption.
        // This only clears matching absent overrides; the regular Apply performs
        // synchronization and retains control over all other pending changes.
        if (!File.Exists(SafePath(DirectoryFor(installation), "session.json"))) return;
        var session = ReadSession(installation);
        if (session.ArchivedFiles.Count == 0) return;
        foreach (var file in session.ArchivedFiles.GroupBy(f => f.Path).Select(group => group.Last()))
        {
            if (!KnownRuntimePath(file.Path) || file.RecoveryPath is null || Hash(SafePath(DirectoryFor(installation), file.RecoveryPath)) != file.Hash)
                throw new InvalidOperationException("A runtime recovery copy changed or is missing. Its loadout entry has been preserved.");
        }
        await ForgetArchivedOverrides(connection, loadout, session);
    }

    private static async Task ForgetArchivedOverrides(IConnection connection, Apocrypha.Sdk.Loadouts.Loadout.ReadOnly loadout, CyberpunkVrLifecycle.Session session)
    {
        using var tx = connection.BeginTransaction();
        var paths = session.ArchivedFiles.Where(f => KnownRuntimePath(f.Path)).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var group in LoadoutItem.FindByLoadout(connection.Db, loadout).OfTypeLoadoutItemGroup().OfTypeLoadoutOverridesGroup())
        foreach (var item in group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
        {
            var path = (GamePath)item.TargetPath;
            if (path.LocationId != LocationId.Game || !paths.Contains(path.Path.ToString())) continue;
            // A file recreated since cleanup belongs to the user again; never remove its entry.
            if (File.Exists(CyberpunkVrLifecycle.SafePath(session.GameRoot, path.Path.ToString()))) continue;
            tx.Delete(item.Id, recursive: true);
        }
        await tx.Commit();
    }
}
