using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Apocrypha.Abstractions.NexusWebApi.DTOs.OAuth;
using Apocrypha.Abstractions.NexusWebApi.Types;
using Apocrypha.Networking.NexusWebApi.Auth;
using Apocrypha.Sdk;
using Apocrypha.Sdk.Jobs;
using NSubstitute;

namespace Apocrypha.Networking.NexusWebApi.Tests;

public class OAuthTests
{
    // ReSharper disable once InconsistentNaming
    private readonly Uri ExpectedAuthURL = new("https://users.nexusmods.com/oauth/authorize?response_type=code&scope=openid profile email&code_challenge_method=S256&client_id=nma&redirect_uri=nxm%3A%2F%2Foauth%2Fcallback&code_challenge=QMZ4D7BLeehAXINE9NZ8dho2i5AYVTbfqJ8PhQ4eUrE&state=00000000-0000-0000-0000-000000000000");
    private readonly ILogger<OAuth> _logger;
    private readonly IJobMonitor _jobMonitor;

    // ReSharper disable once ContextualLoggerProblem
    public OAuthTests(ILogger<OAuth> logger, IJobMonitor jobMonitor)
    {
        _logger = logger;
        _jobMonitor = jobMonitor;
    }

    [Fact]
    public async Task AuthorizeRequestTest()
    {
        #region Setup
        var stateId = "00000000-0000-0000-0000-000000000000";

        var messageHandler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        messageHandler
            .SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(ReplyToken)),
            }));

        var httpClient = new HttpClient(messageHandler);

        var idGen = Substitute.For<IIDGenerator>();
        idGen.UUIDv4().Returns(stateId);

        var os = Substitute.For<IOSInterop>();
        #endregion

        #region Execution
        var oauth = new OAuth(_jobMonitor, _logger, httpClient, idGen, os);
        var tokenTask = oauth.AuthorizeRequest(CancellationToken.None);
        await WaitForBrowser(os);
        oauth.AddUrl(NXMUrl.Parse($"nxm://oauth/callback?state={stateId}&code=code").OAuth);
        var result = await tokenTask;
        #endregion

        #region Verification

        _ = idGen.Received(2).UUIDv4();
        await os.Received(1).OpenUriAsync(ExpectedAuthURL, Arg.Any<CancellationToken>());
        result.Should().BeEquivalentTo(ReplyToken);
        #endregion
    }

    [Fact]
    public async Task RefreshTokenTest()
    {
        #region Setup
        var stateId = "00000000-0000-0000-0000-000000000000";

        var messageHandler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        messageHandler
            .SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(ReplyToken)),
            }));

        var httpClient = new HttpClient(messageHandler);

        var idGen = Substitute.For<IIDGenerator>();
        idGen.UUIDv4().Returns(stateId);

        var os = Substitute.For<IOSInterop>();
        #endregion

        #region Execution
        var oauth = new OAuth(_jobMonitor, _logger, httpClient, idGen, os);
        var token = await oauth.RefreshToken("refresh_token", CancellationToken.None);
        #endregion

        #region Verification

        _ = idGen.DidNotReceive().UUIDv4();
        os.DidNotReceive().OpenUri(Arg.Any<Uri>());
        token.Should().BeEquivalentTo(ReplyToken);

        #endregion
    }

    [Fact]
    public async Task ThrowsOnInvalidResponse()
    {
        #region Setup
        var stateId = "00000000-0000-0000-0000-000000000000";

        var messageHandler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        messageHandler
            .SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("foo"),
            }));

        var httpClient = new HttpClient(messageHandler);

        var idGen = Substitute.For<IIDGenerator>();
        idGen.UUIDv4().Returns(stateId);

        var os = Substitute.For<IOSInterop>();
        #endregion

        #region Execution
        var oauth = new OAuth(_jobMonitor, _logger, httpClient, idGen, os);
        Func<Task> call = () => oauth.AuthorizeRequest(CancellationToken.None);
        var tokenTask = call.Should().ThrowAsync<JsonException>();
        await WaitForBrowser(os);
        oauth.AddUrl(NXMUrl.Parse($"nxm://oauth/callback?state={stateId}&code=code").OAuth);
        await tokenTask;
        #endregion
    }

    [Fact]
    public async Task RefreshToken_RejectedRefresh_ThrowsSessionExpired()
    {
        // Nexus rejects a dead session with 400 + an OAuth error body; blindly deserializing
        // it used to produce a null-field token and the cryptic "Invalid new token" log.
        var messageHandler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        messageHandler
            .SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadRequest,
                Content = new StringContent("""{"error":"invalid_grant","error_description":"The provided authorization grant is invalid"}"""),
            }));

        var oauth = new OAuth(_jobMonitor, _logger, new HttpClient(messageHandler), Substitute.For<IIDGenerator>(), Substitute.For<IOSInterop>());

        Func<Task> call = () => oauth.RefreshToken("refresh_token", CancellationToken.None);
        await call.Should().ThrowAsync<OAuthSessionExpiredException>();
    }

    [Fact]
    public async Task RefreshToken_TransientServerError_DoesNotCountAsExpired()
    {
        var messageHandler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        messageHandler
            .SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Content = new StringContent("<html>upstream error</html>"),
            }));

        var oauth = new OAuth(_jobMonitor, _logger, new HttpClient(messageHandler), Substitute.For<IIDGenerator>(), Substitute.For<IOSInterop>());

        // A Nexus outage must NOT log the user out — it surfaces as a plain HTTP failure.
        Func<Task> call = () => oauth.RefreshToken("refresh_token", CancellationToken.None);
        await call.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact(Timeout = 10000)]
    public async Task WrongStateCannotExchangeTokensAndCorrectCallbackCanStillComplete()
    {
        var handler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        handler.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(JsonSerializer.Serialize(ReplyToken)) }));
        var id = Substitute.For<IIDGenerator>(); id.UUIDv4().Returns("expected-state");
        var os = Substitute.For<IOSInterop>();
        var oauth = new OAuth(_jobMonitor, _logger, new HttpClient(handler), id, os);
        var task = oauth.AuthorizeRequest(default);
        await WaitForBrowser(os);
        oauth.AddUrl(NXMUrl.Parse("nxm://oauth/callback?state=wrong-state&code=fixture").OAuth);
        await Task.Delay(30);
        Assert.False(task.IsCompleted);
        await handler.DidNotReceive().SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
        oauth.AddUrl(NXMUrl.Parse("nxm://oauth/callback?state=expected-state&code=fixture").OAuth);
        Assert.NotNull(await task);
    }

    [Fact(Timeout = 10000)]
    public async Task BrowserFailureIsActionableAndAllowsRetry()
    {
        var os = Substitute.For<IOSInterop>();
        os.OpenUriAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("fixture portal unavailable")));
        var id = Substitute.For<IIDGenerator>(); id.UUIDv4().Returns("retry-state");
        var handler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        handler.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(JsonSerializer.Serialize(ReplyToken)) }));
        var oauth = new OAuth(_jobMonitor, _logger, new HttpClient(handler), id, os);
        var error = await Assert.ThrowsAsync<OAuthFlowException>(() => oauth.AuthorizeRequest(default));
        Assert.Contains("browser", error.Message);
        os.OpenUriAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var retry = oauth.AuthorizeRequest(default);
        await WaitForBrowser(os, 2);
        oauth.AddUrl(NXMUrl.Parse("nxm://oauth/callback?state=retry-state&code=fixture").OAuth);
        Assert.NotNull(await retry);
    }

    [Fact(Timeout = 10000)]
    public async Task TimeoutExplainsRoutingAndCallerCanCancel()
    {
        var oauth = new OAuth(_jobMonitor, _logger, new HttpClient(), Substitute.For<IIDGenerator>(), Substitute.For<IOSInterop>());
        var error = await Assert.ThrowsAsync<OAuthFlowException>(() => oauth.AuthorizeRequest(default, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("nxm", error.Message);
        using var cts = new CancellationTokenSource();
        var task = oauth.AuthorizeRequest(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact(Timeout = 10000)]
    public async Task TokenExchangeUsesPkceVerifierAndNeverAcceptsAnErrorResponse()
    {
        var handler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        string? body = null;
        handler.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            body = await call.ArgAt<HttpRequestMessage>(0).Content!.ReadAsStringAsync();
            return new HttpResponseMessage { StatusCode = HttpStatusCode.BadRequest, Content = new StringContent("fixture-sensitive-server-body") };
        });
        var id = Substitute.For<IIDGenerator>(); id.UUIDv4().Returns("pkce-state");
        var os = Substitute.For<IOSInterop>();
        var oauth = new OAuth(_jobMonitor, _logger, new HttpClient(handler), id, os);
        var task = oauth.AuthorizeRequest(default);
        await WaitForBrowser(os);
        oauth.AddUrl(NXMUrl.Parse("nxm://oauth/callback?state=pkce-state&code=fixture-code").OAuth);
        var error = await Assert.ThrowsAsync<OAuthFlowException>(() => task);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Contains("code_verifier=", body); Assert.Contains("redirect_uri=nxm", body);
    }

    private static async Task WaitForBrowser(IOSInterop os, int count = 1)
    {
        for (var i = 0; i < 500; i++)
        {
            if (os.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IOSInterop.OpenUriAsync)) >= count) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Fixture browser was not opened");
    }

    private static readonly JwtTokenReply ReplyToken =
        new()
        {
            AccessToken = "access_token",
            RefreshToken = "refresh_token",
            Scope = "public",
            Type = "Bearer",
            CreatedAt = 1677143380,
            ExpiresIn = 21600,
        };

    [Fact]
    public void Test_GenerateAuthorizeUrl()
    {
        var res = OAuth.GenerateAuthorizeUrl("QMZ4D7BLeehAXINE9NZ8dho2i5AYVTbfqJ8PhQ4eUrE", "00000000-0000-0000-0000-000000000000");
        res.Should().Be(ExpectedAuthURL);
    }
}
