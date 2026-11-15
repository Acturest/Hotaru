using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FloatingTranslator
{
    public class HistoryItem
    {
        public string t { get; set; } = "";
        public string s { get; set; } = "";
        public string g { get; set; } = "";
        public string m { get; set; } = "";
        public string input { get; set; } = "";
        public string output { get; set; } = "";

        public string TimeText
        {
            get
            {
                if (string.IsNullOrEmpty(t)) return "";
                return t.Length >= 16 ? t.Substring(5, 11) : t;
            }
        }

        public string LangText
        {
            get { return "[" + AppConfig.LangName(s) + "→" + AppConfig.LangName(g) + "]"; }
        }

        public string Preview
        {
            get
            {
                string one = (input ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (one.Length > 60) one = one.Substring(0, 60) + "…";
                return one;
            }
        }
    }

    public class HistoryStore
    {
        private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public List<HistoryItem> Items { get; private set; } = new List<HistoryItem>();

        public static string PathFile
        {
            get { return Path.Combine(App.BaseDir, "悬浮翻译器.history.json"); }
        }

        public HistoryStore()
        {
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(PathFile))
                {
                    string txt = File.ReadAllText(PathFile, Encoding.UTF8);
                    var list = JsonSerializer.Deserialize<List<HistoryItem>>(txt, JsonOpt);
                    if (list != null) Items = list;
                }
            }
            catch (Exception ex) { App.Log("历史读取失败: " + ex.Message); Items = new List<HistoryItem>(); }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(PathFile, JsonSerializer.Serialize(Items, JsonOpt), Encoding.UTF8);
            }
            catch (Exception ex) { App.Log("历史保存失败: " + ex.Message); }
        }

        public void Add(string input, string output, string src, string tgt)
        {
            try
            {
                string key = (input ?? "").Trim();
                if (key.Length == 0) return;
                Items.RemoveAll(x => (x.input ?? "").Trim() == key && x.g == tgt);
                Items.Insert(0, new HistoryItem
                {
                    t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    s = src,
                    g = tgt,
                    m = App.Config != null ? App.Config.model : "",
                    input = input,
                    output = output,
                });
                int max = App.Config != null ? Math.Max(10, App.Config.history_max) : 100;
                if (Items.Count > max) Items.RemoveRange(max, Items.Count - max);
                Save();
            }
            catch (Exception ex) { App.Log("历史写入失败: " + ex.Message); }
        }

        public void Remove(HistoryItem it)
        {
            Items.Remove(it);
            Save();
        }

        public void Clear()
        {
            Items.Clear();
            Save();
        }

        public List<HistoryItem> Search(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Items.ToList();
            string k = keyword.Trim().ToLowerInvariant();
            return Items.Where(x =>
                (x.input ?? "").ToLowerInvariant().Contains(k) ||
                (x.output ?? "").ToLowerInvariant().Contains(k)).ToList();
        }
    }
}
