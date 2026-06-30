using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceInput.Api;

public class XunfeiApi
{
    private readonly string _appId;
    private readonly string _apiSecret;
    private readonly string _apiKey;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isFirstFrame = true;
    private Dictionary<int, string> _sentenceMap = new();
    public Action<string>? onTextChanged;

    public XunfeiApi(string appId, string apiSecret, string apiKey)
    {
        _appId = appId;
        _apiSecret = apiSecret;
        _apiKey = apiKey;
    }

    public async Task ConnectAsync()
    {
        _isFirstFrame = true;
        _sentenceMap.Clear();
        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();

        var authUrl = GetAuthUrl();
        await _webSocket.ConnectAsync(new Uri(authUrl), _cts.Token);

        _ = ReceiveLoopAsync();
    }

    public async Task SendAudioDataAsync(byte[] audioData, int length)
    {
        if (_webSocket is not { State: WebSocketState.Open }) return;

        var base64Audio = Convert.ToBase64String(audioData, 0, length);
        var status = _isFirstFrame ? 0 : 1;

        object requestObj;
        if (_isFirstFrame)
        {
            requestObj = new
            {
                common = new { app_id = _appId },
                business = new
                {
                    language = "zh_cn",
                    domain = "iat",
                    accent = "mandarin",
                    ptt = 1,
                    vad_eos = 2000,
                    dwa = "wpgs"
                },
                data = new
                {
                    status,
                    format = "audio/L16;rate=16000",
                    encoding = "raw",
                    audio = base64Audio
                }
            };
            _isFirstFrame = false;
        }
        else
        {
            requestObj = new
            {
                data = new { status, format = "audio/L16;rate=16000", encoding = "raw", audio = base64Audio }
            };
        }

        var json = JsonSerializer.Serialize(requestObj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
    }

    public async Task StopAndSendLastFrameAsync()
    {
        if (_webSocket is not { State: WebSocketState.Open }) return;

        var requestObj = new
        {
            data = new { status = 2, format = "audio/L16;rate=16000", encoding = "raw", audio = "" }
        };

        var json = JsonSerializer.Serialize(requestObj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
        await Task.Delay(180);
        await CloseAsync();
    }

    public async Task CloseAsync()
    {
        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Normal Closure",
                    CancellationToken.None
                );
            }

            _webSocket.Dispose();
            _webSocket = null;
        }

        _cts?.Cancel();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !_cts!.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage && _webSocket.State == WebSocketState.Open);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var jsonResponse = Encoding.UTF8.GetString(ms.ToArray());

                // 解析这段 JSON
                ParseResult(jsonResponse);
            }
        }
        catch (Exception)
        {
            /* 忽略断开异常 */
        }
    }

    // 解析讯飞返回的 JSON 数据 (支持 wpgs 动态修正)
    private void ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
                return;

            if (root.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("result", out var resultEl) &&
                resultEl.ValueKind != JsonValueKind.Null)
            {
                var sn = resultEl.TryGetProperty("sn", out var snEl) ? snEl.GetInt32() : 1;

                // 取是追加还是替换 (pgs: apd=追加, rpl=替换)
                var pgs = resultEl.TryGetProperty("pgs", out var pgsEl) ? (pgsEl.GetString() ?? "apd") : "apd";

                // 如果是替换操作，根据 rg 参数删除旧的错字
                if (pgs == "rpl" && resultEl.TryGetProperty("rg", out var rgEl) && rgEl.GetArrayLength() >= 2)
                {
                    var startSn = rgEl[0].GetInt32();
                    var endSn = rgEl[1].GetInt32();
                    // 把指定范围内的旧句子全删了
                    for (var i = startSn; i <= endSn; i++)
                    {
                        _sentenceMap.Remove(i);
                    }
                }

                // 提取当前包的文字
                var textSegment = "";
                if (resultEl.TryGetProperty("ws", out var wsEl))
                {
                    foreach (var wsItem in wsEl.EnumerateArray())
                    {
                        if (wsItem.TryGetProperty("cw", out var cwEl))
                        {
                            foreach (var cwItem in cwEl.EnumerateArray())
                            {
                                if (cwItem.TryGetProperty("w", out var wEl))
                                {
                                    textSegment += wEl.GetString();
                                }
                            }
                        }
                    }
                }

                if (_sentenceMap.ContainsKey(sn))
                {
                    _sentenceMap[sn] += textSegment; // 同一个序号的包可能分多次发来，所以要累加
                }
                else
                {
                    _sentenceMap[sn] = textSegment; // 新序号直接存入
                }

                // 将字典里所有的句子按序号拼接成一句完整的话
                var sortedTexts = _sentenceMap.OrderBy(x => x.Key).Select(x => x.Value);
                var currentFullText = string.Join("", sortedTexts);

                if (!string.IsNullOrWhiteSpace(currentFullText))
                {
                    onTextChanged?.Invoke(currentFullText);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "JSON解析异常");
        }
    }

    private string GetAuthUrl()
    {
        string date = DateTime.UtcNow.ToString("r");
        string signatureOrigin = $"host: iat-api.xfyun.cn\ndate: {date}\nGET /v2/iat HTTP/1.1";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        byte[] signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin));
        string signature = Convert.ToBase64String(signatureBytes);

        string authString =
            $"api_key=\"{_apiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"{signature}\"";
        string authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authString));

        return
            $"wss://iat-api.xfyun.cn/v2/iat?authorization={Uri.EscapeDataString(authorization)}&date={Uri.EscapeDataString(date)}&host=iat-api.xfyun.cn";
    }
}