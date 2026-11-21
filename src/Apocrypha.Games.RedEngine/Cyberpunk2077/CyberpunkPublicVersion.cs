namespace Apocrypha.Games.RedEngine.Cyberpunk2077;

/// <summary>Reviewed executable identities. Never infer a public version from an unknown Steam build.</summary>
public static class CyberpunkPublicVersion
{
    // Steam Windows build 26161058, base manifest 3524634124112875626; executable
    // previously verified against that cached Steam manifest. Its public release string is 2.5;
    // PE FileVersion 3.0.5813403 is an internal build and ProductVersion is empty.
    // Publisher release: https://www.cyberpunk.net/en/news/52125/cyberpunk-2077s-edgerunners-2-update-2-5-is-out-now
    // Only this exact binary is associated: hashing ~62 MB does not verify the rest of the game.
    public static string? FromExecutableHash(string hash) =>
        hash.Equals("d36e74c47592e9c656ae250078cb40b2be63c782af5bfbeff2280d43e653fab4", StringComparison.OrdinalIgnoreCase) ? "2.5" : null;
}
