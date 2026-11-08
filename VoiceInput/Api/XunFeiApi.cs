using System;
using System.Buffers;
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
using VoiceInput.Utils;

namespace VoiceInput.Api;

public class XunfeiApi(string? appId, string? apiSecret, string? apiKey) : IDisposable
{
    private const string WssHost = "iat-api.xfyun.cn";
    private const string WssPath = "/v2/iat";

    private const string AudioFormat = "audio/L16;rate=16000";
    private const string AudioEncoding = "raw";
    private const string Language = "zh_cn";
    private const string Domain = "iat";
    private const string Accent = "mandarin";

    // ptt：标点符号，0=无标点，1=有标点（默认1）
    private const int Ptt = 1;

    // vad_eos：后端点静音检测时长（ms），范围[0,10000]，默认2000
    // 语音输入场景适当调大，避免说话停顿时过早截断
    private const int VadEos = 5000;

    // dwa：动态修正，wpgs=开启（仅中文支持），可实时修正识别结果
    private const string Dwa = "wpgs";

    // nunum：是否将数字转为阿拉伯数字，0=不转换，1=转换（默认1）
    private const int Nunum = 1;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly string _appId = appId ?? string.Empty;
    private readonly string _apiSecret = apiSecret ?? string.Empty;
    private readonly string _apiKey = apiKey ?? string.Empty;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Session? _session;

    public event Action<string>? OnTextChanged;

    private bool _disposed;

    public async Task ConnectAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XunfeiApi));

        if (string.IsNullOrWhiteSpace(_appId) ||
            string.IsNullOrWhiteSpace(_apiSecret) ||
            string.IsNullOrWhiteSpace(_apiKey))
        {
            Log.Error("讯飞 API 配置缺失，请先编辑 {ConfigPath}，填写 AppId、ApiSecret、ApiKey",
                AppPaths.ConfigFilePath);
            throw new InvalidOperationException("讯飞 API 配置缺失，无法连接");
        }

        await CloseAsync();

        var session = new Session();
        Volatile.Write(ref _session, session);

        try
        {
            var authUrl = GetAuthUrl();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
            connectCts.CancelAfter(ConnectTimeout);
            await session.WebSocket.ConnectAsync(new Uri(authUrl), connectCts.Token);
            _ = ReceiveLoopAsync(session);
        }
        catch
        {
            await CloseSessionAsync(session);
            throw;
        }
    }

    public async Task SendAudioDataAsync(byte[] audioData, int length)
    {
        var session = Volatile.Read(ref _session);
        if (session is null) return;

        try
        {
            await _sendGate.WaitAsync(session.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _session), session)) return;
            if (session.WebSocket.State != WebSocketState.Open) return;

            var isFirstFrame = session.IsFirstFrame;
            var status = isFirstFrame ? 0 : 1;

            var buffer = new ArrayBufferWriter<byte>(length * 2 + 512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                if (isFirstFrame)
                {
                    writer.WriteStartObject("common");
                    writer.WriteString("app_id", _appId);
                    writer.WriteEndObject();

                    writer.WriteStartObject("business");
                    writer.WriteString("language", Language);
                    writer.WriteString("domain", Domain);
                    writer.WriteString("accent", Accent);
                    writer.WriteNumber("ptt", Ptt);
                    writer.WriteNumber("vad_eos", VadEos);
                    writer.WriteString("dwa", Dwa);
                    writer.WriteNumber("nunum", Nunum);
                    writer.WriteEndObject();
                }

                writer.WriteStartObject("data");
                writer.WriteNumber("status", status);
                writer.WriteString("format", AudioFormat);
                writer.WriteString("encoding", AudioEncoding);
                writer.WriteBase64String("audio", audioData.AsSpan(0, length));
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            await session.WebSocket.SendAsync(
                buffer.WrittenMemory,
                WebSocketMessageType.Text,
                true,
                session.Cts.Token);

            if (isFirstFrame) session.IsFirstFrame = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (WebSocketException ex)
        {
            Log.Warning(ex, "发送音频失败");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAndSendLastFrameAsync()
    {
        var session = Volatile.Read(ref _session);
        if (session is null) return;

        try
        {
            try
            {
                await _sendGate.WaitAsync(session.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (!ReferenceEquals(Volatile.Read(ref _session), session)) return;
                if (session.WebSocket.State != WebSocketState.Open) return;

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
                await session.WebSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    session.Cts.Token);
            }
            finally
            {
                _sendGate.Release();
            }

            await Task.WhenAny(session.FinalResult.Task, Task.Delay(2000));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (WebSocketException ex)
        {
            Log.Warning(ex, "发送最终帧失败");
        }
        finally
        {
            await CloseSessionAsync(session);
        }
    }

    public async Task CloseAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;

        await session.CloseAsync();
    }

    private async Task CloseSessionAsync(Session session)
    {
        Interlocked.CompareExchange(ref _session, null, session);
        await session.CloseAsync();
    }

    private async Task ReceiveLoopAsync(Session session)
    {
        var webSocket = session.WebSocket;
        var cts = session.Cts;

        var buffer = new byte[4096];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage && webSocket.State == WebSocketState.Open);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var jsonResponse = Encoding.UTF8.GetString(ms.ToArray());
                ParseResult(jsonResponse, session);
            }
        }
        catch (OperationCanceledException)
        {
            // 主动取消属于正常流程，单独捕获，不打错误日志
        }
        catch (ObjectDisposedException)
        {
            // CloseAsync 里已释放 WebSocket，属于正常关闭流程
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebSocket 接收循环异常退出");
        }
        finally
        {
            await CloseSessionAsync(session);
        }
    }

    private void ParseResult(string json, Session session)
    {
        if (!ReferenceEquals(Volatile.Read(ref _session), session)) return;

        var isFinal = false;
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

            if (!root.TryGetProperty("data", out var dataEl)) return;

            isFinal = dataEl.TryGetProperty("status", out var statusEl) && statusEl.GetInt32() == 2;

            if (!dataEl.TryGetProperty("result", out var resultEl) ||
                resultEl.ValueKind == JsonValueKind.Null
               ) return;

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
                    session.SentenceMap.Remove(i);
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
                if (session.SentenceMap.TryGetValue(sn, out var existing))
                    session.SentenceMap[sn] = existing + textSegment;
                else
                    session.SentenceMap[sn] = textSegment;
            }

            var fullTextBuilder = new StringBuilder();
            foreach (var kv in session.SentenceMap.OrderBy(x => x.Key))
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
        finally
        {
            if (isFinal) session.FinalResult.TrySetResult(true);
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

    private sealed class Session : IDisposable
    {
        private int _closed;

        public ClientWebSocket WebSocket { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public bool IsFirstFrame { get; set; } = true;
        public TaskCompletionSource<bool> FinalResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<int, string> SentenceMap { get; } = new();

        public async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            try
            {
                try
                {
                    await Cts.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                }

                if (WebSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(CloseTimeout);
                        await WebSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Normal Closure",
                            closeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "WebSocket 关闭时发生异常");
                    }
                }
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            Cts.Dispose();
            WebSocket.Dispose();
        }
    }
}
