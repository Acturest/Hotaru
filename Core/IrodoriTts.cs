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
    /// <summary>
    /// 本地 Irodori-TTS（Gradio 服务）客户端 —— 给「日语」目标语言额外提供一条第三方语音通道。
    ///
    /// 协议（Gradio 6.x，api_prefix = /gradio_api）：
    ///   1) POST {base}/gradio_api/call/_run_generation   body = {"data":[30 项]}  → {"event_id":"…"}
    ///   2) GET  {base}/gradio_api/call/_run_generation/{event_id}   读 SSE 到 event: complete
    ///      返回 34 元组：[0] 音频（{"value":{"path":…,"url":…}}）、[32] Run Log、[33] Timing
    ///
    /// 模型在服务端常驻：只要发的 checkpoint / 设备 / 精度与后端已加载的一致，就会命中缓存
    /// （Run Log 里会显示 runtime: reused），不会重新加载模型。
    /// </summary>
    public class IrodoriTts
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // 一次只允许一个合成请求在跑（本地单卡服务，并发只会互相拖慢、还会乱序返回）。
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // 缓存探测到的 API 前缀，避免每次都问一次服务。
        private static string _prefixBase;
        private static string _prefix;

        public string BaseUrl { get; set; } = "http://127.0.0.1:7860";
        public string Checkpoint { get; set; } = "";
        public string ModelDevice { get; set; } = "cuda";
        public string ModelPrecision { get; set; } = "bf16";
        public string CodecDevice { get; set; } = "cuda";
        public string CodecPrecision { get; set; } = "fp32";
        public double DurationScale { get; set; } = 1.0;
        /// <summary>随机种子（字符串）：留空 = 每次随机，服务端会把实际用的种子写进 Run Log 的 seed_used。</summary>
        public string Seed { get; set; } = "";

        // ==================== 音色设计模式（v4.1 的 caption / ref_wavs / CFG / 采样） ====================
        /// <summary>风格描述（官方建议用日语写）。空 = 不用 Caption 条件。</summary>
        public string Caption { get; set; } = "";
        /// <summary>参考音频的本地文件路径（1 个或多个，按顺序拼接）；空 = 无参考音频。</summary>
        public List<string> RefWavs { get; set; } = new List<string>();
        /// <summary>采样步数（字符串，空 = 服务端默认；配合 sway 建议 6~8）。</summary>
        public string NumSteps { get; set; } = "";
        /// <summary>时间调度：linear / sway。</summary>
        public string TScheduleMode { get; set; } = "linear";
        /// <summary>sway 系数（-1 = 服务端默认）。</summary>
        public double SwayCoeff { get; set; } = -1;
        /// <summary>CFG 引导模式：independent / joint / alternating。</summary>
        public string CfgGuidanceMode { get; set; } = "independent";
        /// <summary>CFG：念字准确度。</summary>
        public double CfgScaleText { get; set; } = 3;
        /// <summary>CFG：遵循 Caption 的力度。</summary>
        public double CfgScaleCaption { get; set; } = 4;
        /// <summary>CFG：参考音频与 Caption 谁更强势。</summary>
        public double CfgScaleSpeaker { get; set; } = 5;
        /// <summary>joint 模式的全局权重（非空时覆盖上面三个，服务端用它当 cfg_scale_raw）。</summary>
        public string CfgScaleRaw { get; set; } = "";
        /// <summary>LoRA 模式：适配器文件夹路径（写进 lora_adapter_raw）；空 = 不加载 LoRA。</summary>
        public string LoraAdapterDir { get; set; } = "";

        /// <summary>参考音频的本地存放目录（程序目录下的「参考音频」，不存在时创建）。</summary>
        public static string ReferenceDir
        {
            get
            {
                var d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "参考音频");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch (Exception) { }
                return d;
            }
        }

        /// <summary>把配置里的参考音频（文件名或绝对路径）解析成真实路径；空 / 不存在都返回空。</summary>
        public static string ResolveReference(string refWav)
        {
            var p = (refWav ?? "").Trim();
            if (p.Length == 0) return "";
            if (File.Exists(p)) return p;
            try
            {
                var q = Path.Combine(ReferenceDir, p);
                if (File.Exists(q)) return q;
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>按设置面板里的配置造一个客户端（seed 由调用方换算好传进来）。</summary>
        public static IrodoriTts FromConfig(AppConfig cfg, string seed = "")
        {
            var t = new IrodoriTts(cfg.irodori_url, cfg.irodori_checkpoint, cfg.irodori_model_device,
                                   cfg.irodori_model_precision, cfg.irodori_codec_device,
                                   cfg.irodori_codec_precision, cfg.irodori_duration_scale, seed ?? "");
            bool design = string.Equals(cfg.irodori_mode, "design", StringComparison.OrdinalIgnoreCase);
            bool lora = string.Equals(cfg.irodori_mode, "lora", StringComparison.OrdinalIgnoreCase);
            // LoRA 模式沿用音色设计模式的采样 / CFG 参数，只是把「风格描述」换成 LoRA 适配器
            bool sampling = design || lora;
            t.NumSteps = sampling ? (cfg.irodori_num_steps ?? "") : "";              // 标准克隆模式保持原有采样参数
            t.TScheduleMode = sampling ? (cfg.irodori_t_schedule ?? "linear") : "linear";
            t.SwayCoeff = sampling ? cfg.irodori_sway_coeff : -1;
            t.CfgGuidanceMode = sampling ? (cfg.irodori_cfg_mode ?? "independent") : "independent";
            t.CfgScaleText = sampling ? cfg.irodori_cfg_text : 3;
            t.CfgScaleCaption = sampling ? cfg.irodori_cfg_caption : 4;
            t.CfgScaleSpeaker = sampling ? cfg.irodori_cfg_speaker : 5;
            t.CfgScaleRaw = sampling && string.Equals(t.CfgGuidanceMode, "joint", StringComparison.OrdinalIgnoreCase)
                ? cfg.irodori_cfg_joint.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            t.Caption = design ? (cfg.irodori_caption ?? "").Trim() : "";   // LoRA 模式不带风格描述
            t.LoraAdapterDir = lora ? (cfg.irodori_lora_dir ?? "").Trim() : "";
            // 无参考音频模式（只在音色设计 / LoRA 模式下有意义）：即使选过音频也不上传
            string refPath = sampling && cfg.irodori_no_ref ? "" : ResolveReference(cfg.irodori_ref_wav);
            if (refPath.Length > 0) t.RefWavs.Add(refPath);
            return t;
        }

        /// <summary>上一次合成的服务端 Run Log（含 runtime: reused / seed / 时长预测）。</summary>
        public string LastRunLog { get; private set; } = "";
        /// <summary>上一次合成的服务端耗时明细。</summary>
        public string LastTiming { get; private set; } = "";

        public IrodoriTts() { }

        public IrodoriTts(string baseUrl, string checkpoint, string modelDevice, string modelPrecision,
                          string codecDevice, string codecPrecision, double durationScale, string seed = "")
        {
            BaseUrl = baseUrl;
            Checkpoint = checkpoint;
            ModelDevice = modelDevice;
            ModelPrecision = modelPrecision;
            CodecDevice = codecDevice;
            CodecPrecision = codecPrecision;
            DurationScale = durationScale;
            Seed = seed ?? "";
        }

        private string Root
        {
            get
            {
                var b = (BaseUrl ?? "").Trim().TrimEnd('/');
                if (b.Length == 0) b = "http://127.0.0.1:7860";
                if (!b.StartsWith("http", StringComparison.OrdinalIgnoreCase)) b = "http://" + b;
                return b;
            }
        }

        /// <summary>探测 API 前缀：新版 Gradio 走 /gradio_api，老版直接用根路径。</summary>
        private async Task<string> PrefixAsync(CancellationToken ct)
        {
            var root = Root;
            if (_prefix != null && _prefixBase == root) return _prefix;
            foreach (var p in new[] { "/gradio_api", "" })
            {
                try
                {
                    using var resp = await Http.GetAsync(root + p + "/info", ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        _prefixBase = root; _prefix = p;
                        return p;
                    }
                }
                catch (Exception) { /* 试下一个 */ }
            }
            throw new InvalidOperationException("连不上 Irodori-TTS 服务（" + root + "），请确认它已经启动。");
        }

        private static string Short(string s, int n = 300)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static string Describe(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException) return "已取消";
            if (ex is HttpRequestException) return "连不上 Irodori-TTS 服务：" + ex.Message;
            return ex.Message;
        }

        /// <summary>POST 建任务并读 SSE，返回 complete 那一帧的 JSON 数组文本。</summary>
        private async Task<JsonElement> CallAsync(string fn, object[] data, CancellationToken ct)
        {
            var prefix = await PrefixAsync(ct);
            var url = Root + prefix + "/call/" + fn;
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["data"] = data });

            HttpResponseMessage resp;
            try
            {
                resp = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            }
            catch (Exception ex) { throw new InvalidOperationException(Describe(ex)); }

            string text;
            using (resp)
            {
                text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("服务返回 " + (int)resp.StatusCode + "：" + Short(text));
            }

            string eventId = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("event_id", out var e)) eventId = e.GetString();
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(eventId))
                throw new InvalidOperationException("服务没有返回 event_id：" + Short(text));

            var streamUrl = url + "/" + eventId;
            try
            {
                using var sreq = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                sreq.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                using var sresp = await Http.SendAsync(sreq, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!sresp.IsSuccessStatusCode)
                    throw new InvalidOperationException("读取结果失败：" + (int)sresp.StatusCode);
                using var stream = await sresp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string ev = null, dataLine = null;
                string line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (line.StartsWith("event:", StringComparison.Ordinal)) ev = line.Substring(6).Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) dataLine = line.Substring(5).Trim();
                    else if (line.Length == 0)
                    {
                        if (ev == "complete")
                        {
                            if (dataLine == null)
                                throw new InvalidOperationException("服务返回了空的合成结果");
                            return JsonDocument.Parse(dataLine).RootElement.Clone();
                        }
                        if (ev == "error")
                            throw new InvalidOperationException("服务报错：" + Short(dataLine ?? ""));
                        ev = null; dataLine = null;
                    }
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException(Describe(ex)); }

            throw new InvalidOperationException("服务没有返回合成结果（连接提前结束）");
        }

        /// <summary>把 _run_generation 的 30 个入参按 v4.1 文档顺序拼好（顺序就是 /info 里 parameters 的顺序）。</summary>
        private object[] BuildArgs(string text, List<string> refServerPaths)
        {
            object refs = null;
            if (refServerPaths != null && refServerPaths.Count > 0)
            {
                var list = new List<object>();
                foreach (var p in refServerPaths) list.Add(FileData(p));
                refs = list;
            }
            return new object[]
            {
                Checkpoint ?? "",                              // 0  checkpoint
                ModelDevice ?? "cuda",                         // 1  model_device
                ModelPrecision ?? "fp32",                      // 2  model_precision
                CodecDevice ?? "cuda",                         // 3  codec_device
                CodecPrecision ?? "fp32",                      // 4  codec_precision
                text,                                          // 5  text
                Caption ?? "",                                 // 6  caption（风格描述）
                refs,                                          // 7  ref_wavs（参考音频，null = 无参考音频模式）
                NumSteps ?? "",                                // 8  num_steps（空 = 服务端默认）
                1,                                             // 9  num_candidates
                Seed ?? "",                                    // 10 seed_raw（留空 = 每次随机）
                "",                                            // 11 seconds_raw
                DurationScale,                                 // 12 duration_scale
                TScheduleMode ?? "linear",                     // 13 t_schedule_mode
                SwayCoeff,                                     // 14 sway_coeff
                CfgGuidanceMode ?? "independent",              // 15 cfg_guidance_mode
                CfgScaleText,                                  // 16 cfg_scale_text
                CfgScaleCaption,                               // 17 cfg_scale_caption
                CfgScaleSpeaker,                               // 18 cfg_scale_speaker
                CfgScaleRaw ?? "",                             // 19 cfg_scale_raw（joint 的全局权重）
                0.5,                                           // 20 cfg_min_t
                1.0,                                           // 21 cfg_max_t
                true,                                          // 22 context_kv_cache
                "",                                            // 23 speaker_kv_scale_raw
                "",                                            // 24 max_text_len_raw
                "",                                            // 25 max_caption_len_raw
                "",                                            // 26 truncation_factor_raw
                "",                                            // 27 rescale_k_raw
                "",                                            // 28 rescale_sigma_raw
                LoraAdapterDir ?? ""                           // 29 lora_adapter_raw（LoRA 模式：适配器文件夹）
            };
        }

        /// <summary>Gradio 的 FileData 结构（服务端只认自己临时目录里的路径，所以参考音频必须先上传）。</summary>
        private static Dictionary<string, object> FileData(string serverPath)
        {
            return new Dictionary<string, object>
            {
                ["path"] = serverPath ?? "",
                ["meta"] = new Dictionary<string, object> { ["_type"] = "gradio.FileData" },
            };
        }

        /// <summary>已上传过的参考音频：本地路径 → (长度, 修改时间, 服务端路径)，避免每段都重传一次。</summary>
        private static readonly Dictionary<string, (long Len, DateTime Mtime, string Server)> _uploaded =
            new Dictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>把本地参考音频上传到 Gradio（multipart POST {prefix}/upload），返回服务端路径。</summary>
        private async Task<string> UploadOneAsync(string localPath, CancellationToken ct)
        {
            FileInfo fi;
            try { fi = new FileInfo(localPath); } catch (Exception ex) { throw new InvalidOperationException("读不到参考音频：" + ex.Message); }
            if (!fi.Exists) throw new InvalidOperationException("找不到参考音频文件：" + localPath);
            var key = fi.FullName;
            lock (_uploaded)
            {
                if (_uploaded.TryGetValue(key, out var hit) && hit.Len == fi.Length && hit.Mtime == fi.LastWriteTimeUtc)
                    return hit.Server;
            }

            var prefix = await PrefixAsync(ct);
            var url = Root + prefix + "/upload?upload_id=" + Guid.NewGuid().ToString("N");
            using var form = new MultipartFormDataContent();
            byte[] bytes;
            try { bytes = File.ReadAllBytes(fi.FullName); }
            catch (Exception ex) { throw new InvalidOperationException("读不到参考音频：" + ex.Message); }
            var content = new ByteArrayContent(bytes);
            try { content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(AudioMime(fi.Extension)); }
            catch (Exception) { }
            form.Add(content, "files", fi.Name);

            string txt;
            try
            {
                using var resp = await Http.PostAsync(url, form, ct);
                txt = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("参考音频上传失败：" + (int)resp.StatusCode + " " + Short(txt));
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException(Describe(ex)); }

            string server = null;
            try
            {
                using var doc = JsonDocument.Parse(txt);
                var r = doc.RootElement;
                if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0)
                {
                    var el = r[0];
                    if (el.ValueKind == JsonValueKind.String) server = el.GetString();
                    else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("path", out var p2) && p2.ValueKind == JsonValueKind.String)
                        server = p2.GetString();
                }
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(server))
                throw new InvalidOperationException("参考音频上传返回格式不对：" + Short(txt));

            lock (_uploaded) _uploaded[key] = (fi.Length, fi.LastWriteTimeUtc, server);
            App.Log("Irodori 参考音频已上传: " + fi.Name + " (" + (bytes.Length / 1024) + " KB) → " + server);
            return server;
        }

        private static string AudioMime(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".flac": return "audio/flac";
                case ".m4a": return "audio/mp4";
                case ".ogg": return "audio/ogg";
                default: return "audio/wav";
            }
        }

        private void ReadMeta(JsonElement arr)
        {
            LastRunLog = ""; LastTiming = "";
            if (arr.ValueKind != JsonValueKind.Array) return;
            if (arr.GetArrayLength() > 32 && arr[32].ValueKind == JsonValueKind.String) LastRunLog = arr[32].GetString();
            if (arr.GetArrayLength() > 33 && arr[33].ValueKind == JsonValueKind.String) LastTiming = arr[33].GetString();
        }

        /// <summary>合成日语语音，成功返回本地 wav 文件路径（失败抛 InvalidOperationException，消息可直接展示）。</summary>
        public async Task<string> SynthesizeAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("没有可朗读的文本");
            if (string.IsNullOrWhiteSpace(Checkpoint)) throw new InvalidOperationException("还没填 Irodori-TTS 的 checkpoint 路径");

            // 本地服务是单卡的：同时发多个请求只会互相抢 GPU（实测 3 个并发时第一段从 3.5 秒拖到 12 秒，
            // 而且完成顺序还会乱），所以这里强制排队，一次只跑一个请求。
            await _gate.WaitAsync(ct);
            try
            {
                return await SynthesizeOnceAsync(text, ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<string> SynthesizeOnceAsync(string text, CancellationToken ct)
        {
            JsonElement arr;
            try
            {
                // 参考音频必须先上传到服务端自己的临时目录，再把服务端路径包成 FileData 传过去
                var refServer = new List<string>();
                foreach (var p in RefWavs ?? new List<string>())
                {
                    var resolved = ResolveReference(p);
                    if (resolved.Length == 0) throw new InvalidOperationException("找不到参考音频文件：" + p);
                    refServer.Add(await UploadOneAsync(resolved, ct));
                }
                arr = await CallAsync("_run_generation", BuildArgs(text, refServer), ct);
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException(Describe(ex)); }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                throw new InvalidOperationException("合成结果格式不对");
            ReadMeta(arr);

            string path = null, url = null;
            var first = arr[0];
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String) path = p.GetString();
                if (v.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString();
            }

            // 服务在本机时 path 就是真实文件路径，直接读；否则退回从 url 里再抠一次路径。
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) { Remember(path); return path; }
            if (!string.IsNullOrEmpty(url))
            {
                int k = url.IndexOf("file=", StringComparison.OrdinalIgnoreCase);
                if (k >= 0)
                {
                    var p2 = Uri.UnescapeDataString(url.Substring(k + 5));
                    if (File.Exists(p2)) { Remember(p2); return p2; }
                }
            }
            throw new InvalidOperationException("服务没有返回可用的音频文件（可能服务在另一台机器上）");
        }

        // ==================== 生成的语音文件：记账与清理 ====================
        // 每次合成的音频是服务端写在 %LOCALAPPDATA%\Temp\gradio\<hash>\sample_*.wav 的临时文件，
        // 服务端不会替我们删。这里自己记账（写到文件里，程序被强杀也留得下线索），
        // 并在「翻译下一句 / 关输入窗 / 退出程序 / 设置里手动清理」时删除。

        /// <summary>记账文件：一行一个已经生成过的音频路径。</summary>
        public static string CacheIndexPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "悬浮翻译器.tts_cache.txt"); }
        }

        /// <summary>Gradio 的临时根目录（%LOCALAPPDATA%\Temp\gradio）。</summary>
        public static string GradioTempRoot
        {
            get { return Path.Combine(Path.GetTempPath(), "gradio"); }
        }

        /// <summary>把生成的音频路径记进账本（重复的不再记）。</summary>
        public static void Remember(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (ReadIndex().Contains(path)) return;
                File.AppendAllText(CacheIndexPath, path + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { /* 记账失败不影响播放 */ }
        }

        private static List<string> ReadIndex()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(CacheIndexPath)) return list;
                foreach (var line in File.ReadAllLines(CacheIndexPath))
                {
                    var s = line.Trim();
                    if (s.Length > 0 && !list.Contains(s)) list.Add(s);
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>
        /// 清理生成的语音文件：先删账本里记着的，再（可选）扫一遍 Gradio 临时目录里遗留的 sample_*。
        /// 返回 (删除个数, 释放字节数)。正在播放的文件删不掉，会留在账本里等下次。
        /// </summary>
        public static (int Count, long Bytes) CleanupCache(bool sweepLeftovers)
        {
            int n = 0; long bytes = 0;
            var failed = new List<string>();
            var idx = ReadIndex();

            foreach (var p in idx)
            {
                if (!TryDelete(p, ref n, ref bytes) && File.Exists(p)) failed.Add(p);
            }

            if (idx.Count > 0)
                App.Log("Irodori 缓存清理: 账本 " + idx.Count + " 条 → 删除 " + n + " 个"
                        + (failed.Count > 0 ? "，失败 " + failed.Count + " 个（可能还在播放或没有权限）" : "")
                        + (sweepLeftovers ? "，含遗留扫描" : ""));

            if (sweepLeftovers)
            {
                try
                {
                    var root = GradioTempRoot;
                    if (Directory.Exists(root))
                    {
                        foreach (var f in Directory.GetFiles(root, "sample_*", SearchOption.AllDirectories))
                        {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            if (ext != ".wav" && ext != ".mp3" && ext != ".flac" && ext != ".ogg") continue;
                            if (failed.Contains(f)) continue;
                            TryDelete(f, ref n, ref bytes);
                        }
                        // 顺手删掉已经空掉的 <hash> 目录
                        foreach (var d in Directory.GetDirectories(root))
                        {
                            try { if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception) { }
            }

            try
            {
                if (failed.Count == 0) File.WriteAllText(CacheIndexPath, "", Encoding.UTF8);
                else File.WriteAllLines(CacheIndexPath, failed, Encoding.UTF8);
            }
            catch (Exception) { }

            return (n, bytes);
        }

        private static bool TryDelete(string path, ref int n, ref long bytes)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return true;
                if (!File.Exists(path))
                {
                    App.Log("Irodori 缓存清理: 文件已不存在（或读不到）" + path);
                    return true;
                }
                long len = 0;
                try { len = new FileInfo(path).Length; } catch (Exception) { }
                File.Delete(path);
                n++; bytes += len;
                return true;
            }
            catch (Exception ex)
            {
                App.Log("Irodori 缓存清理: 删不掉 " + path + " → " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>设置面板里手动「检测服务 / 加载模型」：返回服务端的 Model Status 文本（模型一致时会显示 reused）。</summary>
        public async Task<string> LoadModelAsync(CancellationToken ct)
        {
            // 新版服务没有 _load_model，改成 _describe_runtime：它会按这份模型参数确认（必要时加载）运行时并回报状态
            var data = new object[] { Checkpoint ?? "", ModelDevice ?? "cuda", ModelPrecision ?? "fp32",
                                      CodecDevice ?? "cuda", CodecPrecision ?? "fp32" };
            var arr = await CallAsync("_describe_runtime", data, ct);
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var v = arr[0];
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.String)
                    return inner.GetString();
            }
            return "（服务没有返回状态文本）";
        }

        /// <summary>让服务端把已加载的模型从显存里清掉（下次合成本动重载）；返回服务端的说明文本。</summary>
        public async Task<string> ClearRuntimeAsync(CancellationToken ct)
        {
            var arr = await CallAsync("_clear_runtime_cache", new object[0], ct);
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var v = arr[0];
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
            }
            return "已清理";
        }

        /// <summary>只探活，不改动服务状态。</summary>
        public async Task<string> PingAsync(CancellationToken ct)
        {
            var prefix = await PrefixAsync(ct);
            using var resp = await Http.GetAsync(Root + prefix + "/info", ct);
            return resp.IsSuccessStatusCode ? "OK" : ("HTTP " + (int)resp.StatusCode);
        }
    }
}
