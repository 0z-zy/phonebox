using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The OBS client against a fake obs-websocket 5 server: handshake with a password, state, buttons.</summary>
public sealed class ObsTests : IDisposable
{
    private const string Password = "supersecret";
    private const string Salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    private const string Challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

    private readonly HttpListener _server = new();
    private readonly string _ws;
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-obs-{Guid.NewGuid():N}.json");
    private readonly List<string> _requests = [];
    private bool _recording;
    private string _scene = "Desk";

    public ObsTests()
    {
        var port = TestPorts.Free();
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ws = $"ws://127.0.0.1:{port}/";
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Session(ctx));
        }
    }

    private async Task Session(HttpListenerContext ctx)
    {
        var wsc = await ctx.AcceptWebSocketAsync("obswebsocket.json");
        var ws = wsc.WebSocket;
        async Task Send(object o) => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, default);
        async Task<JsonNode?> Receive()
        {
            var buf = new byte[65536];
            var r = await ws.ReceiveAsync(buf, default);
            return r.MessageType == WebSocketMessageType.Close ? null : JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
        }
        try
        {
            await Send(new { op = 0, d = new { obsWebSocketVersion = "5.5.0", rpcVersion = 1, authentication = new { challenge = Challenge, salt = Salt } } });
            var identify = await Receive();
            if (identify?["d"]?["authentication"]?.GetValue<string>() != ObsMonitor.AuthAnswer(Password, Salt, Challenge))
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed.", default);
                return;
            }
            await Send(new { op = 2, d = new { negotiatedRpcVersion = 1 } });
            while (ws.State == WebSocketState.Open)
            {
                var m = await Receive();
                if (m is null) break;
                var d = m["d"]!;
                var type = d["requestType"]!.GetValue<string>();
                lock (_requests) _requests.Add(type + (d["requestData"]?["sceneName"] is { } sn ? ":" + sn.GetValue<string>() : ""));
                object data = type switch
                {
                    "GetStreamStatus" => new { outputActive = false, outputDuration = 0 },
                    "GetRecordStatus" => new { outputActive = _recording, outputPaused = false, outputDuration = _recording ? 61_000 : 0 },
                    "GetSceneList" => new { currentProgramSceneName = _scene, scenes = new[] { new { sceneName = "BRB" }, new { sceneName = "Desk" }, new { sceneName = "Camera" } } },
                    "GetSpecialInputs" => new { mic1 = "Mic/Aux" },
                    "GetInputMute" => new { inputMuted = true },
                    _ => new { },
                };
                if (type == "ToggleRecord") _recording = !_recording;
                if (type == "SetCurrentProgramScene") _scene = d["requestData"]!["sceneName"]!.GetValue<string>();
                await Send(new { op = 7, d = new { requestType = type, requestId = d["requestId"]!.GetValue<string>(), requestStatus = new { result = true, code = 100 }, responseData = data } });
            }
        }
        catch { }
    }

    private ObsMonitor Monitor(string password, bool control = true)
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            obs = new { enabled = true, url = _ws, password },
            pages = new[] { new { id = "p", title = "P", widgets = new object[] { new { id = "o", type = "obs", x = 0, y = 0, w = 4, h = 3, options = new { control } } } } },
        }));
        return new ObsMonitor(new ConfigStore(_configPath), NullLogger<ObsMonitor>.Instance);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task ConnectsReadsStateAndRunsButtons()
    {
        var obs = Monitor(Password);
        using var cts = new CancellationTokenSource();
        await obs.StartAsync(cts.Token);
        await Until(() => obs.Snapshot().Connected);
        var s = obs.Snapshot();
        Assert.Equal("Desk", s.Scene);
        Assert.Equal(["Camera", "Desk", "BRB"], s.Scenes); // OBS lists scenes bottom-up
        Assert.True(s.MicMuted);
        Assert.False(obs.Live);

        Assert.Null(await obs.ActAsync("record", null));
        Assert.Null(await obs.ActAsync("scene", "Camera"));
        Assert.NotNull(await obs.ActAsync("scene", "No such scene"));
        await Until(() => obs.Snapshot().Recording && obs.Snapshot().Scene == "Camera");
        Assert.True(obs.Live);
        Assert.Contains("SetCurrentProgramScene:Camera", _requests);
        await obs.StopAsync(default);
    }

    [Fact]
    public async Task ButtonsNeedTheWidgetsPermission()
    {
        var obs = Monitor(Password, control: false);
        Assert.NotNull(await obs.ActAsync("record", null));
    }

    [Fact]
    public async Task TestReportsTheWrongPassword()
    {
        Assert.StartsWith("Connected", await ObsMonitor.TestAsync(_ws, Password));
        Assert.NotEqual("Connected", await ObsMonitor.TestAsync(_ws, "wrong"));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Close();
        try { File.Delete(_configPath); } catch { }
    }
}
