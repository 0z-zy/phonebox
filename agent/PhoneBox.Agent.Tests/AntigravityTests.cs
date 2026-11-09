using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class AntigravityTests
{
    [Theory]
    [InlineData("agy.exe --hub --hub-port=13897 --csrf_token=token-12345", 13897, "token-12345")]
    [InlineData("language_server --port 8080 --csrfToken abc-xyz", 8080, "abc-xyz")]
    [InlineData("Antigravity --hub_port=9090 --csrf_token \"my-quoted-token\"", 9090, "my-quoted-token")]
    public void ParseEndpointFromCommandLine_ExtractsPortAndToken(string cmd, int expectedPort, string expectedCsrf)
    {
        var ep = QuotaMonitor.ParseEndpointFromCommandLine(cmd);
        Assert.NotNull(ep);
        Assert.Equal(expectedPort, ep.Value.Port);
        Assert.Equal(expectedCsrf, ep.Value.CsrfToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("agy.exe --hub --hub-port=13897")]
    [InlineData("language_server --csrf_token=my-token")]
    [InlineData("notepad.exe somefile.txt")]
    public void ParseEndpointFromCommandLine_Invalid_ReturnsNull(string cmd)
    {
        var ep = QuotaMonitor.ParseEndpointFromCommandLine(cmd);
        Assert.Null(ep);
    }

    [Fact]
    public void ParseAntigravityLimits_MultiGroup_OrdersSessionBeforeWeekly()
    {
        var json = """
        {
            "response": {
                "groups": [
                    {
                        "displayName": "Gemini Models",
                        "buckets": [
                            {
                                "bucketId": "gemini-weekly",
                                "window": "weekly",
                                "remainingFraction": 0.95,
                                "resetTime": "2026-11-15T07:30:12Z"
                            },
                            {
                                "bucketId": "gemini-5h",
                                "window": "5h",
                                "remainingFraction": 0.90,
                                "resetTime": "2026-11-09T22:15:55Z"
                            }
                        ]
                    },
                    {
                        "displayName": "Claude and GPT models",
                        "buckets": [
                            {
                                "bucketId": "3p-weekly",
                                "window": "weekly",
                                "remainingFraction": 0.15,
                                "resetTime": "2026-11-16T17:46:14Z"
                            },
                            {
                                "bucketId": "3p-5h",
                                "window": "5h",
                                "remainingFraction": 1.0,
                                "resetTime": "2026-11-09T22:46:14Z"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);

        Assert.Equal(4, limits.Count);

        // 1. Gemini 5-hour
        Assert.Equal("5-hour (Gemini)", limits[0].Label);
        Assert.Equal(10.0, limits[0].Percent);
        Assert.Equal(18000L, limits[0].WindowSec);
        Assert.Equal("normal", limits[0].Severity);

        // 2. Gemini Weekly
        Assert.Equal("Weekly (Gemini)", limits[1].Label);
        Assert.Equal(5.0, limits[1].Percent);
        Assert.Equal(604800L, limits[1].WindowSec);
        Assert.Equal("normal", limits[1].Severity);

        // 3. Claude/GPT 5-hour
        Assert.Equal("5-hour (Claude/GPT)", limits[2].Label);
        Assert.Equal(0.0, limits[2].Percent);
        Assert.Equal(18000L, limits[2].WindowSec);
        Assert.Equal("normal", limits[2].Severity);

        // 4. Claude/GPT Weekly (85% used -> warning)
        Assert.Equal("Weekly (Claude/GPT)", limits[3].Label);
        Assert.Equal(85.0, limits[3].Percent);
        Assert.Equal(604800L, limits[3].WindowSec);
        Assert.Equal("warning", limits[3].Severity);
    }

    [Fact]
    public void ParseAntigravityLimits_SingleGroup_UsesSimpleLabels()
    {
        var json = """
        {
            "response": {
                "groups": [
                    {
                        "displayName": "Gemini Models",
                        "buckets": [
                            {
                                "bucketId": "gemini-5h",
                                "window": "5h",
                                "remainingFraction": 0.02,
                                "resetTime": "2026-11-09T22:15:55Z"
                            },
                            {
                                "bucketId": "gemini-weekly",
                                "window": "weekly",
                                "remainingFraction": 0.80,
                                "resetTime": "2026-11-15T07:30:12Z"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);

        Assert.Equal(2, limits.Count);
        Assert.Equal("5-hour session", limits[0].Label);
        Assert.Equal(98.0, limits[0].Percent);
        Assert.Equal("critical", limits[0].Severity);

        Assert.Equal("Weekly", limits[1].Label);
        Assert.Equal(20.0, limits[1].Percent);
        Assert.Equal("normal", limits[1].Severity);
    }

    [Fact]
    public void ParseAntigravityLimits_MalformedOrEmpty_ReturnsEmptyList()
    {
        using var doc = JsonDocument.Parse("{}");
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);
        Assert.Empty(limits);
    }

    [Fact]
    public void ParseAntigravityPlan_ExtractsPlanName_FallsBackToUserTier()
    {
        var jsonWithPlanName = """
        {
            "userStatus": {
                "planStatus": {
                    "planInfo": {
                        "planName": "Pro"
                    }
                },
                "userTier": {
                    "name": "Google AI Pro"
                }
            }
        }
        """;

        using var doc1 = JsonDocument.Parse(jsonWithPlanName);
        Assert.Equal("Pro", QuotaMonitor.ParseAntigravityPlan(doc1.RootElement));

        var jsonWithTierOnly = """
        {
            "userStatus": {
                "userTier": {
                    "name": "Google AI Ultra"
                }
            }
        }
        """;

        using var doc2 = JsonDocument.Parse(jsonWithTierOnly);
        Assert.Equal("Google AI Ultra", QuotaMonitor.ParseAntigravityPlan(doc2.RootElement));
    }
}
