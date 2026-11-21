using System.Security.Cryptography;
using System.Text;
using DynamicData.Kernel;
using Microsoft.AspNetCore.WebUtilities;
using Apocrypha.Abstractions.NexusWebApi;
using Apocrypha.Abstractions.NexusWebApi.DTOs.OAuth;
using Apocrypha.Abstractions.NexusWebApi.Types;
using Apocrypha.Networking.NexusWebApi.Auth;
using Apocrypha.Sdk;
using Apocrypha.Sdk.Jobs;
using R3;

namespace Apocrypha.Networking.NexusWebApi;

internal sealed class OAuthJob : IOAuthJob, IJobDefinitionWithStart<OAuthJob, Optional<JwtTokenReply>>
{
    private readonly IIDGenerator _idGenerator;
    private CancellationToken _callerToken;
    private TimeSpan _timeout;
    private readonly IOSInterop _os;
    private readonly HttpClient _httpClient;
    private readonly Observable<NXMOAuthUrl> _nxmUrlMessages;

    public BehaviorSubject<Uri?> LoginUriSubject { get; } = new(initialValue: null);

    private OAuthJob(
        IIDGenerator idGenerator,
        IOSInterop os,
        HttpClient httpClient,
        Observable<NXMOAuthUrl> nxmUrlMessages)
    {
        _idGenerator = idGenerator;
        _os = os;
        _httpClient = httpClient;
        _nxmUrlMessages = nxmUrlMessages;
    }

    public static IJobTask<OAuthJob, Optional<JwtTokenReply>> Create(
        IJobMonitor jobMonitor,
        IIDGenerator idGenerator,
        IOSInterop os,
        HttpClient httpClient,
        Observable<NXMOAuthUrl> nxmUrlMessages,
        CancellationToken callerToken,
        TimeSpan timeout)
    {
        var job = new OAuthJob(idGenerator, os, httpClient, nxmUrlMessages);
        job._callerToken = callerToken;
        job._timeout = timeout;
        return jobMonitor.Begin<OAuthJob, Optional<JwtTokenReply>>(job);
    }

    public async ValueTask<Optional<JwtTokenReply>> StartAsync(IJobContext<OAuthJob> context)
    {
        // see https://www.rfc-editor.org/rfc/rfc7636#section-4.1
        var codeVerifier = Convert.ToBase64String(Encoding.UTF8.GetBytes(_idGenerator.UUIDv4()));

        // see https://www.rfc-editor.org/rfc/rfc7636#section-4.2
        var codeChallengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        var codeChallenge = WebEncoders.Base64UrlEncode(codeChallengeBytes);

        var state = _idGenerator.UUIDv4();
        var uri = OAuth.GenerateAuthorizeUrl(codeChallenge, state);
        LoginUriSubject.OnNext(uri);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _callerToken);
        cts.CancelAfter(_timeout);

        // Start listening first, otherwise we might miss the message
        var codeTask = _nxmUrlMessages
            .Where(state, static (oauth, state) => oauth.State == state)
            .Select(url => url.OAuth.Code)
            .WhereNotNull()
            .FirstAsync(cts.Token);

        try
        {
            try { await _os.OpenUriAsync(uri, cts.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new OAuthFlowException("Could not open the browser. Check your desktop session/portal, then retry. You can also open the authorization link shown in the login job."); }
            var code = await codeTask;
            return await OAuth.AuthorizeToken(codeVerifier, code, _httpClient, cts.Token);
        }
        catch (OperationCanceledException) when (!_callerToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            throw new OAuthFlowException("Authorization timed out. Check that nxm links open this running session. Isolated sessions need explicit callback forwarding. Retry login.");
        }
        finally
        {
            await cts.CancelAsync();
            // Observe a cancelled subscription even when browser opening fails.
            try { await codeTask; } catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        LoginUriSubject.Dispose();
    }
}
