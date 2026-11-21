using Apocrypha.Sdk.Games;

namespace Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;

/// <summary>User choice at the normal Apply/unmanage boundary, before removing VR.</summary>
public interface IVrRemovalReview
{
    Task<VrRemovalChoice> Choose(GameInstallation installation, CyberpunkVrLifecycle.Review review);
}

/// <summary>Every choice preserves recovery copies; cancellation makes no deployment changes.</summary>
public enum VrRemovalChoice { Cancel, KeepSettings, RestoreSettings }
