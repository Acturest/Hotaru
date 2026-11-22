using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FloatingTranslator
{
    /// <summary>语言定义：代码 / 中文名 / 角标 / 目标语言可选</summary>
    public class Lang
    {
        public string Code;
        public string Name;
        public string Badge;
        public bool CanBeTarget;
        public Lang(string code, string name, string badge, bool canTarget = true)
        {
            Code = code; Name = name; Badge = badge; CanBeTarget = canTarget;
        }
    }

    public class AppConfig
    {
        // ---- 接口 ----
        public string api_base { get; set; } = "https://index-translate.bilibili.com/v1";
        public string api_key { get; set; } = "";
        public int timeout { get; set; } = 120;
        public string model { get; set; } = "Index-Translate-35B-A3B";
        /// <summary>无响应自动降级：当前模型在 model_fallback_timeout 秒内没吐出第一个字，就换下一档模型重试（只降级、不升级）。</summary>
        public bool model_fallback { get; set; } = true;
        /// <summary>首字等待上限（秒）：超过就判定「无响应」，切到下一档模型。</summary>
        public int model_fallback_timeout { get; set; } = 12;

        // ---- 翻译 ----
        public string source { get; set; } = "auto";
        public string target { get; set; } = "zh";
        public int max_tokens { get; set; } = 2048;
        public double temperature { get; set; } = 0.0;
        public int chunk_limit { get; set; } = 700;
        public string instruction { get; set; } = "";
        /// <summary>文体声明（instTrans 的 genre，例如「商务邮件」「评论」「工业制造」），默认「文本」。</summary>
        public string genre { get; set; } = "";
        /// <summary>翻译指令预设：name 是设置面板里那颗按钮的名字，text 是点下去会填进「翻译指令」的整段指令。</summary>
        public class InstructionPreset
        {
            public string name { get; set; } = "";
            public string text { get; set; } = "";
        }

        /// <summary>出场自带的两个指令预设（用户还能在设置面板里「存为预设」，删掉也认）。</summary>
        public static List<InstructionPreset> DefaultPresets()
        {
            return new List<InstructionPreset>
            {
                new InstructionPreset { name = "敬体日语", text = "把文本翻译成日语，使用敬体(です・ます调)，语气客气得体。" },
                new InstructionPreset { name = "保留MD", text = "将以下文本翻译为英文，严格保留所有 Markdown 格式（标题、加粗、列表）。" },
            };
        }

        public List<InstructionPreset> instruction_presets { get; set; } = DefaultPresets();
        public string glossary { get; set; } = "";
        public bool glossary_on { get; set; } = true;

        // ---- 自动化 ----
        public string auto_translate { get; set; } = "off";   // off | paste | typing
        public int auto_delay { get; set; } = 800;
        public bool auto_copy { get; set; } = false;
        public bool auto_speak { get; set; } = false;
        public int speak_rate { get; set; } = 0;
        /// <summary>朗读通道：system = 系统语音（离线、拼接音）；edge = Edge 在线语音（神经音，需联网）。</summary>
        public string speak_engine { get; set; } = "system";
        public string edge_voice_zh { get; set; } = "zh-CN-XiaoxiaoNeural";
        public string edge_voice_ja { get; set; } = "ja-JP-NanamiNeural";
        public string edge_voice_en { get; set; } = "en-US-AriaNeural";
        public int auto_hide { get; set; } = 0;               // 空输入自动隐藏（秒），0=从不

        // ---- 热键 ----
        public bool hotkey_enabled { get; set; } = true;
        public string hotkey_toggle { get; set; } = "Ctrl+Alt+Q";
        public string hotkey_select { get; set; } = "Ctrl+Alt+W";
        public string hotkey_source { get; set; } = "clipboard";   // clipboard | selection

        // ---- 外观 / 行为 ----
        public bool topmost { get; set; } = true;
        public double opacity { get; set; } = 1.0;
        public int history_max { get; set; } = 100;
        /// <summary>输入卡片优先弹哪一侧：left（默认）/ right。</summary>
        public string panel_side { get; set; } = "left";

        // ---- 第三方语音：本地 Irodori-TTS（只在目标语言为日语时出现的那颗按钮用） ----
        // 这 5 个模型参数必须和后端「已经加载好的那份」完全一致，否则服务端会重新加载模型。
        public string irodori_url { get; set; } = "http://127.0.0.1:7860";
        public string irodori_checkpoint { get; set; } = @"E:\hf-cache\hub\models--Aratako--Irodori-TTS-v4.1-Small-Quantized\snapshots\ef04e6c3ba56138ae23e86a2eabc004f76990e37\int4-weight-only\model.safetensors";
        public string irodori_model_device { get; set; } = "cuda";
        public string irodori_model_precision { get; set; } = "bf16";
        public string irodori_codec_device { get; set; } = "cuda";
        public string irodori_codec_precision { get; set; } = "fp32";
        public double irodori_duration_scale { get; set; } = 1.0;
        /// <summary>自定义随机种子：name 是给用户看/选的名字（如「种子A」），value 是真正发给服务端的数字。</summary>
        public class IrodoriSeed
        {
            public string name { get; set; } = "";
            public string value { get; set; } = "";
        }

        /// <summary>用户自定义的种子清单（「随机」选项从这里面抽）。</summary>
        public List<IrodoriSeed> irodori_seeds { get; set; } = new List<IrodoriSeed>();
        /// <summary>当前选中的种子： "无" = 每次由服务端随机， "随机" = 从 irodori_seeds 里抽一个， 其它 = irodori_seeds 里的 name。</summary>
        public string irodori_seed_choice { get; set; } = "无";
        /// <summary>长文分段的每段字数上限（0 = 不分段，整段一次合成）。超过这个字数就切成多段，边放已生成的边合成下一段。</summary>
        public int irodori_chunk_chars { get; set; } = 60;
        /// <summary>
        /// 切分前缀：长文切成 A/B/C 三段时，**B、C 段**（第 2 段起）在合成前拼上这段文字再发给 Irodori。
        /// 第 1 段不加（它本来就是开头）。只影响合成请求，不改分段规则、也不进译文；可以是任意字符（含 emoji）。
        /// 空 = 不加。
        /// </summary>
        public string irodori_chunk_prefix { get; set; } = "";

        // ---- 音色设计模式（caption + 可选参考音频；标准克隆模式 = clone） ----
        /// <summary>clone = 标准克隆模式（可用参考音频克隆音色）；design = 音色设计模式（文字描述音色）；lora = LoRA 模式（加载 LoRA 适配器，不用风格描述）。</summary>
        public string irodori_mode { get; set; } = "clone";
        /// <summary>LoRA 模式的适配器文件夹路径（原样填进服务端的 lora_adapter_raw；空 = 不加载 LoRA）。</summary>
        public string irodori_lora_dir { get; set; } = "";
        /// <summary>音色设计模式的风格描述（官方建议用日语写，例如「落ち着いた女性の声で、囁いてください」）。</summary>
        public string irodori_caption { get; set; } = "";
        /// <summary>无参考音频模式：为 true 时即使选过参考音频也不上传，只靠文本 + Caption 生成。</summary>
        public bool irodori_no_ref { get; set; } = false;
        /// <summary>参考音频：文件名（相对程序目录下的「参考音频」文件夹）或绝对路径；空 = 不用参考音频。</summary>
        public string irodori_ref_wav { get; set; } = "";
        /// <summary>CFG 引导模式：independent（文本 / 描述 / 音色分开调）/ joint（一个全局权重）/ alternating。</summary>
        public string irodori_cfg_mode { get; set; } = "independent";
        /// <summary>CFG：念字准确度（independent / alternating 用）。</summary>
        public double irodori_cfg_text { get; set; } = 3.0;
        /// <summary>CFG：多听话地遵循 Caption 描述（性别 / 风格不对时调高）。</summary>
        public double irodori_cfg_caption { get; set; } = 4.0;
        /// <summary>CFG：既有参考音频又有 Caption 时更偏向谁。</summary>
        public double irodori_cfg_speaker { get; set; } = 5.0;
        /// <summary>joint 模式下的全局 CFG 权重（写进 cfg_scale_raw）。</summary>
        public double irodori_cfg_joint { get; set; } = 4.0;
        /// <summary>时间调度：linear / sway（sway 配合低步数更快）。</summary>
        public string irodori_t_schedule { get; set; } = "sway";
        /// <summary>采样步数（字符串，空 = 服务端默认；配合 sway 建议 6~8）。</summary>
        public string irodori_num_steps { get; set; } = "8";
        /// <summary>sway 系数（-1 = 服务端默认值）。</summary>
        public double irodori_sway_coeff { get; set; } = -1.0;

        // ---- 语音识别（ASR：sherpa-onnx，CPU INT8；原方案 Parakeet 日语，另可选流式 Zipformer 中英） ----
        /// <summary>识别方案：offline = 原 Parakeet 日语整段识别；streaming = Zipformer 中英流式识别。</summary>
        public string asr_engine { get; set; } = "offline";
        /// <summary>离线模型文件夹：里面要有 model.int8.onnx（或 model.onnx）和 tokens.txt。也可以直接填 onnx 文件路径。</summary>
        public string asr_model_dir { get; set; } = "";
        /// <summary>流式模型文件夹：里面要有 encoder/decoder/joiner 三个 onnx 和 tokens.txt。</summary>
        public string asr_stream_model_dir { get; set; } = "";
        /// <summary>CPU 线程数（0 = 自动按核心数算）。</summary>
        public int asr_threads { get; set; } = 0;
        /// <summary>识别完成后是否自动把结果送进翻译。</summary>
        public bool asr_auto_translate { get; set; } = true;

        // ---- 点词查词（Yomitan term_bank 格式词典，例如 Jitendex / JMdict） ----
        /// <summary>词典文件夹：里面放 term_bank_*.json。</summary>
        public string dict_dir { get; set; } = "";
        /// <summary>是否启用点词查词。</summary>
        public bool dict_on { get; set; } = true;
        /// <summary>查到词后自动念一遍（日语发音）。</summary>
        public bool dict_speak { get; set; } = true;
        /// <summary>启动时就建好词典索引（false = 第一次点词时再建，省一份常驻内存）。</summary>
        public bool dict_preload { get; set; } = false;

        // ---- 运行时记忆 ----
        public double orb_x { get; set; } = -1;
        public double orb_y { get; set; } = -1;
        public bool panel_open { get; set; } = false;

        // ================= 语言表 =================
        // 与 call_api.py 的 LANG_NAMES 对齐（官方覆盖 150 种，未在此列出的可用完整语言名）。
        public static readonly List<Lang> LANG_LIST = new List<Lang>
        {
            new Lang("auto",    "自动检测", "AUTO", false),
            new Lang("zh",      "中文",     "中"),
            new Lang("zh-Hant", "繁体中文", "繁"),
            new Lang("en",      "英语",     "EN"),
            new Lang("ja",      "日语",     "日"),
            new Lang("ko",      "韩语",     "한"),
            new Lang("fr",      "法语",     "FR"),
            new Lang("de",      "德语",     "DE"),
            new Lang("es",      "西班牙语", "ES"),
            new Lang("ru",      "俄语",     "RU"),
            new Lang("pt",      "葡萄牙语", "PT"),
            new Lang("it",      "意大利语", "IT"),
            new Lang("nl",      "荷兰语",   "NL"),
            new Lang("pl",      "波兰语",   "PL"),
            new Lang("sv",      "瑞典语",   "SV"),
            new Lang("ro",      "罗马尼亚语", "RO"),
            new Lang("tr",      "土耳其语", "TR"),
            new Lang("ar",      "阿拉伯语", "AR"),
            new Lang("hi",      "印地语",   "HI"),
            new Lang("th",      "泰语",     "TH"),
            new Lang("vi",      "越南语",   "VI"),
            new Lang("id",      "印尼语",   "ID"),
            new Lang("ms",      "马来语",   "MS"),
            new Lang("fil",     "菲律宾语", "FIL"),
            new Lang("ukr_cyrl","乌克兰语", "UK"),
            new Lang("fas_arab","波斯语",   "FA"),
            new Lang("ces_latn","捷克语",   "CS"),
            new Lang("ell_grek","希腊语",   "EL"),
            new Lang("dan_latn","丹麦语",   "DA"),
            new Lang("hun_latn","匈牙利语", "HU"),
            new Lang("fin_latn","芬兰语",   "FI"),
            new Lang("nob_latn","书面挪威语", "NB"),
            new Lang("slk_latn","斯洛伐克语", "SK"),
            new Lang("bul_cyrl","保加利亚语", "BG"),
        };

        /// <summary>官方公网 API 支持的三种规格（都能做图片翻译）。</summary>
        public static readonly string[] MODEL_LIST = {
            "Index-Translate-35B-A3B", "Index-Translate-9B", "Index-Translate-2B",
        };

        public static string ModelLabel(string model)
        {
            if (string.IsNullOrEmpty(model)) return "35B-A3B 旗舰（默认）";
            string m = model.ToUpperInvariant();
            if (m.Contains("2B")) return "2B 轻量（最快）";
            if (m.Contains("9B")) return "9B 均衡（推荐）";
            return "35B-A3B 旗舰（默认）";
        }

        /// <summary>模型名 → 界面上显示的完整标签（设置面板里那行说明用）。</summary>
        public static string ModelFullLabel(string model) => ModelLabel(model) + " · " + (model ?? "");

        /// <summary>短名（35B / 9B / 2B），用于降级链路这种地方。</summary>
        public static string ModelShort(string model)
        {
            string m = (model ?? "").ToUpperInvariant();
            if (m.Contains("2B")) return "2B";
            if (m.Contains("9B")) return "9B";
            return "35B";
        }

        /// <summary>
        /// 降级链路：先试当前模型，再按「低档优先」往下试；低档都不通时兜底回高档（离当前最近的先试）。
        /// 35B → 9B → 2B；9B → 2B → 35B；2B → 9B → 35B。第一项永远是当前模型。
        /// </summary>
        public static List<string> FallbackChain(string primary)
        {
            var chain = new List<string>();
            int start = 0;
            for (int i = 0; i < MODEL_LIST.Length; i++)
                if (string.Equals(MODEL_LIST[i], primary, StringComparison.OrdinalIgnoreCase)) { start = i; break; }
            for (int i = start; i < MODEL_LIST.Length; i++) chain.Add(MODEL_LIST[i]);   // 当前 + 更低档
            for (int i = start - 1; i >= 0; i--) chain.Add(MODEL_LIST[i]);              // 兜底：更高档（由近到远）
            if (chain.Count == 0) chain.Add(MODEL_LIST[0]);
            return chain;
        }

        public static Lang FindLang(string code)
        {
            foreach (var l in LANG_LIST)
                if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
            return LANG_LIST[0];
        }

        public static string LangName(string code)
        {
            return FindLang(code).Name;
        }

        // ================= 读写 =================
        private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string ConfigPath
        {
            get { return Path.Combine(App.BaseDir, "悬浮翻译器.config.json"); }
        }

        public static AppConfig Load()
        {
            AppConfig cfg = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string txt = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    cfg = JsonSerializer.Deserialize<AppConfig>(txt, JsonOpt);
                }
            }
            catch (Exception ex) { App.Log("配置读取失败: " + ex.Message); }
            if (cfg == null) cfg = new AppConfig();

            // 翻译模型：只认官方三档规格，其它取值（含历史配置里的奇怪字符串）一律回落到旗舰
            bool known = false;
            foreach (var m in MODEL_LIST)
                if (string.Equals(m, cfg.model, StringComparison.OrdinalIgnoreCase)) { known = true; cfg.model = m; break; }
            if (!known) cfg.model = MODEL_LIST[0];
            if (cfg.model_fallback_timeout < 3) cfg.model_fallback_timeout = 12;
            if (cfg.model_fallback_timeout > 120) cfg.model_fallback_timeout = 120;
            if (cfg.max_tokens < 64) cfg.max_tokens = 2048;
            if (cfg.chunk_limit < 100) cfg.chunk_limit = 700;
            if (cfg.history_max < 10) cfg.history_max = 100;
            if (cfg.opacity < 0.4 || cfg.opacity > 1.0) cfg.opacity = 1.0;
            if (string.IsNullOrWhiteSpace(cfg.api_base)) cfg.api_base = "https://index-translate.bilibili.com/v1";
            if (cfg.timeout < 5) cfg.timeout = 120;
            if (cfg.auto_delay < 100) cfg.auto_delay = 800;
            if (cfg.instruction_presets == null) cfg.instruction_presets = DefaultPresets();
            // 切分前缀：历史配置里没有这个键（反序列化后是 null），别让朗读路径里出现 null 拼接
            if (cfg.irodori_chunk_prefix == null) cfg.irodori_chunk_prefix = "";
            if (cfg.asr_model_dir == null) cfg.asr_model_dir = "";
            if (cfg.asr_stream_model_dir == null) cfg.asr_stream_model_dir = "";
            if (cfg.asr_engine != "streaming") cfg.asr_engine = "offline";
            if (cfg.dict_dir == null) cfg.dict_dir = "";
            if (cfg.asr_threads < 0) cfg.asr_threads = 0;
            if (cfg.asr_threads > 16) cfg.asr_threads = 16;
            return cfg;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpt), Encoding.UTF8);
            }
            catch (Exception ex) { App.Log("配置保存失败: " + ex.Message); }
        }

        public void ResetToDefault()
        {
            var d = new AppConfig();
            api_base = d.api_base; api_key = d.api_key; timeout = d.timeout;
            model = d.model; model_fallback = d.model_fallback; model_fallback_timeout = d.model_fallback_timeout;
            source = d.source; target = d.target; max_tokens = d.max_tokens;
            temperature = d.temperature; chunk_limit = d.chunk_limit;
            instruction = d.instruction; genre = d.genre; glossary = d.glossary; glossary_on = d.glossary_on;
            instruction_presets = DefaultPresets();
            auto_translate = d.auto_translate; auto_delay = d.auto_delay;
            auto_copy = d.auto_copy; auto_speak = d.auto_speak; speak_rate = d.speak_rate;
            speak_engine = d.speak_engine;
            edge_voice_zh = d.edge_voice_zh; edge_voice_ja = d.edge_voice_ja; edge_voice_en = d.edge_voice_en;
            auto_hide = d.auto_hide;
            hotkey_enabled = d.hotkey_enabled; hotkey_toggle = d.hotkey_toggle;
            hotkey_select = d.hotkey_select; hotkey_source = d.hotkey_source;
            topmost = d.topmost; opacity = d.opacity;
            history_max = d.history_max;
            panel_side = d.panel_side;
            irodori_url = d.irodori_url; irodori_checkpoint = d.irodori_checkpoint;
            irodori_model_device = d.irodori_model_device; irodori_model_precision = d.irodori_model_precision;
            irodori_codec_device = d.irodori_codec_device; irodori_codec_precision = d.irodori_codec_precision;
            irodori_duration_scale = d.irodori_duration_scale;
            irodori_seeds = new List<IrodoriSeed>();
            irodori_seed_choice = "无";
            irodori_chunk_chars = d.irodori_chunk_chars;
            irodori_chunk_prefix = d.irodori_chunk_prefix;
            irodori_mode = d.irodori_mode; irodori_caption = d.irodori_caption;
            irodori_lora_dir = d.irodori_lora_dir;
            irodori_no_ref = d.irodori_no_ref; irodori_ref_wav = d.irodori_ref_wav;
            irodori_cfg_mode = d.irodori_cfg_mode; irodori_cfg_text = d.irodori_cfg_text;
            irodori_cfg_caption = d.irodori_cfg_caption; irodori_cfg_speaker = d.irodori_cfg_speaker;
            irodori_cfg_joint = d.irodori_cfg_joint; irodori_t_schedule = d.irodori_t_schedule;
            irodori_num_steps = d.irodori_num_steps; irodori_sway_coeff = d.irodori_sway_coeff;
            asr_engine = d.asr_engine; asr_model_dir = d.asr_model_dir;
            asr_stream_model_dir = d.asr_stream_model_dir; asr_threads = d.asr_threads;
            asr_auto_translate = d.asr_auto_translate;
            dict_dir = d.dict_dir; dict_on = d.dict_on; dict_preload = d.dict_preload;
            dict_speak = d.dict_speak;
        }
    }

    /// <summary>提示词构造 / 语言检测 / 长文分段（与 call_api.py 保持一致）</summary>
    public static class Prompt
    {
        /// <summary>
        /// instTrans 提示词（对齐官方 call_api.py 的 build_prompt）：
        /// 无约束 → 简单模板；有约束 → 【源文】+【约束要求】结构，术语走【硬性要求】、指令走【注意】。
        /// genre 是文体声明（官方 -d/--genre），默认「文本」。
        /// </summary>
        public static string Build(string text, string targetLang, string sourceLang = "auto",
                                   string instruction = "", string glossary = "", string genre = "")
        {
            string tgt = AppConfig.LangName(targetLang);
            string terms = ParseGlossaryTerms(glossary);
            bool hasConstraint = !string.IsNullOrWhiteSpace(instruction) || terms.Length > 0;

            if (!hasConstraint)
            {
                if (string.IsNullOrWhiteSpace(sourceLang) || sourceLang == "auto")
                    return "请将以下文本翻译为" + tgt + "，直接输出翻译结果，不要进行任何解释。\n\n" + text;
                string src0 = AppConfig.LangName(sourceLang);
                return "请将以下" + src0 + "文本翻译为" + tgt + "，直接输出翻译结果，不要进行任何解释。\n\n" + text;
            }

            string style = string.IsNullOrWhiteSpace(genre) ? "文本" : genre.Trim();
            string src = (string.IsNullOrWhiteSpace(sourceLang) || sourceLang == "auto")
                       ? "" : AppConfig.LangName(sourceLang);

            var sb = new StringBuilder();
            sb.Append("请将以下").Append(src).Append(style).Append("翻译成").Append(tgt)
              .Append("，并且严格遵循所有约束要求。");
            sb.Append("\n\n【源文】\n").Append(text).Append("\n\n【约束要求】\n");

            int n = 1;
            if (terms.Length > 0)
                sb.Append(n++).Append(". 【硬性要求】专名/术语对照: ").Append(terms).Append('\n');

            if (!string.IsNullOrWhiteSpace(instruction))
            {
                foreach (var raw in instruction.Replace("\r", "").Split('\n'))
                {
                    string line = raw.Trim();
                    // 去掉行首编号「1.」「2、」之类，官方也是这么处理的
                    line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*\d+\s*[\.、\)]\s*", "").Trim();
                    if (line.Length == 0) continue;
                    bool tagged = line.StartsWith("【硬性要求】") || line.StartsWith("【注意】") || line.StartsWith("【软性要求】");
                    sb.Append(n++).Append(". ").Append(tagged ? "" : "【注意】").Append(line).Append('\n');
                }
            }

            // 术语表里有「一个术语是另一个的前缀」时（LOL / LOLI）补一条定向约束
            foreach (var note in GlossaryNotes(glossary))
                sb.Append(n++).Append(". 【注意】").Append(note).Append('\n');

            sb.Append('\n');
            if (IsJsonObject(text) && MentionsJson(instruction))
                sb.Append("请以相同的 JSON 格式输出翻译结果，key 保持不变，value 为对应译文。只输出 JSON，不要有任何额外说明。");
            else
                sb.Append("只输出译文，不要有任何额外说明。");
            return sb.ToString();
        }

        /// <summary>
        /// 图片翻译提示词（对齐官方 --image 分支）：文本作为「参考文本/关注内容」一起发过去。
        /// </summary>
        public static string BuildImage(string referenceText, string targetLang, string sourceLang = "auto",
                                        string instruction = "", string glossary = "")
        {
            string tgt = AppConfig.LangName(targetLang);
            string src = (string.IsNullOrWhiteSpace(sourceLang) || sourceLang == "auto")
                       ? "" : AppConfig.LangName(sourceLang);

            bool hasExtra = !string.IsNullOrWhiteSpace(referenceText)
                         || !string.IsNullOrWhiteSpace(instruction)
                         || !string.IsNullOrWhiteSpace(glossary);

            var sb = new StringBuilder();
            sb.Append("请将图片中的").Append(src).Append("文本翻译为").Append(tgt).Append("，直接输出翻译结果，不要进行任何解释。");
            if (hasExtra)
            {
                if (!string.IsNullOrWhiteSpace(referenceText))
                    sb.Append("\n\n【参考文本/关注内容】\n").Append(referenceText.Trim());
                if (!string.IsNullOrWhiteSpace(instruction))
                    sb.Append("\n\n【指令要求】").Append(instruction.Trim());
                string terms = ParseGlossaryTerms(glossary);
                if (terms.Length > 0)
                    sb.Append("\n\n【术语对照】").Append(terms);
                sb.Append("\n\n只输出翻译结果，不要进行任何解释。");
            }
            return sb.ToString();
        }

        /// <summary>源文是不是一个合法的 JSON 对象（用来决定要不要用 JSON 专用收尾语）。</summary>
        private static bool IsJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (!t.StartsWith("{") || !t.EndsWith("}")) return false;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(t))
                    return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
            }
            catch { return false; }
        }

        private static bool MentionsJson(string s)
            => !string.IsNullOrEmpty(s) && s.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// 术语表解析（对齐官方 parse_glossary_terms）：
        /// ①一个 .json 文件路径 ②内联 JSON 字符串 ③分隔串（每行或逗号分隔，分隔符 → -&gt; : ： = ⇥）。
        /// 返回 (原词, 译词) 列表，**长的排前面**：LOL / LOLI 这种前缀关系时，让模型先看到更长的那个。
        /// </summary>
        public static List<KeyValuePair<string, string>> GlossaryPairs(string glossary)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(glossary)) return list;
            string raw = glossary.Trim();

            // ① JSON 文件路径
            try
            {
                if (raw.Length < 260 && raw.IndexOf('\n') < 0 && raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(raw))
                    raw = File.ReadAllText(raw, System.Text.Encoding.UTF8);
            }
            catch (Exception ex) { App.Log("术语表文件读取失败: " + ex.Message); }

            // ② 内联 JSON
            if (raw.TrimStart().StartsWith("{"))
            {
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(raw))
                    {
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var p in doc.RootElement.EnumerateObject())
                            {
                                string v = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                                         ? p.Value.GetString() : p.Value.ToString();
                                if (!string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(v))
                                    list.Add(new KeyValuePair<string, string>(p.Name.Trim(), v.Trim()));
                            }
                        }
                    }
                    if (list.Count > 0) return SortByLengthDesc(list);
                }
                catch (Exception ex) { App.Log("术语表 JSON 解析失败: " + ex.Message); }
            }

            // ③ 分隔串
            foreach (var line0 in raw.Replace("\r", "").Split(new[] { '\n', ',', '，' }))
            {
                string line = line0.Trim();
                if (line.Length == 0) continue;
                string k = null, v = null;
                foreach (var sep in new[] { "->", "=>", "→", ":", "：", "=", "\t" })
                {
                    int i = line.IndexOf(sep, StringComparison.Ordinal);
                    if (i > 0)
                    {
                        k = line.Substring(0, i).Trim();
                        v = line.Substring(i + sep.Length).Trim();
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    list.Add(new KeyValuePair<string, string>(k, v));
            }
            return SortByLengthDesc(list);
        }

        private static List<KeyValuePair<string, string>> SortByLengthDesc(List<KeyValuePair<string, string>> list)
        {
            list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            return list;
        }

        /// <summary>术语表渲染成 `A→B、C→D`（长的在前）。</summary>
        public static string ParseGlossaryTerms(string glossary)
        {
            var parts = new List<string>();
            foreach (var p in GlossaryPairs(glossary)) parts.Add(p.Key + "→" + p.Value);
            return string.Join("、", parts);
        }

        /// <summary>
        /// 术语之间有前缀关系时（LOL 与 LOLI）补一条定向约束。
        /// 实测：只说「LOL→英雄联盟、LOLI→萝莉控」时，源文只有「LOLI」会同时吐出「英雄联盟 / 萝莉控」两个候选；
        /// 加上这条「长术语优先、每个词只输出一个译法」之后只输出「萝莉控」。
        /// </summary>
        public static List<string> GlossaryNotes(string glossary)
        {
            var notes = new List<string>();
            var pairs = GlossaryPairs(glossary);
            var hits = new List<string>();
            for (int i = 0; i < pairs.Count; i++)
            {
                for (int j = 0; j < pairs.Count; j++)
                {
                    if (i == j) continue;
                    string shorter = pairs[i].Key, longer = pairs[j].Key;
                    if (longer.Length <= shorter.Length) continue;
                    if (!longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)) continue;
                    string s = longer + " 不要套用 " + shorter + " 的译法";
                    if (!hits.Contains(s)) hits.Add(s);
                }
            }
            if (hits.Count > 3) hits = hits.GetRange(0, 3);
            if (hits.Count > 0)
                notes.Add("术语按完整词匹配、长术语优先：" + string.Join("；", hits) + "；同一个词只输出一个译法");
            return notes;
        }

        /// <summary>兼容旧调用名。</summary>
        public static string FormatGlossary(string glossary) => ParseGlossaryTerms(glossary);

        /// <summary>
        /// 当前生效的约束概览，用于设置面板实时提示与每次翻译的日志。
        /// 用户报「指令不生效」时先看这行：术语表关掉、指令为空时提示里会直说。
        /// </summary>
        public static string ConstraintSummary(string instruction, string glossary, bool glossaryOn, string genre)
        {
            int ins = 0;
            if (!string.IsNullOrWhiteSpace(instruction))
            {
                foreach (var raw in instruction.Replace("\r", "").Split('\n'))
                    if (raw.Trim().Length > 0) ins++;
            }
            int gl = 0;
            try { gl = GlossaryPairs(glossary).Count; } catch { gl = 0; }
            string g = string.IsNullOrWhiteSpace(genre) ? "无" : genre.Trim();
            return "指令 " + ins + " 条 · 术语 " + (glossaryOn ? gl + " 组" : "未启用") + " · 文体 " + g;
        }

        public static string StripThink(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf("</think", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                int j = s.IndexOf('>', i);
                if (j >= 0) s = s.Substring(j + 1);
            }
            s = s.Replace("<think>", "").Replace("</think>", "");
            return s.TrimStart('\n');
        }

        /// <summary>按字符集粗略检测语言</summary>
        public static string Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "auto";
            int ja = 0, ko = 0, zh = 0, cyr = 0, ar = 0, th = 0, lat = 0, total = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c)) continue;
                total++;
                if ((c >= 0x3040 && c <= 0x30FF)) ja++;                 // 平假名 / 片假名
                else if (c >= 0xAC00 && c <= 0xD7AF) ko++;              // 谚文
                else if (c >= 0x4E00 && c <= 0x9FFF) zh++;              // 汉字（日语里也含汉字，故先算 ja）
                else if (c >= 0x0400 && c <= 0x04FF) cyr++;
                else if (c >= 0x0600 && c <= 0x06FF) ar++;
                else if (c >= 0x0E00 && c <= 0x0E7F) th++;
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c > 0x00C0) lat++;
            }
            if (total == 0) return "auto";
            if (ja > 0) return "ja";
            if (ko > 0) return "ko";
            if (zh > 0) return "zh";
            if (cyr > 0) return "ru";
            if (ar > 0) return "ar";
            if (th > 0) return "th";
            if (lat > 0) return "en";
            return "auto";
        }

        /// <summary>长文按句子边界切分</summary>
        public static List<string> SplitChunks(string text, int limit)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text)) return chunks;
            if (limit < 100) limit = 700;
            if (text.Length <= limit) { chunks.Add(text); return chunks; }

            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int end = i;
                while (end < text.Length && !IsBreak(text[end])) end++;
                if (end < text.Length) end++;
                string piece = text.Substring(i, end - i);
                i = end;
                if (sb.Length > 0 && sb.Length + piece.Length > limit)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }
                if (piece.Length > limit)
                {
                    for (int k = 0; k < piece.Length; k += limit)
                        chunks.Add(piece.Substring(k, Math.Min(limit, piece.Length - k)));
                    continue;
                }
                sb.Append(piece);
            }
            if (sb.Length > 0) chunks.Add(sb.ToString());
            return chunks;
        }

        private static bool IsBreak(char c)
        {
            return c == '\n' || c == '。' || c == '！' || c == '？' || c == '；'
                || c == '.' || c == '!' || c == '?' || c == ';' || c == '…';
        }
    }
}
