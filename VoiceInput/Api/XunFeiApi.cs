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
using Serilog;

namespace VoiceInput.Api;

public class XunfeiApi : IDisposable
{
    // ✅ [改] 将所有魔法字符串常量提取为 const，集中管理，便于修改
    private const string WssHost = "iat-api.xfyun.cn";
    private const string WssPath = "/v2/iat";
    private const string WssUrl = $"wss://{WssHost}{WssPath}";

    // ✅ [改] business 参数中的配置常量化
    private const string AudioFormat = "audio/L16;rate=16000";
    private const string AudioEncoding = "raw";
    private const string Language = "zh_cn";
    private const string Domain = "iat";
    private const string Accent = "mandarin";

    // ✅ [改] 根据文档说明显式注释每个参数的含义和取值范围
    // ptt：标点符号，0=无标点，1=有标点（默认1）
    private const int Ptt = 1;

    // vad_eos：后端点静音检测时长（ms），范围[0,10000]，默认2000
    // 语音输入场景适当调大，避免说话停顿时过早截断
    private const int VadEos = 5000;

    // dwa：动态修正，wpgs=开启（仅中文支持），可实时修正识别结果
    private const string Dwa = "wpgs";

    // nunum：是否将数字转为阿拉伯数字，0=不转换，1=转换（默认1）
    private const int Nunum = 1;

    private readonly string _appId;
    private readonly string _apiSecret;
    private readonly string _apiKey;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isFirstFrame = true;
    private TaskCompletionSource<bool>? _finalResultTcs;

    private readonly Dictionary<int, string> _sentenceMap = new();
    public event Action<string>? OnTextChanged;

    private bool _disposed;

    public XunfeiApi(string appId, string apiSecret, string apiKey)
    {
        _appId = appId;
        _apiSecret = apiSecret;
        _apiKey = apiKey;
    }

    public async Task ConnectAsync()
    {
        await CloseAsync();

        _isFirstFrame = true;
        _sentenceMap.Clear();
        _finalResultTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
                common = new
                {
                    app_id = _appId
                },
                business = new
                {
                    language = Language,
                    domain = Domain,
                    accent = Accent,
                    ptt = Ptt,
                    vad_eos = VadEos,
                    dwa = Dwa,
                    nunum = Nunum
                },
                data = new
                {
                    status,
                    format = AudioFormat,
                    encoding = AudioEncoding,
                    audio = base64Audio
                }
            };
            _isFirstFrame = false;
        }
        else
        {
            requestObj = new
            {
                data = new
                {
                    status,
                    format = AudioFormat,
                    encoding = AudioEncoding,
                    audio = base64Audio
                }
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
            data = new
            {
                status = 2,
                format = AudioFormat,
                encoding = AudioEncoding,
                audio = ""
            }
        };

        var json = JsonSerializer.Serialize(requestObj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
        if (_finalResultTcs != null)
        {
            await Task.WhenAny(_finalResultTcs.Task, Task.Delay(2000));
        }

        await CloseAsync();
    }

    public async Task CloseAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        var webSocket = Interlocked.Exchange(ref _webSocket, null);
        if (webSocket != null)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Normal Closure",
                        CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // CTS 取消后 CloseAsync 收到取消信号，属于正常流程
                }
                catch (ObjectDisposedException)
                {
                    // CTS 取消后 WebSocket 已被释放，属于正常流程
                }
                catch (Exception ex)
                {
                    // 真正意外的异常才记录
                    Log.Warning(ex, "WebSocket 关闭时发生异常");
                }
            }

            webSocket.Dispose();
        }

        _cts?.Dispose();
        _cts = null;
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
                ParseResult(jsonResponse);
            }
        }
        catch (OperationCanceledException)
        {
            // 主动取消属于正常流程，单独捕获，不打错误日志
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebSocket 接收循环异常退出");
        }
    }

    private void ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "unknown";
                Log.Warning("讯飞 API 返回错误码 {Code}: {Message}", codeEl.GetInt32(), msg);
                return;
            }

            if (!root.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("result", out var resultEl) ||
                resultEl.ValueKind == JsonValueKind.Null
               ) return;

            if (dataEl.TryGetProperty("status", out var statusEl) && statusEl.GetInt32() == 2)
            {
                _finalResultTcs?.TrySetResult(true);
            }
            
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
            if (resultEl.TryGetProperty("ws", out var wsEl))
            {
                var sb = new StringBuilder();
                foreach (var wsItem in wsEl.EnumerateArray())
                {
                    if (wsItem.TryGetProperty("cw", out var cwEl))
                    {
                        foreach (var cwItem in cwEl.EnumerateArray())
                        {
                            if (cwItem.TryGetProperty("w", out var wEl))
                            {
                                sb.Append(wEl.GetString());
                            }
                        }
                    }
                }

                var textSegment = sb.ToString();
                if (_sentenceMap.TryGetValue(sn, out var existing))
                    _sentenceMap[sn] = existing + textSegment;
                else
                    _sentenceMap[sn] = textSegment;
            }

            var fullTextBuilder = new StringBuilder();
            foreach (var kv in _sentenceMap.OrderBy(x => x.Key))
            {
                fullTextBuilder.Append(kv.Value);
            }

            var currentFullText = fullTextBuilder.ToString();
            if (!string.IsNullOrWhiteSpace(currentFullText)) OnTextChanged?.Invoke(currentFullText);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "JSON解析异常");
        }
    }

    private string GetAuthUrl()
    {
        var date = DateTime.UtcNow.ToString("r");
        var signatureOrigin = $"host: {WssHost}\ndate: {date}\nGET {WssPath} HTTP/1.1";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin));
        var signature = Convert.ToBase64String(signatureBytes);

        var authString =
            $"api_key=\"{_apiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"{signature}\"";
        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authString));

        return
            $"wss://iat-api.xfyun.cn/v2/iat?authorization={Uri.EscapeDataString(authorization)}&date={Uri.EscapeDataString(date)}&host=iat-api.xfyun.cn";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = CloseAsync();
        GC.SuppressFinalize(this);
    }
}