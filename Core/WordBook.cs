using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FloatingTranslator
{
    /// <summary>生词本里的一条。</summary>
    public class WordItem
    {
        /// <summary>辞书形（活用已经还原过，例如 食べた → 食べる）。</summary>
        public string term { get; set; } = "";
        public string reading { get; set; } = "";
        public string pos { get; set; } = "";
        /// <summary>释义摘要（取词典第一条的第一小段，够在列表里认出这个词）。</summary>
        public string brief { get; set; } = "";
        /// <summary>收藏时这个词出现的原句。</summary>
        public string sentence { get; set; } = "";
        /// <summary>收藏时间。</summary>
        public string t { get; set; } = "";
    }

    /// <summary>
    /// 生词本：点词查词时按 ☆ 收藏，存成和配置同目录的 JSON。
    /// 只存「足够在列表里认出这个词」的信息，不依赖词典文件在不在。
    /// </summary>
    public class WordBook
    {
        private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public List<WordItem> Items { get; private set; } = new List<WordItem>();

        public static string PathFile
        {
            get { return Path.Combine(App.BaseDir, "悬浮翻译器.生词本.json"); }
        }

        public WordBook()
        {
            Load();
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(PathFile)) return;
                string txt = File.ReadAllText(PathFile, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<WordItem>>(txt, JsonOpt);
                if (list != null) Items = list;
            }
            catch (Exception ex)
            {
                App.Log("生词本读取失败: " + ex.Message);
                Items = new List<WordItem>();
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(PathFile, JsonSerializer.Serialize(Items, JsonOpt), Encoding.UTF8);
            }
            catch (Exception ex) { App.Log("生词本保存失败: " + ex.Message); }
        }

        public bool Contains(string term)
        {
            string k = (term ?? "").Trim();
            if (k.Length == 0) return false;
            return Items.Exists(x => string.Equals(x.term, k, StringComparison.Ordinal));
        }

        public WordItem Find(string term)
        {
            string k = (term ?? "").Trim();
            return Items.Find(x => string.Equals(x.term, k, StringComparison.Ordinal));
        }

        /// <summary>收藏一个词；已经有就只刷新原句和时间，返回 false。</summary>
        public bool Add(string term, string reading, string pos, string definition, string sentence)
        {
            string k = (term ?? "").Trim();
            if (k.Length == 0) return false;

            string brief = FirstLine(definition, 160);
            string tail = Trim(sentence, 300);
            WordItem old = Find(k);
            if (old != null)
            {
                if (brief.Length > 0) old.brief = brief;
                if (tail.Length > 0) old.sentence = tail;
                if (!string.IsNullOrEmpty(reading)) old.reading = reading;
                old.t = Now();
                Save();
                return false;
            }

            Items.Insert(0, new WordItem
            {
                term = k,
                reading = reading ?? "",
                pos = pos ?? "",
                brief = brief,
                sentence = tail,
                t = Now(),
            });
            Save();
            return true;
        }

        public void Remove(WordItem item)
        {
            if (item == null) return;
            Items.Remove(item);
            Save();
        }

        public void Clear()
        {
            Items.Clear();
            Save();
        }

        /// <summary>导出 CSV（Excel 直接能开；带 UTF-8 BOM，免得中文变乱码）。</summary>
        public string ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("词形,读音,词性,释义,原句,收藏时间");
            foreach (WordItem w in Items)
            {
                sb.Append(Csv(w.term)).Append(',')
                  .Append(Csv(w.reading)).Append(',')
                  .Append(Csv(w.pos)).Append(',')
                  .Append(Csv(w.brief)).Append(',')
                  .Append(Csv(w.sentence)).Append(',')
                  .Append(Csv(w.t)).AppendLine();
            }
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            if (s.IndexOfAny(new[] { ',', '"' }) >= 0)
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>
        /// 从完整释义里挑一行当摘要。Jitendex 的释义第一行常常是词性 / 标记（「5-dantransitive」「★」），
        /// 真正的意思在它后面，所以要跳过这些没有空格的短行。
        /// </summary>
        private static string FirstLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (string raw in s.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim().TrimStart('•', '＊', '*', ' ', '　').Trim();
                if (line.Length == 0) continue;
                if (line.Length <= 40 && line.IndexOf(' ') < 0) continue;   // 词性 / 标记行
                return Trim(line, max);
            }
            return Trim(s.Replace("\r", "").Replace("\n", " "), max);
        }

        private static string Trim(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
