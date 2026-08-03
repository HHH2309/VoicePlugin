using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    /// <summary>
    /// Edge 在线语音引擎（手搓协议实现，无第三方依赖）。
    /// <para>
    /// 协议与开源实现 edge-tts / EdgeTTS.DotNet 一致：
    /// 经 WebSocket 连接微软 Edge 朗读服务，发送 speech.config 与 SSML 消息，
    /// 接收 MP3 音频帧写入输出流。语音列表走 REST 接口。
    /// 注意：非官方接口，可能随时间失效；失效时由 TtsProviderManager
    /// 按既有容错逻辑自动降级到本地引擎，不影响播报。
    /// </para>
    /// </summary>
    public sealed class EdgeOnlineTtsProvider : ITtsProvider, ITtsCacheableProvider
    {
        private const string DefaultVoice = "zh-CN-XiaoxiaoNeural";
        private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const string BaseUrl =
            "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
        private const string SecMsGecVersion = "1-143.0.3650.75";
        // 注意：服务端会校验浏览器指纹头，User-Agent 必须带 Edg/ 尾巴
        // （Edge 浏览器指纹），且需一并发送 Accept-Language / Pragma /
        // Cache-Control，否则 WebSocket 握手被 403 拒绝（实测验证）。
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
        private const string Origin =
            "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
        private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";
        private const int MaxSsmlBytesPerChunk = 4096;

        private static readonly string WssUriBase =
            $"wss://{BaseUrl}/edge/v1?TrustedClientToken={TrustedClientToken}";
        private static readonly string VoicesListUriBase =
            $"https://{BaseUrl}/voices/list?trustedclienttoken={TrustedClientToken}";

        private readonly IAudioPlaybackService _playback;
        private readonly TtsAudioCache _cache;
        private readonly object _gate = new object();
        private CancellationTokenSource _active;
        private bool _disposed;

        public EdgeOnlineTtsProvider(
            IAudioPlaybackService playback,
            TtsAudioCache cache = null,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _playback = playback ?? throw new ArgumentNullException(nameof(playback));
            _cache = cache;
        }

        public string CacheExtension => ".mp3";

        public TtsProviderKind Kind => TtsProviderKind.EdgeOnline;
        public string DisplayName => "Edge 在线语音（非官方）";
        public bool IsAvailable => !_disposed;

        public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var json = await http.GetStringAsync(
                VoicesListUriBase
                    + "&Sec-MS-GEC=" + GenerateSecMsGec()
                    + "&Sec-MS-GEC-Version=" + SecMsGecVersion,
                cancellationToken)
                .ConfigureAwait(false);

            var voices = JsonSerializer.Deserialize<List<EdgeVoiceInfo>>(json)
                ?? new List<EdgeVoiceInfo>();
            return voices
                .Where(voice => voice != null && !string.IsNullOrWhiteSpace(voice.ShortName))
                .OrderByDescending(voice =>
                    voice.Locale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true)
                .ThenBy(voice => voice.Locale)
                .ThenBy(voice => voice.ShortName)
                .Select(voice => new TtsVoiceInfo(
                    voice.ShortName,
                    $"{voice.ShortName} · {voice.Locale}",
                    Kind,
                    voice.Locale,
                    true))
                .ToArray();
        }

        public async Task SpeakAsync(
            string text,
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource operation;
            lock (_gate)
            {
                ThrowIfDisposed();
                CancelActiveLocked();
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _active = operation;
            }

            var deleteAfterPlay = false;
            string audioPath = null;
            try
            {
                if (_cache?.IsEnabled == true && !string.IsNullOrWhiteSpace(text))
                {
                    // 缓存路径：命中直接播缓存文件；未命中合成后写入缓存（提交失败时
                    // 返回缓存目录内的 .tmp 路径，播放后由下方清理）。
                    audioPath = await _cache.GetOrCreateAsync(
                        new TtsAudioCache.CacheKey(
                            Kind,
                            request.VoiceId,
                            request.Rate,
                            request.Volume,
                            text),
                        CacheExtension,
                        (path, token) => SynthesizeAsync(text, request, path, token),
                        operation.Token).ConfigureAwait(false);
                    deleteAfterPlay = audioPath.EndsWith(
                        TtsAudioCache.TempSuffix,
                        StringComparison.Ordinal);
                }
                else
                {
                    // 无缓存：合成到系统临时目录（原行为）。
                    audioPath = Path.Combine(
                        Path.GetTempPath(),
                        "VoicePlugin-" + Guid.NewGuid().ToString("N") + CacheExtension);
                    deleteAfterPlay = true;
                    await SynthesizeAsync(text, request, audioPath, operation.Token)
                        .ConfigureAwait(false);
                }

                operation.Token.ThrowIfCancellationRequested();
                await _playback.PlayFileAsync(audioPath, operation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, operation)) _active = null;
                }
                operation.Dispose();
                if (deleteAfterPlay)
                {
                    TryDelete(audioPath);
                }
            }
        }

        /// <summary>合成到指定文件（不播放、不清理），供缓存与预缓存复用。</summary>
        public async Task SynthesizeAsync(
            string text,
            TtsRequest request,
            string outputPath,
            CancellationToken cancellationToken)
        {
            var voice = request.Provider == Kind && !string.IsNullOrWhiteSpace(request.VoiceId)
                ? request.VoiceId
                : DefaultVoice;
            var rate = FormatPercentage(request.Rate * 5);
            var volume = FormatPercentage(request.Volume - 100);

            // 清理控制字符并做 XML 转义，再按 4096 字节分块
            // （每块独立建立一次 WebSocket 连接，与 edge-tts 行为一致）。
            var escaped = EscapeXml(RemoveIncompatibleCharacters(text));
            using var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (var chunk in SplitTextByByteLength(escaped, MaxSsmlBytesPerChunk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SynthesizeChunkAsync(
                    voice, rate, volume, chunk, output, cancellationToken)
                    .ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed) return;
                CancelActiveLocked();
                _playback.Stop();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CancelActiveLocked();
            }
        }

        /// <summary>一次 WebSocket 会话：发送配置与 SSML，接收音频写入输出流。</summary>
        private static async Task SynthesizeChunkAsync(
            string voice,
            string rate,
            string volume,
            string escapedText,
            Stream output,
            CancellationToken cancellationToken)
        {
            using var socket = new ClientWebSocket();
            // 头集合与 EdgeTTS.DotNet 0.4.0 对齐（浏览器指纹校验，缺一即 403）。
            socket.Options.SetRequestHeader("Pragma", "no-cache");
            socket.Options.SetRequestHeader("Cache-Control", "no-cache");
            socket.Options.SetRequestHeader("Origin", Origin);
            socket.Options.SetRequestHeader("User-Agent", UserAgent);
            socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
            socket.Options.SetRequestHeader("Cookie", $"muid={GenerateMuid()};");

            // Sec-MS-GEC：5 分钟窗口令牌（算法与 edge-tts 一致，double 运算保持浮点语义）。
            var wssUri = new Uri(WssUriBase + "&ConnectionId=" + Guid.NewGuid().ToString("N")
                + "&Sec-MS-GEC=" + GenerateSecMsGec()
                + "&Sec-MS-GEC-Version=" + SecMsGecVersion);
            await socket.ConnectAsync(wssUri, cancellationToken).ConfigureAwait(false);

            try
            {
                var timestamp = DateToString(DateTimeOffset.UtcNow);

                // 1) 配置消息。
                var configBody = "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{"
                    + "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}"
                    + ",\"outputFormat\":\"" + OutputFormat + "\"}}}}";
                var configMessage = Encoding.UTF8.GetBytes(
                    "X-Timestamp:" + timestamp + "\r\n"
                    + "Content-Type:application/json; charset=utf-8\r\n"
                    + "Path:speech.config\r\n\r\n"
                    + configBody);
                await socket.SendAsync(
                    new ArraySegment<byte>(configMessage),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken).ConfigureAwait(false);

                // 2) SSML 请求。
                var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' "
                    + "xml:lang='en-US'><voice name='" + voice + "'><prosody "
                    + "pitch='+0Hz' rate='" + rate + "' volume='" + volume + "'>"
                    + escapedText
                    + "</prosody></voice></speak>";
                // X-Timestamp 尾部的 Z 是微软 Edge 的既有行为，保持一致。
                var ssmlMessage = Encoding.UTF8.GetBytes(
                    "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\n"
                    + "Content-Type:application/ssml+xml\r\n"
                    + "X-Timestamp:" + timestamp + "Z\r\n"
                    + "Path:ssml\r\n\r\n"
                    + ssml);
                await socket.SendAsync(
                    new ArraySegment<byte>(ssmlMessage),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken).ConfigureAwait(false);

                // 3) 接收音频帧，直到 turn.end。
                var buffer = new byte[16384];
                var receivedAudio = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var message = await ReceiveFullMessageAsync(socket, buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (message.IsText)
                    {
                        var path = ParseTextFramePath(message.Bytes);
                        if (path == "turn.end")
                        {
                            break;
                        }
                        // audio.metadata / response / turn.start 等帧无音频数据，忽略。
                        continue;
                    }

                    // 二进制帧：前 2 字节为大端头长度，其后为 JSON 头与 MP3 数据。
                    var bytes = message.Bytes;
                    if (bytes.Length < 2) continue;
                    var headerLength = (bytes[0] << 8) | bytes[1];
                    if (bytes.Length <= 2 + headerLength) continue;
                    await output.WriteAsync(
                        bytes, 2 + headerLength, bytes.Length - 2 - headerLength,
                        cancellationToken).ConfigureAwait(false);
                    receivedAudio = true;
                }

                if (!receivedAudio)
                {
                    throw new InvalidOperationException(
                        "Edge online TTS returned no audio data.");
                }
            }
            finally
            {
                // 主动关闭（或取消时中止连接）。
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "finish",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    try
                    {
                        socket.Abort();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static async Task<(bool IsText, byte[] Bytes)> ReceiveFullMessageAsync(
            ClientWebSocket socket,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            using var collected = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                collected.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return (result.MessageType == WebSocketMessageType.Text, collected.ToArray());
        }

        private static string ParseTextFramePath(byte[] bytes)
        {
            // 帧结构：头部（\r\n 分隔的 "Key:Value"）以 \r\n\r\n 结束，其后为正文。
            var text = Encoding.UTF8.GetString(bytes);
            var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var header = separator < 0 ? text : text.Substring(0, separator);
            foreach (var line in header.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                if (string.Equals(
                    trimmed.Substring(0, colon).Trim(),
                    "Path",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(colon + 1).Trim();
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 按字节长度切分（≤4096 字节/块）：优先在换行、空格等安全边界切分，
        /// 不切开多字节 UTF-8 字符，也不切开 XML 实体（&amp; 等）。
        /// </summary>
        internal static IEnumerable<string> SplitTextByByteLength(
            string text,
            int maxBytes)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield return string.Empty;
                yield break;
            }

            var chunk = new StringBuilder();
            var lastHardBreak = -1;   // 最近一个换行后的安全断点（chunk 内索引）
            var lastSoftBreak = -1;   // 最近一个空格后的安全断点
            var chunkBytes = 0;

            foreach (var rune in text.EnumerateRunes())
            {
                var encoded = Encoding.UTF8.GetByteCount(rune.ToString());
                if (chunkBytes + encoded > maxBytes && chunk.Length > 0)
                {
                    // 回退到安全断点；退无可退时强制切分（至少保留一个字符）。
                    var cut = lastHardBreak >= 0
                        ? lastHardBreak
                        : lastSoftBreak >= 0 ? lastSoftBreak : chunk.Length;
                    cut = AdjustForXmlEntity(chunk.ToString(), cut);
                    if (cut <= 0) cut = chunk.Length;
                    var finished = chunk.ToString(0, cut);
                    chunk.Remove(0, cut);
                    chunkBytes = Encoding.UTF8.GetByteCount(chunk.ToString());
                    lastHardBreak = chunk.ToString().LastIndexOf('\n');
                    lastSoftBreak = chunk.ToString().LastIndexOf(' ');
                    yield return finished;
                }

                chunk.Append(rune.ToString());
                chunkBytes += encoded;
                if (rune.Value == '\n') lastHardBreak = chunk.Length - 1;
                if (rune.Value == ' ') lastSoftBreak = chunk.Length - 1;
            }

            if (chunk.Length > 0)
            {
                yield return chunk.ToString();
            }
        }

        /// <summary>若断点切开了未闭合的 XML 实体（&…;），回退到该实体之前。</summary>
        private static int AdjustForXmlEntity(string chunk, int cut)
        {
            var amp = chunk.LastIndexOf('&', cut - 1);
            if (amp < 0) return cut;
            if (chunk.IndexOf(';', amp) >= cut) return amp; // 实体被切断：回退到 & 前
            return cut;
        }

        private static string RemoveIncompatibleCharacters(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var builder = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                // 控制字符（0-8、11-12、14-31）替换为空格（与 edge-tts 一致）。
                builder.Append(ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r'
                    ? ' '
                    : ch);
            }
            return builder.ToString();
        }

        private static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        /// <summary>Sec-MS-GEC 令牌：Windows 文件时间（5 分钟取整）×10⁷ 与令牌拼接后 SHA-256。</summary>
        private static string GenerateSecMsGec()
        {
            // 用 double 保持与 edge-tts（Python float）一致的浮点语义。
            var ticks = (double)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L);
            ticks -= ticks % 300.0;
            ticks *= 10000000.0;
            var toHash = ticks.ToString("F0", CultureInfo.InvariantCulture)
                + TrustedClientToken;
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(toHash));
            return Convert.ToHexString(hash).ToUpperInvariant();
        }

        private static string GenerateMuid()
        {
            var bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToUpperInvariant();
        }

        private static string DateToString(DateTimeOffset utcNow)
        {
            // JavaScript Date.toString 风格（如 "Tue Apr 13 2026 12:00:00 GMT+0000 (Coordinated Universal Time)"）。
            return utcNow.ToString(
                "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
                CultureInfo.InvariantCulture);
        }

        private static string FormatPercentage(int percentage)
        {
            // 服务端参数校验要求符号前缀（如 "+0%"）；无符号的 "0%" 会被拒绝。
            return percentage >= 0
                ? "+" + percentage + "%"
                : percentage + "%";
        }

        private void CancelActiveLocked()
        {
            var active = _active;
            _active = null;
            if (active == null) return;
            try
            {
                active.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EdgeOnlineTtsProvider));
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class EdgeVoiceInfo
        {
            public string Name { get; set; }
            public string ShortName { get; set; }
            public string Locale { get; set; }
        }
    }
}
