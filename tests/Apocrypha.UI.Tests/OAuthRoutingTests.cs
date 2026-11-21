using System.Net;
using System.Text.Json;
using Apocrypha.CLI.Types;
using Apocrypha.SingleProcess;
using Apocrypha.Networking.NexusWebApi.Auth;
using Apocrypha.Abstractions.NexusWebApi.Types;
using Apocrypha.Abstractions.NexusWebApi.DTOs.OAuth;
using Apocrypha.Sdk;
using Apocrypha.Sdk.Jobs;
using Apocrypha.Sdk.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Apocrypha.UI.Tests;

[Collection("Game registration")]
public class OAuthRoutingTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact(Timeout=15000)]
    public async Task CallbackForwardedOverRealIpcCompletesOnlyTheWaitingInstance()
    {
        var id = Substitute.For<IIDGenerator>(); id.UUIDv4().Returns("ipc-state");
        var os = Substitute.For<IOSInterop>();
        var oauth = new OAuth(Provider.GetRequiredService<IJobMonitor>(), Provider.GetRequiredService<ILogger<OAuth>>(), new HttpClient(new TokenHandler()), id, os);
        using var services = new ServiceCollection().AddLogging().AddSingleton<IIpcProtocolHandler>(new Handler(oauth)).BuildServiceProvider();
        var sync = Provider.GetRequiredService<SyncFile>();
        var configurator = new CommandLineConfigurator(services, [], []);
        using var server = new CliServer(services.GetRequiredService<ILogger<CliServer>>(), configurator, Provider.GetRequiredService<ISettingsManager>(), sync, services);
        await server.StartCliServerAsync();
        try
        {
            var waiting = oauth.AuthorizeRequest(default);
            var client = new CliClient(services.GetRequiredService<ILogger<CliClient>>(), sync, services);
            await client.ExecuteCommand(["nxm://oauth/callback?state=ipc-state&code=fixture-code"]);
            Assert.Equal("fixture-access", (await waiting)!.AccessToken);
        }
        finally { await server.StopAsync(default); }
    }

    private sealed class Handler(OAuth oauth) : IIpcProtocolHandler
    {
        public string Protocol => "nxm";
        public Task Handle(string url, CancellationToken token) { oauth.AddUrl(NXMUrl.Parse(url).OAuth); return Task.CompletedTask; }
    }
    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new JwtTokenReply
            { AccessToken="fixture-access",RefreshToken="fixture-refresh",Type="Bearer",Scope="public",CreatedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),ExpiresIn=3600 })) });
    }
}
