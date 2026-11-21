using Apocrypha.Abstractions.Loadouts;
using Apocrypha.App.UI.Dialog;
using Apocrypha.App.UI.Dialog.Enums;
using Apocrypha.App.UI.Windows;
using Apocrypha.Games.RedEngine.Cyberpunk2077;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using Apocrypha.Sdk;
using Apocrypha.Sdk.Games;
using Apocrypha.Sdk.Loadouts;
using Apocrypha.UI.Sdk.Dialog;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Hashing.xxHash3;

namespace Apocrypha.App.UI.LeftMenu.Loadout;

internal static class CyberpunkVrRecoveryDialogs
{
    private static readonly ButtonDefinitionId Restore = ButtonDefinitionId.From("vr-restore");
    private static readonly ButtonDefinitionId Archive = ButtonDefinitionId.From("vr-archive");
    private static readonly ButtonDefinitionId Capture = ButtonDefinitionId.From("vr-capture");
    private static readonly ButtonDefinitionId Backups = ButtonDefinitionId.From("vr-backups");
    private static readonly ButtonDefinitionId Next = ButtonDefinitionId.From("vr-next");
    private static readonly ButtonDefinitionId Undo = ButtonDefinitionId.From("vr-undo");

    internal static async Task Show(IServiceProvider provider, Sdk.Loadouts.Loadout.ReadOnly loadout)
    {
        var windows = provider.GetRequiredService<IWindowManager>();
        async Task<StandardDialogResult> Dialog(string text, params DialogButtonDefinition[] buttons) =>
            await windows.ShowDialog(DialogFactory.CreateStandardDialog("CyberpunkVR settings and recovery",
                new StandardDialogParameters { Text = text }, buttons), DialogWindowType.Modal);
        async Task ReviewBackups(CyberpunkVrLifecycle lifecycle, GameInstallation installation)
        {
            if (RunCyberpunk2077Game.HasVrPayload(loadout) || CyberpunkVrLifecycle.HasVrFilesOnDisk(installation))
                throw new InvalidOperationException("Uninstall and apply removal before restoring preset backups.");
            foreach (var backup in CyberpunkVrLifecycle.PortBackups(installation))
            {
                var expected = CyberpunkVrLifecycle.Hash(CyberpunkVrLifecycle.FindSettings(installation));
                var selection = await Dialog($"Selected preset backup: {backup.Path}\n\n" +
                    "Restoring replaces your current settings with this exact file. Your current settings will be saved for Undo.\n\n" +
                    "Choose Next to inspect another backup. No backup is selected automatically.", DialogStandardButtons.Cancel,
                    new DialogButtonDefinition("Next backup", Next), new DialogButtonDefinition("Restore this backup", Restore));
                if (selection.ButtonId == Restore) { await lifecycle.RestorePortBackup(installation, backup, expected); return; }
                if (selection.ButtonId != Next) return;
            }
            await Dialog("No more valid preset backups are available. Your current settings have been kept.", DialogStandardButtons.Ok);
        }
        try
        {
            var lifecycle = provider.GetRequiredService<CyberpunkVrLifecycle>();
            var installation = loadout.InstallationInstance;
            CyberpunkVrLifecycle.Review review;
            try { review = lifecycle.Inspect(installation); }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                var choice = await Dialog("A settings snapshot is captured before a VR launch from Apocrypha.\n\n" +
                    "Capture the current settings now only if you want this exact state as your restoration point. " +
                    "An installation already used in VR may already contain the VR preset. Existing files and the port's own backups will be preserved.",
                    DialogStandardButtons.Cancel, new DialogButtonDefinition("Capture settings", Capture),
                    new DialogButtonDefinition("Preset backups", Backups));
                if (choice.ButtonId == Capture) await lifecycle.Prepare(installation);
                if (choice.ButtonId == Backups) await ReviewBackups(lifecycle, installation);
                return;
            }
            if (RunCyberpunk2077Game.HasVrPayload(loadout) || CyberpunkVrLifecycle.HasVrFilesOnDisk(installation))
            {
                await Dialog($"Settings were captured on {review.Session.CapturedAt.LocalDateTime:g}.\n\n" +
                    "Preferences are preserved across updates. To restore settings or archive newly generated files, uninstall CyberpunkVR, apply the change, and close the game first.\n\n" +
                    $"Recovery copies: {review.RecoveryDirectory}", DialogStandardButtons.Ok);
                return;
            }
            if (review.Session.Completed)
            {
                var choice = await Dialog("The removal review is complete. Recovery copies have been kept. Undo restores only absent runtime files and refuses to overwrite newly edited settings.\n\n" +
                    review.RecoveryDirectory, DialogStandardButtons.Cancel, new DialogButtonDefinition("Undo last cleanup", Undo),
                    new DialogButtonDefinition("Preset backups", Backups));
                if (choice.ButtonId == Undo) await lifecycle.UndoRemoval(installation);
                if (choice.ButtonId == Backups) await ReviewBackups(lifecycle, installation);
                return;
            }
            var result = await Dialog($"Restore the settings captured on {review.Session.CapturedAt.LocalDateTime:g}? " +
                "Restore and archive restores that snapshot and moves the listed files to recovery storage. Archive VR files keeps current settings. Your current settings are saved before restoration so it can be undone. The snapshot may already include VR changes if the mod was used before capture.\n\n" +
                $"{review.NewRuntimeFiles.Length} known runtime files appeared after capture and can be moved to recovery storage:\n" +
                string.Join("\n", review.NewRuntimeFiles.Select(f => f.Path)) + "\n\n" +
                $"{review.PreservedExistingFiles.Length} pre-existing files and all unknown files will stay in place.\n\n" +
                "The port's timestamped preset backups are also kept; no older backup is selected automatically.\n\n" +
                $"Recovery copies: {review.RecoveryDirectory}",
                new DialogButtonDefinition("Keep everything", ButtonDefinitionId.Cancel),
                new DialogButtonDefinition("Archive VR files", Archive),
                new DialogButtonDefinition("Restore and archive", Restore),
                new DialogButtonDefinition("Preset backups", Backups));
            if (result.ButtonId == Backups) { await ReviewBackups(lifecycle, installation); return; }
            if (result.ButtonId != Restore && result.ButtonId != Archive) return;
            var session = await lifecycle.FinishRemoval(installation, review, result.ButtonId == Restore, archiveNewRuntimeFiles: true);
            await lifecycle.ReconcileArchivedOverrides(installation, provider.GetRequiredService<IConnection>(), loadout);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await Dialog(exception.Message + "\n\nUncertain files and recovery copies have been preserved.", DialogStandardButtons.Ok);
        }
    }

}
