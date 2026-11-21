namespace Apocrypha.Sdk;

/// <summary>Explicit opt-out for isolated sessions; XDG state isolation alone does not isolate desktop services.</summary>
public static class DesktopIntegration
{
    public static bool IsDisabled => Environment.GetEnvironmentVariable("APOCRYPHA_NO_DESKTOP_INTEGRATION") == "1";
}
