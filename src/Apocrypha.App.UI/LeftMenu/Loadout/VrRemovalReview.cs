using Avalonia.Threading;
using Apocrypha.App.UI.Dialog;
using Apocrypha.App.UI.Dialog.Enums;
using Apocrypha.App.UI.Windows;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using Apocrypha.Sdk.Games;
using Apocrypha.UI.Sdk.Dialog;

namespace Apocrypha.App.UI.LeftMenu.Loadout;

internal sealed class VrRemovalReview(IWindowManager windows) : IVrRemovalReview
{
    public async Task<VrRemovalChoice> Choose(GameInstallation installation, CyberpunkVrLifecycle.Review review)
    {
        var keep = ButtonDefinitionId.From("vr-keep-settings");
        var restore = ButtonDefinitionId.From("vr-restore-settings");
        var result = await Dispatcher.UIThread.InvokeAsync(() => windows.ShowDialog(
            DialogFactory.CreateStandardDialog("Disable CyberpunkVR",
                new StandardDialogParameters
                {
                    Text = $"Choose settings for conventional gameplay before applying this change.\n\n" +
                        $"Restore uses the snapshot from {review.Session.CapturedAt.LocalDateTime:g}; it may replace preferences changed since then. " +
                        "Keep current settings preserves your latest choices. Both options archive newly generated VR preferences for recovery. " +
                        "Pre-existing and unknown files stay in place.\n\n" +
                        string.Join("\n", review.NewRuntimeFiles.Select(file => file.Path)) + "\n\n" +
                        $"Recovery copies: {review.RecoveryDirectory}",
                },
                [DialogStandardButtons.Cancel,
                 new DialogButtonDefinition("Keep current settings", keep),
                 new DialogButtonDefinition("Restore pre-VR settings", restore)]), DialogWindowType.Modal));
        return result.ButtonId == keep ? VrRemovalChoice.KeepSettings :
            result.ButtonId == restore ? VrRemovalChoice.RestoreSettings : VrRemovalChoice.Cancel;
    }
}
