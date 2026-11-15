using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FloatingTranslator
{
    /// <summary>一条词典释义（已压平为纯文本）。</summary>
    public sealed class DictEntry
    {
        public string Term = "";
        public string Reading = "";
        public string Pos = "";
        public string Definition = "";
    }

    public sealed class DictResult
    {
        public string Query = "";
        public string BaseForm = "";
        public string Reading = "";
        public List<DictEntry> Entries = new List<DictEntry>();
        public bool Deinflected;
    }

    /// <summary>
    /// 点词查词：直接读取 Yomitan 格式的 term_bank_*.json（Jitendex / JMdict 的 Yomitan 版都行）。
    /// 只依赖 System.Text.Json，不引数据库。
    ///
    /// 分片加载：加载阶段用 Utf8JsonReader 扫一遍所有分片，只记「词形 / 读音 → 该词条在源文件里的
    /// 字节偏移」，不解析释义；真正查某个词时按偏移只读取那一条（RandomAccess.Read），
    /// 所以既不用把 43 万条释义常驻内存，也不会像「整片缓存」那样反复重解析同一文件。
    ///
    /// 支持常见日语活用还原（食べた → 食べる、行った → 行く、読んだ → 読む …）。
    /// </summary>
    public sealed class YomitanDictionary
    {
        /// <summary>一个词条在源文件里的位置（只记位置，不记内容）。</summary>
        private struct Slot
        {
            public int File;      // 分片号
            public int Offset;    // 词条数组 '[' 的字节偏移
            public int Length;    // 含首尾方括号的字节长度
            public int Next;      // 同一个词的其它条目（链表，-1 结束）
        }

        private Slot[] _slots = Array.Empty<Slot>();
        /// <summary>词形 → slot 下标（链头）。</summary>
        private Dictionary<string, int> _termHead = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>读音 → slot 下标（链头）。</summary>
        private Dictionary<string, int> _readingHead = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>index = 分片号，值是 term_bank 文件路径。</summary>
        private string[] _banks = Array.Empty<string>();

        public bool Ready { get; private set; }
        public string LoadedDir { get; private set; } = "";
        public int EntryCount { get; private set; }
        public int FileCount { get; private set; }
        public int ErrorCount { get; private set; }
        public string LastError { get; private set; } = "";
        /// <summary>索引里记了多少条（含词形与读音各记一次）。</summary>
        public int SlotCount { get { return _slots.Length; } }
        /// <summary>加载后索引实际占用的托管内存（字节）。</summary>
        public long MemoryBytes { get; private set; }
        /// <summary>加载耗时（毫秒）。</summary>
        public int LoadMs { get; private set; }

        /// <summary>加载进度（0..1，说明文字）。</summary>
        public event Action<double, string> Progress;

        public Task LoadAsync(string dirOrFile, CancellationToken ct)
        {
            return Task.Run(() => Load(dirOrFile, ct), ct);
        }

        public void Load(string dirOrFile, CancellationToken ct = default(CancellationToken))
        {
            Ready = false;
            LastError = "";
            EntryCount = 0;
            FileCount = 0;
            ErrorCount = 0;
            MemoryBytes = 0;
            LoadMs = 0;
            LoadedDir = (dirOrFile ?? "").Trim();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long memBefore = GC.GetTotalMemory(true);
            try
            {
                string[] files = FindFiles(LoadedDir);
                if (files.Length == 0)
                {
                    LastError = "文件夹里没找到 term_bank_*.json（Yomitan 词典解压后的文件）";
                    Progress?.Invoke(0, LastError);
                    return;
                }
                FileCount = files.Length;

                var termHead = new Dictionary<string, int>(StringComparer.Ordinal);
                var readingHead = new Dictionary<string, int>(StringComparer.Ordinal);
                var slots = new List<Slot>(600000);
                int total = 0;
                for (int fi = 0; fi < files.Length; fi++)
                {
                    ct.ThrowIfCancellationRequested();
                    Progress?.Invoke((double)fi / files.Length, "正在索引 " + Path.GetFileName(files[fi]) + " …");
                    int got = IndexFile(files[fi], fi, slots, termHead, readingHead, ct);
                    total += got;
                    if (got == 0) ErrorCount++;
                }

                _banks = files;
                _slots = slots.ToArray();
                _termHead = termHead;
                _readingHead = readingHead;
                EntryCount = total;
                Ready = total > 0;
                if (!Ready) LastError = "词典文件读不出词条（term_bank 格式不对？）";

                // 索引阶段每行都会读一遍字节 + 分配词形/读音字符串；建完压一次 LOH 把临时内存还给系统。
                TrimMemory();
                MemoryBytes = Math.Max(0, GC.GetTotalMemory(false) - memBefore);
                LoadMs = (int)sw.ElapsedMilliseconds;
                Progress?.Invoke(1, Ready ? "词典已索引：" + total + " 条" : LastError);
                App.Log("词典：索引完成 分片=" + files.Length + " 词条=" + total + " 槽=" + slots.Count
                        + " 解析失败=" + ErrorCount + " 索引内存=" + (MemoryBytes / 1048576)
                        + "MB 耗时=" + LoadMs + "ms 目录=" + LoadedDir);
            }
            catch (OperationCanceledException)
            {
                LastError = "已取消";
                Progress?.Invoke(0, "词典加载已取消");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                App.Log("词典：加载失败 " + ex);
                Progress?.Invoke(0, "词典加载失败：" + ex.Message);
            }
        }

        /// <summary>压一次 LOH，把索引阶段留下的临时内存还给系统（索引本身不动）。</summary>
        public static void TrimMemory()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, false);
            }
            catch (Exception) { }
        }

        private static string[] FindFiles(string dirOrFile)
        {
            if (string.IsNullOrWhiteSpace(dirOrFile)) return Array.Empty<string>();
            if (File.Exists(dirOrFile)) return new[] { dirOrFile };
            if (!Directory.Exists(dirOrFile)) return Array.Empty<string>();
            try
            {
                var files = Directory.GetFiles(dirOrFile, "term_bank_*.json", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                return files;
            }
            catch (Exception ex)
            {
                App.Log("词典：扫描文件夹失败 " + ex.Message);
                return Array.Empty<string>();
            }
        }

        // ==================== 索引 ====================

        /// <summary>
        /// 扫一个 term_bank 文件，把每个词条的「词形 / 读音 → 字节位置」记下来。
        /// term_bank 有两种写法：每行一个词条数组（JSONL），或整个文件是一个大数组（Jindetx 官方发布）。
        /// Utf8JsonReader 两种都能顺着读，靠 CurrentDepth 区分「词条本身」和「装词条的外层数组」。
        /// </summary>
        private int IndexFile(string path, int bank, List<Slot> slots,
                              Dictionary<string, int> termHead, Dictionary<string, int> readingHead,
                              CancellationToken ct)
        {
            byte[] buf;
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > 200L * 1024 * 1024) return 0;
                buf = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                ErrorCount++;
                App.Log("词典：读取失败 " + Path.GetFileName(path) + " " + ex.Message);
                return 0;
            }

            var opt = new JsonReaderOptions
            {
                AllowMultipleValues = true,            // JSONL：一个文件里连续多个顶层数组
                CommentHandling = JsonCommentHandling.Skip,
            };

            // 先探一下是不是「大数组套小数组」：第一个元素又是数组的话，词条在深度 1，否则在深度 0。
            int entryDepth;
            try
            {
                var probe = new Utf8JsonReader(buf, opt);
                if (!probe.Read() || probe.TokenType != JsonTokenType.StartArray) return 0;
                if (!probe.Read()) return 0;
                entryDepth = probe.TokenType == JsonTokenType.StartArray ? 1 : 0;
            }
            catch (JsonException)
            {
                ErrorCount++;
                App.Log("词典：格式不对 " + Path.GetFileName(path));
                return 0;
            }

            int n = 0;
            try
            {
                var r = new Utf8JsonReader(buf, opt);
                while (r.Read())
                {
                    if ((n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                    if (r.TokenType != JsonTokenType.StartArray) continue;
                    if (r.CurrentDepth != entryDepth) continue;

                    long start = r.TokenStartIndex;
                    string term = "";
                    string reading = "";
                    // 词条形如 [term, reading, pos, ..., glossary]，只取前两个
                    if (r.Read() && r.TokenType == JsonTokenType.String) term = r.GetString() ?? "";
                    if (r.Read() && r.TokenType == JsonTokenType.String) reading = r.GetString() ?? "";

                    // 走到词条的 ']' 拿结束位置。这里不能用 Skip()：它只跳过「容器」的子元素，
                    // 而此刻已经停在第二个字符串上了，Skip 等价于再 Read 一次，位置会落在词条中间。
                    int depth = 1;
                    long end = -1;
                    while (depth > 0)
                    {
                        if (!r.Read()) break;
                        switch (r.TokenType)
                        {
                            case JsonTokenType.StartArray:
                            case JsonTokenType.StartObject:
                                depth++;
                                break;
                            case JsonTokenType.EndArray:
                            case JsonTokenType.EndObject:
                                depth--;
                                if (depth == 0) end = r.TokenStartIndex;   // ']' 自身的字节位置
                                break;
                        }
                    }
                    if (end <= start) continue;

                    if (term.Length == 0 || term.Length > 60) continue;
                    int offset = (int)start;
                    int length = (int)(end + 1 - start);
                    AddSlot(slots, termHead, term, bank, offset, length);
                    if (reading.Length > 0 && reading != term)
                        AddSlot(slots, readingHead, reading, bank, offset, length);
                    n++;
                }
            }
            catch (JsonException ex)
            {
                ErrorCount++;
                App.Log("词典：索引中断 " + Path.GetFileName(path) + " 第 " + n + " 条起 " + ex.Message);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                ErrorCount++;
                App.Log("词典：索引失败 " + Path.GetFileName(path) + " " + ex.Message);
            }
            return n;
        }

        /// <summary>同一个词最多挂 20 条（够显示，也防止个别派生词把链拉太长）。</summary>
        private const int MaxSlotsPerKey = 20;

        private static void AddSlot(List<Slot> slots, Dictionary<string, int> map, string key,
                                    int bank, int offset, int length)
        {
            int head;
            if (map.TryGetValue(key, out head))
            {
                int cur = head, guard = 0;
                while (cur >= 0 && guard++ < MaxSlotsPerKey)
                {
                    if (slots[cur].File == bank && slots[cur].Offset == offset) return;   // 已记过
                    cur = slots[cur].Next;
                }
                if (guard >= MaxSlotsPerKey) return;
                slots.Add(new Slot { File = bank, Offset = offset, Length = length, Next = head });
                map[key] = slots.Count - 1;
            }
            else
            {
                slots.Add(new Slot { File = bank, Offset = offset, Length = length, Next = -1 });
                map[key] = slots.Count - 1;
            }
        }

        // ==================== 按偏移读取单条 ====================

        /// <summary>按索引里的位置只读那一条词条，再解析成 DictEntry。读不到就返回 null。</summary>
        private DictEntry ReadSlot(Slot s)
        {
            if (s.File < 0 || s.File >= _banks.Length || s.Length <= 0) return null;
            try
            {
                var buf = new byte[s.Length];
                using (var handle = File.OpenHandle(_banks[s.File], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int got = RandomAccess.Read(handle, new Span<byte>(buf, read, buf.Length - read), s.Offset + read);
                        if (got <= 0) break;
                        read += got;
                    }
                    if (read != buf.Length) return null;
                }
                using (var doc = JsonDocument.Parse(buf))
                    return ParseEntryObject(doc.RootElement);
            }
            catch (Exception ex)
            {
                App.Log("词典：读取词条失败 " + Path.GetFileName(_banks[s.File]) + "@" + s.Offset + " " + ex.Message);
                return null;
            }
        }

        /// <summary>顺着链表把同一个词的所有条目都取出来。</summary>
        private List<DictEntry> Collect(Dictionary<string, int> map, string key)
        {
            int idx;
            if (!map.TryGetValue(key, out idx)) return null;
            var list = new List<DictEntry>(1);
            int guard = 0;
            while (idx >= 0 && guard++ < MaxSlotsPerKey)
            {
                Slot s = _slots[idx];
                DictEntry e = ReadSlot(s);
                if (e != null) list.Add(e);
                idx = s.Next;
            }
            return list;
        }

        private static DictEntry ParseEntryObject(JsonElement a)
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 6) return null;
            string term = Str(a[0]);
            if (term.Length == 0 || term.Length > 60) return null;
            string reading = Str(a[1]);
            string pos = TagsText(a[2]);
            string def = GlossaryText(a[5]);
            if (def.Length == 0) return null;
            return new DictEntry { Term = term, Reading = reading, Pos = pos, Definition = def };
        }

        private static string Str(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
            return "";
        }

        private static string TagsText(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
            if (el.ValueKind != JsonValueKind.Array) return "";
            var parts = new List<string>();
            foreach (var x in el.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) parts.Add(x.GetString() ?? "");
            return string.Join("、", parts.Where(s => s.Length > 0).Take(8));
        }

        private static string GlossaryText(JsonElement el)
        {
            var sb = new StringBuilder();
            if (el.ValueKind == JsonValueKind.Array)
            {
                bool first = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!first) sb.Append('\n');
                    first = false;
                    AppendGloss(item, sb);
                    if (sb.Length > 1600) break;
                }
            }
            else AppendGloss(el, sb);
            string s = sb.ToString().Replace("\r", "");
            s = Regex.Replace(s, @"[ \t\u00A0]+", " ");
            s = Regex.Replace(s, @" *\n *", "\n");
            s = Regex.Replace(s, @"\n{2,}", "\n").Trim();
            if (s.Length > 1600) s = s.Substring(0, 1600) + "…";
            return s;
        }

        private static void AppendGloss(JsonElement el, StringBuilder sb)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    sb.Append(el.GetString());
                    return;
                case JsonValueKind.Array:
                    foreach (var x in el.EnumerateArray()) AppendGloss(x, sb);
                    return;
                case JsonValueKind.Object:
                    string tag = "";
                    JsonElement t;
                    if (el.TryGetProperty("tag", out t) && t.ValueKind == JsonValueKind.String) tag = t.GetString() ?? "";
                    else if (el.TryGetProperty("type", out t) && t.ValueKind == JsonValueKind.String) tag = t.GetString() ?? "";
                    if (tag == "br") { sb.Append('\n'); return; }
                    if (tag == "li") { if (sb.Length > 0) sb.Append('\n'); sb.Append("• "); }
                    else if (tag == "div" || tag == "p" || tag == "ul" || tag == "ol" || tag == "tr")
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    }
                    else if (tag == "rt" || tag == "rp") return;   // 注音不重复显示
                    JsonElement content;
                    if (el.TryGetProperty("content", out content)) AppendGloss(content, sb);
                    else if (el.TryGetProperty("data", out content)) AppendGloss(content, sb);
                    else if (el.TryGetProperty("text", out content)) AppendGloss(content, sb);
                    if (tag == "div" || tag == "p" || tag == "ul" || tag == "ol" || tag == "tr" || tag == "li")
                        sb.Append('\n');
                    return;
            }
        }

        /// <summary>查一个词：精确 → 读音 → 活用还原。</summary>
        public DictResult Lookup(string input)
        {
            if (!Ready || string.IsNullOrWhiteSpace(input)) return null;
            string q = Normalize(input);
            if (q.Length == 0) return null;

            var searched = new HashSet<string>(StringComparer.Ordinal);
            foreach (string cand in Deinflect(q))
            {
                if (!searched.Add(cand)) continue;

                List<DictEntry> list = Collect(_termHead, cand);
                if (list != null && list.Count > 0)
                    return BuildResult(q, cand, list, cand != q);

                list = Collect(_readingHead, cand);
                if (list != null && list.Count > 0)
                    return BuildResult(q, cand, list, cand != q);
            }
            return null;
        }

        private static DictResult BuildResult(string query, string baseForm, List<DictEntry> list, bool deinflected)
        {
            var r = new DictResult { Query = query, BaseForm = baseForm, Deinflected = deinflected };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in list)
            {
                string key = e.Term + "\u0001" + e.Reading + "\u0001" + e.Definition;
                if (!seen.Add(key)) continue;
                r.Entries.Add(e);
                if (r.Reading.Length == 0) r.Reading = e.Reading;
                if (r.Entries.Count >= 6) break;
            }
            return r;
        }

        private static string Normalize(string s)
        {
            s = (s ?? "").Trim();
            s = s.Trim('「', '」', '『', '』', '（', '）', '(', ')', '[', ']', '【', '】',
                       '。', '、', '，', '．', '！', '？', '!', '?', '…', '・', '：', ':', '；', ';', '"', '\'');
            s = s.Replace(" ", "").Replace("\u3000", "").Replace("\n", "").Replace("\t", "");
            return s;
        }

        // ==================== 活用还原 ====================
        // 目标：食べた→食べる、行った→行く、読んだ→読む、話した→話す、待った→待つ、
        //       食べない→食べる、行きます→行く、食べれば→食べる、行こう→行く 等。
        private static readonly Dictionary<char, char> A2U = new Dictionary<char, char> {
            {'あ','う'},{'か','く'},{'が','ぐ'},{'さ','す'},{'た','つ'},{'な','ぬ'},
            {'は','う'},{'ば','ぶ'},{'ま','む'},{'や','う'},{'ら','る'},{'わ','う'},
        };
        private static readonly Dictionary<char, char> I2U = new Dictionary<char, char> {
            {'い','う'},{'き','く'},{'ぎ','ぐ'},{'し','す'},{'ち','つ'},{'に','ぬ'},
            {'ひ','う'},{'び','ぶ'},{'み','む'},{'り','る'},
        };
        private static readonly Dictionary<char, char> E2U = new Dictionary<char, char> {
            {'え','う'},{'け','く'},{'げ','ぐ'},{'せ','す'},{'て','つ'},{'ね','ぬ'},
            {'へ','う'},{'べ','ぶ'},{'め','む'},{'れ','る'},
        };
        private static readonly Dictionary<char, char> O2U = new Dictionary<char, char> {
            {'お','う'},{'こ','く'},{'ご','ぐ'},{'そ','す'},{'と','つ'},{'の','ぬ'},
            {'ほ','う'},{'ぼ','ぶ'},{'も','む'},{'よ','う'},{'ろ','る'},{'を','う'},
        };

        private struct Rule
        {
            public string Suffix;
            public string[] Add;
            public bool A, I, E, O, Ru;
            public Rule(string suffix, string[] add, bool a = false, bool i = false, bool e = false, bool o = false, bool ru = false)
            { Suffix = suffix; Add = add; A = a; I = i; E = e; O = o; Ru = ru; }
        }

        private static readonly Rule[] Rules =
        {
            new Rule("ませんでした", new string[0], i:true, e:true, ru:true),
            new Rule("たくなかった", new string[0], ru:true),
            new Rule("くなかった", new[]{"い"}),
            new Rule("ません", new string[0], i:true, e:true, ru:true),
            new Rule("ました", new string[0], i:true, e:true, ru:true),
            new Rule("なかった", new string[0], a:true, ru:true),
            new Rule("たかった", new string[0], i:true, ru:true),
            new Rule("かったら", new[]{"い"}),
            new Rule("かった", new[]{"い"}),
            new Rule("させる", new string[0], a:true, e:true, ru:true),
            new Rule("られる", new string[0], a:true, e:true, ru:true),
            new Rule("ったら", new[]{"う","つ","る","く"}),
            new Rule("った", new[]{"く","う","つ","る"}),
            new Rule("って", new[]{"う","つ","る","く"}),
            new Rule("んだ", new[]{"ぬ","ぶ","む"}),
            new Rule("んで", new[]{"ぬ","ぶ","む"}),
            new Rule("いた", new[]{"く"}),
            new Rule("いて", new[]{"く"}),
            new Rule("いだ", new[]{"ぐ"}),
            new Rule("いで", new[]{"ぐ"}),
            new Rule("した", new[]{"す","する"}),
            new Rule("して", new[]{"す","する"}),
            new Rule("します", new[]{"する"}),
            new Rule("しない", new[]{"する"}),
            new Rule("しなかった", new[]{"する"}),
            new Rule("たら", new string[0], e:true, ru:true),
            new Rule("れば", new string[0], e:true, ru:true),
            new Rule("ます", new string[0], i:true, e:true, ru:true),
            new Rule("ない", new string[0], a:true, ru:true),
            new Rule("れる", new string[0], a:true, e:true, ru:true),
            new Rule("せる", new string[0], a:true, e:true, ru:true),
            new Rule("よう", new string[0], o:true, ru:true),
            new Rule("たい", new string[0], i:true, ru:true),
            new Rule("た", new string[0], ru:true),
            new Rule("だ", new string[0], ru:true),
            new Rule("て", new string[0], e:true, ru:true),
            new Rule("で", new string[0], e:true, ru:true),
            new Rule("ば", new string[0], e:true, ru:true),
            new Rule("う", new string[0], o:true),
            new Rule("ろ", new string[0], e:true, ru:true),
            new Rule("く", new[]{"い"}),
            new Rule("さ", new[]{"い"}),
        };

        public static List<string> Deinflect(string word)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<KeyValuePair<string, int>>();
            AddWord(word, 0, results, seen, queue);

            // 只做两层还原，最多 64 个候选：点击查词时要在 100 次以内查完，不能爆
            while (queue.Count > 0 && results.Count < 64)
            {
                var cur = queue.Dequeue();
                if (cur.Value >= 2) continue;
                foreach (string c in Transform(cur.Key))
                    AddWord(c, cur.Value + 1, results, seen, queue);
            }
            return results;
        }

        private static void AddWord(string w, int depth, List<string> results, HashSet<string> seen,
                                    Queue<KeyValuePair<string, int>> queue)
        {
            if (string.IsNullOrEmpty(w) || w.Length > 40) return;
            if (!seen.Add(w)) return;
            results.Add(w);
            queue.Enqueue(new KeyValuePair<string, int>(w, depth));
        }

        private static IEnumerable<string> Transform(string w)
        {
            foreach (var r in Rules)
            {
                if (!w.EndsWith(r.Suffix, StringComparison.Ordinal)) continue;
                string stem = w.Substring(0, w.Length - r.Suffix.Length);
                if (stem.Length == 0) continue;
                foreach (var add in r.Add)
                    yield return stem + add;
                if (r.Ru) yield return stem + "る";
                if (r.A) yield return MapLast(stem, A2U);
                if (r.I) yield return MapLast(stem, I2U);
                if (r.E) yield return MapLast(stem, E2U);
                if (r.O) yield return MapLast(stem, O2U);
            }
        }

        private static string MapLast(string s, Dictionary<char, char> map)
        {
            if (s.Length == 0) return s;
            char last = s[s.Length - 1];
            char mapped;
            if (map.TryGetValue(last, out mapped))
                return s.Substring(0, s.Length - 1) + mapped;
            return s;
        }
    }
}
