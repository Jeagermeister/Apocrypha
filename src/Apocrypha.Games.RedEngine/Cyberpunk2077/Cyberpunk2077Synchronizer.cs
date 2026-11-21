using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Abstractions.Loadouts.Synchronizers;
using Apocrypha.Sdk.Settings;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Emitters;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk.Loadouts;
using R3;

namespace Apocrypha.Games.RedEngine.Cyberpunk2077;

public class Cyberpunk2077Synchronizer : ALoadoutSynchronizer
{
    private Cyberpunk2077Settings _settings;
    
    /// <summary>
    /// Redmod deploys combined mods to the redmod cache folder
    /// </summary>
    private static GamePath RedModCacheFolder => new(LocationId.Game, "r6/cache/modded");
    
    /// <summary>
    /// Redmod stages the scripts in the redmod/scripts folder
    /// </summary>
    private static GamePath RedModScriptsFolder => new(LocationId.Game, "tools/redmod/scripts");
    
    /// <summary>
    /// Redmod stages the tweaks in the redmod/tweaks folder
    /// </summary>
    private static GamePath RedModTweaksFolder => new(LocationId.Game, "tools/redmod/tweaks");
    
    private static GamePath ArchivePcContentFolder => new(LocationId.Game, "archive/pc/content");
    
    private static GamePath ArchivePcEp1Folder => new(LocationId.Game, "archive/pc/ep1");
    
    
    private readonly RedModDeployTool _redModTool;
    private readonly Vr.CyberpunkVrLifecycle _vrLifecycle;
    private readonly IServiceProvider _provider;
    
    protected internal Cyberpunk2077Synchronizer(IServiceProvider provider) : base(provider)
    {
        _provider = provider;
        _vrLifecycle = provider.GetRequiredService<Vr.CyberpunkVrLifecycle>();
        var settingsManager = provider.GetRequiredService<ISettingsManager>();

        _settings = settingsManager.Get<Cyberpunk2077Settings>();
        settingsManager.GetChanges<Cyberpunk2077Settings>(prependCurrent: false).Subscribe(value => _settings = value);
        _redModTool = provider.GetServices<ITool>().OfType<RedModDeployTool>().First();
    }

    private static readonly GamePath[] IgnoredBackupFolders =
    [
        ArchivePcContentFolder,
        ArchivePcEp1Folder,
    ];

    public override async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job)
    {
        await _vrLifecycle.ReconcileArchivedOverrides(loadout.InstallationInstance, Connection, loadout);
        loadout = await base.Synchronize(loadout.Rebase(), job);
        if (!MissingRedModEmitter.HasRedMods(loadout, out _, out var numRedModDirs)) return loadout;
        if (!MissingRedModEmitter.HasRedModToolInstalled(loadout, out _))
        {
            Logger.LogWarning("RedMod tool isn't installed but the loadout contains `{Count}` red mods", numRedModDirs);
            return loadout;
        }

        await _redModTool.Execute(loadout, CancellationToken.None);
        return await base.Synchronize(loadout, job);
    }


    protected override async Task<Func<Task>?> PrepareLifecycle(GameInstallation installation, Dictionary<GamePath, SyncNode> tree)
    {
        var enabled = tree.Any(pair => pair.Key.LocationId == LocationId.Game &&
            Vr.CyberpunkVrLifecycle.IsVrPlugin(pair.Key.Path.ToString()) && pair.Value.HaveLoadout);
        if (_vrLifecycle.HasPendingRemoval(installation))
        {
            if (enabled) throw new InvalidOperationException("Finish the pending VR removal with Apply before enabling VR again.");
            if (_vrLifecycle.PendingRemovalNeedsReview(installation))
                await ReviewRemoval(installation, _vrLifecycle.Inspect(installation));
            return () => _vrLifecycle.CompletePendingRemoval(installation);
        }
        if (enabled)
        {
            var red4ext = new GamePath(LocationId.Game, "red4ext/RED4ext.dll");
            var loader = new GamePath(LocationId.Game, "bin/x64/winmm.dll");
            if (!tree.TryGetValue(red4ext, out var dependency) || !dependency.HaveLoadout ||
                !tree.TryGetValue(loader, out var bootstrap) || !bootstrap.HaveLoadout)
                throw new InvalidOperationException("CyberpunkVR requires RED4ext. Install and enable RED4ext 1.31.0 or newer in this loadout before applying VR.");
            // Capture before deployment, including when the next launch is via Steam.
            await _vrLifecycle.Prepare(installation);
            return null;
        }
        if (!_vrLifecycle.HasSession(installation)) return null;
        var review = _vrLifecycle.Inspect(installation);
        if (review.Session.Completed) return null;
        await ReviewRemoval(installation, review);
        return () => _vrLifecycle.CompletePendingRemoval(installation);
    }

    private async Task ReviewRemoval(GameInstallation installation, Vr.CyberpunkVrLifecycle.Review review)
    {
        var interaction = _provider.GetService<Vr.IVrRemovalReview>()
            ?? throw new InvalidOperationException("Removing VR requires a settings choice. Apply this change in the desktop application.");
        var choice = await interaction.Choose(installation, review);
        if (choice == Vr.VrRemovalChoice.Cancel) throw new OperationCanceledException("VR removal cancelled before changing files.");
        _vrLifecycle.PlanRemoval(installation, review, choice == Vr.VrRemovalChoice.RestoreSettings);
    }

    public override bool ShouldSynchronize(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
        => _vrLifecycle.HasPendingRemoval(loadout.InstallationInstance) || base.ShouldSynchronize(loadout, previousDiskState, lastScannedDiskState);

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup)
            return false;
        
        if (path.LocationId != LocationId.Game)
            return false;
        
        return IgnoredBackupFolders.Any(ignore => path.Path.InFolder(ignore.Path));
    }

}
