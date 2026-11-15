using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FloatingTranslator
{
    /// <summary>微软 Edge「大声朗读」在线语音（免费、无需 Key）。
    /// 和系统语音（Speaker.cs，SAPI/OneCore）并列的一条朗读通道，质量高一个档次：
    /// 中文 Xiaoxiao/Yunxi、日语 Nanami 都是神经语音，不是老式拼接音。
    /// 代价是要联网，而且微软会隔一段时间改一次握手（2026-10 把令牌升到 v2，
    /// 旧客户端全线 403），改算法时只需要跟着改 EdgeDrm.cs。</summary>
    public class EdgeTts : IDisposable
    {
        const string Wss = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
        const string ChromiumVersion = "143.0.3650.75";
        const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
        const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                               + " (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

        /// <summary>本机时钟和服务器的偏差（秒）。403 时按响应头 Date 校准，之后一直沿用。</summary>
        static double _skewSeconds;

        readonly AudioPlayer _player;
        CancellationTokenSource _cts;
        TaskCompletionSource<bool> _playDone;
        volatile bool _busy;

        public bool Busy { get { return _busy; } }

        /// <summary>只能在 UI 线程建（AudioPlayer 依赖 MediaPlayer）。</summary>
        public EdgeTts()
        {
            _player = new AudioPlayer();
            _player.Ended += () => { var t = _playDone; if (t != null) t.TrySetResult(true); };
            _player.Failed += msg => { var t = _playDone; if (t != null) t.TrySetException(new Exception(msg)); };
            try { Directory.CreateDirectory(CacheDir); } catch (Exception) { }
        }

        public static string CacheDir
        {
            get { return Path.Combine(Path.GetTempPath(), "悬浮翻译器_edge_tts"); }
        }

        /// <summary>语言代码 → 默认音色。</summary>
        public static string DefaultVoice(string lang)
        {
            string p = (lang ?? "").Split('-')[0].ToLowerInvariant();
            switch (p)
            {
                case "zh":
                case "zh-hant":
                    return "zh-CN-XiaoxiaoNeural";
                case "ja":
                    return "ja-JP-NanamiNeural";
                case "en":
                    return "en-US-AriaNeural";
                default:
                    return "zh-CN-XiaoxiaoNeural";
            }
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }
            var t = _playDone;
            if (t != null) t.TrySetCanceled();
            try { _player.Stop(); } catch (Exception) { }
        }

        /// <summary>朗读一段文本：按句切段，边合成边播（下一段在上一段播的时候就开工）。
        /// 除了「开播 / 停止」两处，全部在后台线程跑——UI 线程一旦被网络或解码卡住，窗口就拖不动了。</summary>
        public Task SpeakAsync(string text, string voice, int rate, Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
            Stop();
            _busy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            StartLagProbe();
            return Task.Run(() => SpeakCoreAsync(text, voice, rate, ct, onError));
        }

        async Task SpeakCoreAsync(string text, string voice, int rate, CancellationToken ct, Action<string> onError)
        {
            var files = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(CacheDir);
                SweepOldFiles();
                var chunks = Split(text);
                App.Log("Edge 朗读: " + text.Length + " 字 → " + chunks.Count + " 段，音色 " + voice
                        + "，语速 " + rate);
                Task<string> prefetch = null;
                string cur = null;
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    _phase = "合成第 " + (i + 1) + " 段";
                    cur = prefetch != null
                        ? await prefetch.ConfigureAwait(false)
                        : await SynthToFileAsync(chunks[i], voice, rate, ct).ConfigureAwait(false);
                    files.Add(cur);
                    prefetch = (i + 1 < chunks.Count)
                        ? SynthToFileAsync(chunks[i + 1], voice, rate, ct)
                        : null;
                    _phase = "播放第 " + (i + 1) + " 段";
                    await PlayAsync(cur, ct).ConfigureAwait(false);
                }
                _phase = "收尾";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Log("Edge 朗读失败: " + ex.Message);
                if (onError != null) onError("在线朗读失败，可切回系统语音：" + Short(ex.Message));
            }
            finally
            {
                long total = sw.ElapsedMilliseconds;
                string lag = StopLagProbe();
                App.Log("Edge 朗读结束: 合计 " + total + "ms" + (lag.Length > 0 ? "，" + lag : ""));
                _busy = false;
                // 删文件放到最后再做，而且让 MediaPlayer 先放开句柄（否则第一次删必然失败）
                var dead = files.ToArray();
                _ = Task.Run(() =>
                {
                    Thread.Sleep(600);
                    foreach (string f in dead) TryDelete(f);
                });
            }
        }

        // ---- UI 卡顿探针：朗读期间每 50ms 打一次点，最后报最大延迟发生在哪个阶段 ----
        System.Windows.Threading.DispatcherTimer _lagTimer;
        DateTime _lagNext;
        long _lagMax;
        string _lagWhere = "";
        volatile string _phase = "";

        void StartLagProbe()
        {
            _lagMax = 0;
            _lagWhere = "";
            _phase = "准备";
            var app = System.Windows.Application.Current;
            if (app == null) return;
            _lagNext = DateTime.UtcNow.AddMilliseconds(50);
            _lagTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, app.Dispatcher);
            _lagTimer.Interval = TimeSpan.FromMilliseconds(50);
            _lagTimer.Tick += (s, e) =>
            {
                DateTime now = DateTime.UtcNow;
                long late = (long)(now - _lagNext).TotalMilliseconds;
                if (late > _lagMax) { _lagMax = late; _lagWhere = _phase; }
                _lagNext = now.AddMilliseconds(50);
            };
            _lagTimer.Start();
        }

        string StopLagProbe()
        {
            if (_lagTimer != null)
            {
                try { _lagTimer.Stop(); } catch (Exception) { }
                _lagTimer = null;
            }
            return _lagMax >= 120 ? ("UI 最大卡顿 " + _lagMax + "ms（" + _lagWhere + "）") : "";
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "未知原因";
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        /// <summary>按句末标点切段，每段不超过 280 字（再长单次合成的延迟会很明显）。</summary>
        static List<string> Split(string text)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in text.Replace("\r", ""))
            {
                sb.Append(c);
                bool stop = c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '；'
                            || c == ';' || c == '…' || c == '\n';
                if ((stop && sb.Length >= 12) || sb.Length >= 280)
                {
                    string s = sb.ToString().Trim();
                    if (s.Length > 0) result.Add(s);
                    sb.Clear();
                }
            }
            string tail = sb.ToString().Trim();
            if (tail.Length > 0) result.Add(tail);
            if (result.Count == 0) result.Add(text.Trim());
            return result;
        }

        async Task PlayAsync(string path, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            _playDone = tcs;
            var disp = System.Windows.Application.Current.Dispatcher;
            await disp.InvokeAsync(() =>
            {
                try { _player.Play(path); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            using (ct.Register(() => tcs.TrySetCanceled()))
            using (var to = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (to.Token.Register(() => tcs.TrySetException(new Exception("播放超时"))))
                await tcs.Task;
        }

        // ===================== 合成 =====================

        /// <summary>只合成不播放（设置页的「试听 / 检测」用）。返回 mp3 字节。</summary>
        public Task<byte[]> SynthesizeAsync(string text, string voice, int rate, CancellationToken ct)
        {
            return SynthAsync(text, voice, rate, ct, true);
        }

        async Task<string> SynthToFileAsync(string text, string voice, int rate, CancellationToken ct)
        {
            byte[] mp3 = await SynthAsync(text, voice, rate, ct, true);
            string path = Path.Combine(CacheDir, "e" + Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(path, mp3);
            return path;
        }

        async Task<byte[]> SynthAsync(string text, string voice, int rate, CancellationToken ct, bool allowRetry)
        {
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("Pragma", "no-cache");
                ws.Options.SetRequestHeader("Cache-Control", "no-cache");
                ws.Options.SetRequestHeader("Origin", Origin);
                ws.Options.SetRequestHeader("User-Agent", UserAgent);
                ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
                ws.Options.SetRequestHeader("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");

                DateTime now = DateTime.UtcNow.AddSeconds(_skewSeconds);
                string token = EdgeDrm.GenerateSecMsGec(now);
                string url = Wss
                    + "?TrustedClientToken=" + EdgeDrm.TrustedClientToken
                    + "&ConnectionId=" + Guid.NewGuid().ToString("N")
                    + "&Sec-MS-GEC=" + token
                    + "&Sec-MS-GEC-Version=2-" + ChromiumVersion;

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        await ws.ConnectAsync(new Uri(url), timeout.Token);
                    }
                    catch (Exception ex) when (allowRetry && !ct.IsCancellationRequested)
                    {
                        // 大概率是本机时钟和服务端差太多（令牌落不进 5 分钟窗口）；
                        // 拿音色列表的 Date 头对一次表，然后重试一次。
                        App.Log("Edge 握手失败（" + ex.Message + "），校准时钟后重试");
                        if (!await CalibrateClockAsync()) throw;
                        return await SynthAsync(text, voice, rate, ct, false);
                    }
                    if (ws.State != WebSocketState.Open)
                        throw new Exception("连接未打开：" + ws.State);
                    await SendConfigAsync(ws, timeout.Token);
                    await SendSsmlAsync(ws, text, voice, rate, timeout.Token);
                    return await ReadAudioAsync(ws, timeout.Token);
                }
            }
        }

        static async Task<bool> CalibrateClockAsync()
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    string url = "https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list"
                               + "?trustedclienttoken=" + EdgeDrm.TrustedClientToken;
                    using (var resp = await http.GetAsync(url))
                    {
                        if (resp.Headers.Date.HasValue)
                        {
                            _skewSeconds = (resp.Headers.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                            App.Log("Edge 时钟偏差已校准: " + _skewSeconds.ToString("F1") + " 秒");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) { App.Log("Edge 时钟校准失败: " + ex.Message); }
            return false;
        }

        static Task SendConfigAsync(ClientWebSocket ws, CancellationToken ct)
        {
            string json = "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{"
                        + "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},"
                        + "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
            string msg = "X-Timestamp:" + Stamp() + "\r\n"
                       + "Content-Type:application/json; charset=utf-8\r\n"
                       + "Path:speech.config\r\n\r\n" + json + "\r\n";
            return SendTextAsync(ws, msg, ct);
        }

        static Task SendSsmlAsync(ClientWebSocket ws, string text, string voice, int rate, CancellationToken ct)
        {
            int pct = Math.Max(-50, Math.Min(50, rate * 10));
            string r = (pct >= 0 ? "+" : "") + pct.ToString(CultureInfo.InvariantCulture) + "%";
            string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                        + "<voice name='" + voice + "'>"
                        + "<prosody pitch='+0Hz' rate='" + r + "' volume='+0%'>"
                        + Escape(text)
                        + "</prosody></voice></speak>";
            string msg = "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\n"
                       + "Content-Type:application/ssml+xml\r\n"
                       + "X-Timestamp:" + Stamp() + "Z\r\n"
                       + "Path:ssml\r\n\r\n" + ssml;
            return SendTextAsync(ws, msg, ct);
        }

        static Task SendTextAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        {
            byte[] b = Encoding.UTF8.GetBytes(msg);
            return ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, ct);
        }

        static async Task<byte[]> ReadAudioAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var audio = new MemoryStream();
            var buf = new byte[16 * 1024];
            var frame = new MemoryStream();
            while (true)
            {
                frame.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    frame.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                byte[] payload = frame.ToArray();
                if (payload.Length < 2) continue;

                if (r.MessageType == WebSocketMessageType.Text)
                {
                    string head = Encoding.UTF8.GetString(payload);
                    if (head.Contains("Path:turn.end")) break;
                    continue;
                }
                if (r.MessageType != WebSocketMessageType.Binary) continue;

                int headerLen = (payload[0] << 8) | payload[1];
                if (headerLen + 2 > payload.Length) continue;
                string headers = Encoding.ASCII.GetString(payload, 2, headerLen);
                if (!headers.Contains("Path:audio")) continue;
                int start = 2 + headerLen;
                audio.Write(payload, start, payload.Length - start);
            }
            if (audio.Length == 0) throw new Exception("没有收到音频数据");
            return audio.ToArray();
        }

        static string Stamp()
        {
            // 官方客户端发的是这种非 RFC1123 的格式，服务端就认它（"GMT+0000" 不是笔误）
            return DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
                                            CultureInfo.InvariantCulture);
        }

        static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("'", "&apos;").Replace("\"", "&quot;");
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        /// <summary>清掉上一次异常退出留下的临时音频（只动 1 小时前的，免得删到正在播的）。</summary>
        static void SweepOldFiles()
        {
            try
            {
                var dir = new DirectoryInfo(CacheDir);
                DateTime limit = DateTime.Now.AddHours(-1);
                foreach (var f in dir.GetFiles("e*.mp3"))
                    if (f.LastWriteTime < limit) TryDelete(f.FullName);
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            Stop();
            try { _player.Dispose(); } catch (Exception) { }
        }
    }
}
