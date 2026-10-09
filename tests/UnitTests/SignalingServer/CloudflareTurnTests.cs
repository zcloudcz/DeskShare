using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using DeskShare.SignalingServer.Services;

namespace DeskShare.UnitTests.SignalingServer;

public class CloudflareTurnTests
{
    private const string SuccessBody =
        "{\"iceServers\":{\"urls\":[\"stun:stun.cloudflare.com:3478\",\"turn:turn.cloudflare.com:3478?transport=udp\"]," +
        "\"username\":\"user-1\",\"credential\":\"cred-1\"}}";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public Func<HttpResponseMessage> Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody)
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return Respond();
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IConfiguration Config(bool cloudflare = true, int ttl = 86400)
    {
        var values = new Dictionary<string, string?>
        {
            ["IceServers:0:Urls"] = "stun:stun.l.google.com:19302",
            ["IceServers:1:Urls"] = "turn:own.example:3478",
            ["IceServers:1:Username"] = "u",
            ["IceServers:1:Credential"] = "p"
        };
        if (cloudflare)
        {
            values["Turn:Cloudflare:KeyId"] = "key-123";
            values["Turn:Cloudflare:ApiToken"] = "token-abc";
            values["Turn:Cloudflare:BaseUrl"] = "https://cf.test";
            values["Turn:Cloudflare:TtlSeconds"] = ttl.ToString();
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static CloudflareTurnCredentialService CreateService(FakeHandler handler, FakeTime time, int ttl = 86400)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(CloudflareTurnCredentialService.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new CloudflareTurnCredentialService(factory, Config(ttl: ttl), time);
    }

    [Fact]
    public async Task GetCredentials_ParsesResponse_AndSendsBearerAndTtl()
    {
        var handler = new FakeHandler();
        var service = CreateService(handler, new FakeTime());

        var server = await service.GetCredentialsAsync(CancellationToken.None);

        Assert.Equal(new[] { "stun:stun.cloudflare.com:3478", "turn:turn.cloudflare.com:3478?transport=udp" }, server.Urls);
        Assert.Equal("user-1", server.Username);
        Assert.Equal("cred-1", server.Credential);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://cf.test/v1/turn/keys/key-123/credentials/generate", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("token-abc", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal(86400, JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task GetCredentials_ReusesCacheWhileMoreThanHalfTtlRemains()
    {
        var handler = new FakeHandler();
        var time = new FakeTime();
        var service = CreateService(handler, time, ttl: 1000);

        await service.GetCredentialsAsync(CancellationToken.None);
        time.Now = time.Now.AddSeconds(499); // just under half of the TTL
        await service.GetCredentialsAsync(CancellationToken.None);
        Assert.Equal(1, handler.Requests);

        time.Now = time.Now.AddSeconds(2); // past half of the TTL -> refresh
        await service.GetCredentialsAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Builder_ReturnsStaticPlusCloudflare_OnSuccess()
    {
        var handler = new FakeHandler();
        var provider = new IceServerProvider(Config(), NullLogger<IceServerProvider>.Instance, CreateService(handler, new FakeTime()));

        var servers = await provider.GetIceServersAsync(CancellationToken.None);

        Assert.Equal(3, servers.Count);
        Assert.Equal(new[] { "stun:stun.l.google.com:19302" }, servers[0].Urls);
        Assert.Null(servers[0].Username); // STUN entry has no credentials
        Assert.Equal("u", servers[1].Username);
        Assert.Equal("user-1", servers[2].Username);
    }

    [Fact]
    public async Task Builder_ReturnsStaticOnly_WhenCloudflareNotConfigured()
    {
        var provider = new IceServerProvider(Config(cloudflare: false), NullLogger<IceServerProvider>.Instance);

        var servers = await provider.GetIceServersAsync(CancellationToken.None);

        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public async Task Builder_ReturnsStaticOnly_OnNon2xx()
    {
        var handler = new FakeHandler { Respond = () => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var provider = new IceServerProvider(Config(), NullLogger<IceServerProvider>.Instance, CreateService(handler, new FakeTime()));

        var servers = await provider.GetIceServersAsync(CancellationToken.None);

        Assert.Equal(2, servers.Count);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Builder_ReturnsStaticOnly_OnNetworkError()
    {
        var handler = new FakeHandler { Respond = () => throw new HttpRequestException("boom") };
        var provider = new IceServerProvider(Config(), NullLogger<IceServerProvider>.Instance, CreateService(handler, new FakeTime()));

        var servers = await provider.GetIceServersAsync(CancellationToken.None);

        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public async Task Builder_ReturnsStaticOnly_OnMalformedResponse()
    {
        var handler = new FakeHandler
        {
            Respond = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"iceServers\":{}}") }
        };
        var provider = new IceServerProvider(Config(), NullLogger<IceServerProvider>.Instance, CreateService(handler, new FakeTime()));

        var servers = await provider.GetIceServersAsync(CancellationToken.None);

        Assert.Equal(2, servers.Count);
    }
}
