namespace Apocrypha.Networking.NexusWebApi.Auth;

/// <summary>Safe, actionable authentication error without URL, code, response body or token data.</summary>
public sealed class OAuthFlowException(string message) : Exception(message);
