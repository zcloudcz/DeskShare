using DeskShare.Core.Auth;
using DeskShare.Core.WebRTC;

namespace DeskShare.UnitTests.WebRTC;

public class IceServerMapperTests
{
    [Fact]
    public void EachSupportedUrl_BecomesItsOwnEntry_WithCredentials()
    {
        var servers = new[]
        {
            new IceServerInfo
            {
                Urls = new[]
                {
                    "stun:stun.cloudflare.com:3478",
                    "turn:turn.cloudflare.com:3478?transport=udp",
                    "turn:turn.cloudflare.com:3478?transport=tcp",
                    "turns:turn.cloudflare.com:5349?transport=tcp"
                },
                Username = "user",
                Credential = "pass"
            }
        };

        var result = IceServerMapper.ToRtcIceServers(servers);

        Assert.Equal(4, result.Count);
        Assert.All(result, s => Assert.DoesNotContain(",", s.urls));
        Assert.All(result, s => Assert.Equal("user", s.username));
        Assert.Contains(result, s => s.urls == "turns:turn.cloudflare.com:5349?transport=tcp");
    }

    [Fact]
    public void CommaSeparatedUrls_AreSplit()
    {
        var result = IceServerMapper.ToRtcIceServers(new[]
        {
            new IceServerInfo { Urls = new[] { "stun:a.example:3478, stun:b.example:3478" } }
        });

        Assert.Equal(new[] { "stun:a.example:3478", "stun:b.example:3478" }, result.Select(s => s.urls));
        Assert.All(result, s => Assert.Null(s.username));
    }

    [Fact]
    public void UnsupportedUrls_AreDropped()
    {
        var result = IceServerMapper.ToRtcIceServers(new[]
        {
            new IceServerInfo { Urls = new[] { "http://not-an-ice-server", "", "stun:ok.example:3478" } }
        });

        Assert.Equal("stun:ok.example:3478", Assert.Single(result).urls);
    }

    [Fact]
    public void NullInput_GivesEmptyList()
    {
        Assert.Empty(IceServerMapper.ToRtcIceServers(null));
    }
}
