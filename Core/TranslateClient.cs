using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FloatingTranslator
{
    public class TranslateException : Exception
    {
        public TranslateException(string msg) : base(msg) { }
    }

    /// <summary>OpenAI 兼容的流式翻译客户端（对应 call_api.py 的 call_completion）</summary>
    public class TranslateClient
    {
        private readonly string _base;
        private readonly string _key;
        private readonly int _timeout;

        /// <summary>发生降级时通知界面：参数是（原模型、新模型、原因）。</summary>
        public static event Action<string, string, string> Fallback;

        /// <summary>最近一次真正发出请求的模型（降级后用来看实际用的是哪一档）。</summary>
        public string LastModel { get; private set; } = "";

        // 降级后「粘住」低档模型：长文按段翻时，不必每段都白等一次首字超时。
        // 换配置 / 换模型会重建 TranslateClient，粘性自然失效；这里再加个 10 分钟上限兜底。
        private string _stickyModel;
        private DateTime _stickyUntil = DateTime.MinValue;

        public TranslateClient(string apiBase, string apiKey, int timeoutSec)
        {
            _base = (apiBase ?? "").TrimEnd('/');
            _key = apiKey ?? "";
            _timeout = timeoutSec <= 0 ? 120 : timeoutSec;
        }

        public async Task<string> TranslateAsync(string prompt, Action<string> onDelta,
                                                 CancellationToken ct,
                                                 Action<string> onReasoning = null)
        {
            return await SendAsync(prompt, onDelta, ct, onReasoning).ConfigureAwait(false);
        }

        /// <summary>
        /// 图片翻译：content 用数组形式（文本 + data URL 图片），对齐 call_api.py 的 --image 分支。
        /// </summary>
        public async Task<string> TranslateImageAsync(string promptText, byte[] imageBytes, string mime,
                                                      Action<string> onDelta, CancellationToken ct,
                                                      Action<string> onReasoning = null)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                throw new TranslateException("图片是空的");
            string dataUrl = "data:" + (string.IsNullOrEmpty(mime) ? "image/png" : mime) + ";base64,"
                           + Convert.ToBase64String(imageBytes);
            var content = new object[]
            {
                new Dictionary<string, object> { { "type", "text" }, { "text", promptText } },
                new Dictionary<string, object> { { "type", "image_url" },
                    { "image_url", new Dictionary<string, object> { { "url", dataUrl } } } },
            };
            return await SendAsync(content, onDelta, ct, onReasoning).ConfigureAwait(false);
        }

        /// <summary>
        /// 按降级链路发请求：当前模型在「首字超时」内没吐出第一个字（或直接报错），就换下一档重试。
        /// 一旦已经有内容流出来就不再重试（否则会重复叠加），直接抛错。
        /// </summary>
        private async Task<string> SendAsync(object content, Action<string> onDelta,
                                             CancellationToken ct, Action<string> onReasoning)
        {
            string configured = App.Config.model;
            string primary = configured;
            if (App.Config.model_fallback && !string.IsNullOrEmpty(_stickyModel)
                && !string.Equals(_stickyModel, configured, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow < _stickyUntil)
            {
                primary = _stickyModel;   // 刚降过级：直接从这里开始，别再等一次超时
            }
            int firstTok = App.Config.model_fallback_timeout;
            if (firstTok < 3) firstTok = 12;
            List<string> chain = App.Config.model_fallback
                ? AppConfig.FallbackChain(primary)
                : new List<string> { configured };

            TranslateException last = null;
            for (int i = 0; i < chain.Count; i++)
            {
                string model = chain[i];
                bool[] got = new bool[1];
                try
                {
                    string text = await SendOnceAsync(content, model, onDelta, ct, onReasoning,
                                                     () => got[0] = true, firstTok).ConfigureAwait(false);
                    LastModel = model;
                    if (string.Equals(model, configured, StringComparison.OrdinalIgnoreCase))
                    {
                        _stickyModel = null;                 // 配置的模型又能用了，解除粘性
                    }
                    else if (!string.Equals(model, primary, StringComparison.OrdinalIgnoreCase))
                    {
                        _stickyModel = model;                // 这一档救回来了，后面几段沿用它
                        _stickyUntil = DateTime.UtcNow.AddMinutes(10);
                        App.Log("翻译降级：本次会话后续请求沿用 " + AppConfig.ModelShort(model) + "（10 分钟内有效）");
                    }
                    return text;
                }
                catch (OperationCanceledException) { throw; }   // 用户取消（Ctrl+Z / 关窗），不降级
                catch (TranslateException ex)
                {
                    // 已经有内容流出来 / 没有下一档 / 错误不该换模型 → 直接抛
                    if (got[0] || i + 1 >= chain.Count || !Retryable(ex.Message)) throw;
                    last = ex;
                }
                string next = chain[i + 1];
                App.Log("翻译降级：" + AppConfig.ModelShort(model) + " → " + AppConfig.ModelShort(next)
                        + "（" + Short(last.Message) + "）");
                try { Fallback?.Invoke(model, next, last.Message); } catch { }
            }
            throw last ?? new TranslateException("翻译失败");
        }

        /// <summary>
        /// 单次请求。首字阶段用 firstTokenSec 计时，收到第一个字后改成整体超时 _timeout。
        /// 这样「模型在排队 / 没响应」和「长文一直在正常输出」不会被同一个计时器误杀。
        /// </summary>
        private async Task<string> SendOnceAsync(object content, string model, Action<string> onDelta,
                                                 CancellationToken ct, Action<string> onReasoning,
                                                 Action onFirstDelta, int firstTokenSec)
        {
            string url = _base + "/chat/completions";
            var payload = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", new object[] { new Dictionary<string, object> {
                        { "role", "user" }, { "content", content } } } },
                { "max_tokens", App.Config.max_tokens },
                { "temperature", App.Config.temperature },
                { "stream", true },
                { "chat_template_kwargs", new Dictionary<string, object> { { "enable_thinking", false } } },
            };
            string json = JsonSerializer.Serialize(payload);

            using (var handler = new HttpClientHandler { UseProxy = false })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = Timeout.InfiniteTimeSpan;   // 超时自己管（要区分首字超时与整体超时）
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    bool gotContent = false;
                    attempt.CancelAfter(TimeSpan.FromSeconds(firstTokenSec));
                    try
                    {
                        HttpResponseMessage resp;
                        using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                        {
                            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                            // 官方客户端用的是这个 UA：默认 UA 会被 WAF 拦成 HTTP 412
                            req.Headers.TryAddWithoutValidation("User-Agent", "Index-Translate-Client/1.0");
                            if (!string.IsNullOrWhiteSpace(_key))
                                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key.Trim());

                            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                                             .ConfigureAwait(false);
                        }

                        using (resp)
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                string body = "";
                                try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                                catch { }
                                throw new TranslateException(ErrorText((int)resp.StatusCode, body));
                            }

                            var sb = new StringBuilder();
                            using (var stream = await resp.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false))
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                while (true)
                                {
                                    string line = await reader.ReadLineAsync(attempt.Token).ConfigureAwait(false);
                                    if (line == null) break;
                                    if (line.Length == 0) continue;
                                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                                    string data = line.Substring(5).Trim();
                                    if (data.Length == 0) continue;
                                    if (data == "[DONE]") break;

                                    string delta = null, reason = null;
                                    try
                                    {
                                        using (var doc = JsonDocument.Parse(data))
                                        {
                                            var root = doc.RootElement;
                                            if (root.TryGetProperty("choices", out var choices) &&
                                                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                                            {
                                                var c0 = choices[0];
                                                if (c0.TryGetProperty("delta", out var d))
                                                {
                                                    if (d.TryGetProperty("content", out var cc) &&
                                                        cc.ValueKind == JsonValueKind.String)
                                                        delta = cc.GetString();
                                                    if (d.TryGetProperty("reasoning_content", out var rc) &&
                                                        rc.ValueKind == JsonValueKind.String)
                                                        reason = rc.GetString();
                                                }
                                                else if (c0.TryGetProperty("message", out var msg) &&
                                                         msg.TryGetProperty("content", out var mc) &&
                                                         mc.ValueKind == JsonValueKind.String)
                                                {
                                                    delta = mc.GetString();
                                                }
                                            }
                                        }
                                    }
                                    catch (JsonException) { continue; }

                                    if (!string.IsNullOrEmpty(reason) && onReasoning != null) onReasoning(reason);
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        if (!gotContent)
                                        {
                                            gotContent = true;
                                            // 第一个字到了：换成长文本的整体超时（中途卡死同样会中断）
                                            attempt.CancelAfter(TimeSpan.FromSeconds(_timeout));
                                            if (onFirstDelta != null) onFirstDelta();
                                        }
                                        sb.Append(delta);
                                        if (onDelta != null) onDelta(delta);
                                    }
                                }
                            }

                            string text = Prompt.StripThink(sb.ToString()).Trim();
                            if (text.Length == 0) throw new TranslateException("服务端没有返回内容");
                            return text;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;       // 用户取消，交给上层
                        throw new TranslateException(gotContent
                            ? "输出中断（" + _timeout + " 秒没有新内容）"
                            : firstTokenSec + " 秒内没有响应");
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new TranslateException("网络错误：" + ex.Message);
                    }
                }
            }
        }

        /// <summary>这种错误换一档模型有没有用：鉴权错换模型也没用，其它（超时/5xx/模型不可用）值得重试。</summary>
        private static bool Retryable(string message)
        {
            if (string.IsNullOrEmpty(message)) return true;
            if (message.Contains("401") || message.Contains("403")) return false;
            return true;
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        private static string ErrorText(int code, string body)
        {
            string detail = null;
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("detail", out var d))
                    {
                        if (d.ValueKind == JsonValueKind.String) detail = d.GetString();
                        else detail = d.ToString();
                    }
                }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(detail)) return "服务端错误：" + detail;
            if (!string.IsNullOrWhiteSpace(body))
            {
                string b = body.Trim();
                if (b.Length > 160) b = b.Substring(0, 160) + "…";
                return "HTTP " + code + "：" + b;
            }
            return "HTTP " + code;
        }
    }
}
