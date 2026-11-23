using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FloatingTranslator
{
    public class PopupItem
    {
        public string Value = "";
        public string Text = "";
        public string Badge = "";
        public bool Separator = false;
    }

    public partial class MainWindow
    {
        /// <summary>设置面板里的一栏（标签 + 该栏自己的内容面板）。原来所有设置挤在一条长列表里，现在按栏拆开。</summary>
        private sealed class SettingsTab
        {
            public string Key = "";
            public StackPanel Panel;
            public TextBlock Text;
            public Border Underline;
        }

        private readonly List<SettingsTab> _settingsTabs = new List<SettingsTab>();
        private StackPanel _buildingTab;              // 正在往里加控件的分栏
        private string _settingsTabKey = "translate"; // 当前显示的分栏

        /// <summary>构建期往哪个分栏里塞控件（原来叫 SettingsStack，为了少改动保留这个名字）。</summary>
        private StackPanel SettingsStack { get { return _buildingTab; } }

        private readonly List<Action> _savers = new List<Action>();
        private FrameworkElement _dropdownAnchor;
        private bool _settingsBuilt;
        private Button _irSeedBtn;   // 「随机种子」下拉按钮（--demo seed 用它展开列表）
        private TextBlock _constraintSummary;   // 「当前生效的约束」提示行
        private TextBlock _edgeVoiceHint;       // 朗读音色提示行（选完立刻反映当前音色）
        private TextBlock _fallbackHint;        // 「模型降级链路」提示行
        private TextBox _fIns;                  // 「翻译指令」输入框（--demo inst 用它验证即时生效）
        private WrapPanel _presetBar;           // 「指令预设」按钮条（--demo preset 用它点预设）
        private string _activePreset = "";      // 最近一次点过/存过的预设名（「删除预设」没填名字时用它）

        // ---- Irodori 音色设计模式 ----
        private Button _tabCloneBtn, _tabDesignBtn, _tabLoraBtn;
        private readonly List<FrameworkElement> _irDesignRows = new List<FrameworkElement>();  // 只在「音色设计模式」下显示的行（Caption）
        private readonly List<FrameworkElement> _irSamplingRows = new List<FrameworkElement>(); // 「音色设计 / LoRA 模式」共用的采样 / CFG 行
        private readonly List<FrameworkElement> _irLoraRows = new List<FrameworkElement>();     // 只在「LoRA 模式」下显示的行
        private readonly List<FrameworkElement> _irIndepRows = new List<FrameworkElement>();   // CFG 分开调时的三个滑块
        private FrameworkElement _irJointRow;                                                   // CFG joint 时的全局滑块
        private TextBlock _irCaptionWarn;   // Caption 语言提醒
        private TextBox _fIrCap;            // Caption 输入框（--demo ir-live 用它证明即时生效）
        private TextBox _fIrLora;           // LoRA 适配器文件夹路径
        private TextBlock _irLoraHint;      // LoRA 路径提示
        private CheckBox _irNoRef;          // 无参考音频
        private TextBlock _irRefName;       // 参考音频文件名
        private TextBlock _irRefHint;       // 参考音频提示
        private DispatcherTimer _liveSaveTimer;   // 动态生效：写配置文件的防抖定时器

        // ============================ 弹出列表 ============================
        private Border PopupRow(PopupItem it, bool current, Action pick)
        {
            var bd = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Background = current ? new SolidColorBrush(Color.FromArgb(0x14, 0x5E, 0xEA, 0xD4)) : Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new TextBlock
            {
                Text = it.Badge,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("DimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock
            {
                Text = it.Text,
                FontSize = 12.5,
                Foreground = (Brush)FindResource(current ? "AccentBrush" : "TxtBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tick = new TextBlock
            {
                Text = current ? "✓" : "",
                FontSize = 12,
                Foreground = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(badge, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(tick, 2);
            grid.Children.Add(badge);
            grid.Children.Add(name);
            grid.Children.Add(tick);
            bd.Child = grid;

            bd.MouseEnter += (s, e) =>
            {
                if (!current) bd.Background = new SolidColorBrush(Color.FromArgb(0x14, 0x5E, 0xEA, 0xD4));
                name.Foreground = (Brush)FindResource("AccentBrush");
            };
            bd.MouseLeave += (s, e) =>
            {
                if (!current) bd.Background = Brushes.Transparent;
                if (!current) name.Foreground = (Brush)FindResource("TxtBrush");
            };
            bd.MouseLeftButtonUp += (s, e) => { e.Handled = true; CloseDropdown(); pick(); };
            return bd;
        }

        private void ShowPopupList(FrameworkElement anchor, List<PopupItem> items, string current,
                                   Action<string> onPick, double width = 180)
        {
            CloseDropdown();
            LangList.Children.Clear();
            foreach (var it in items)
            {
                if (it.Separator)
                {
                    LangList.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)FindResource("DividerBrush"),
                        Margin = new Thickness(6, 5, 6, 5),
                    });
                    continue;
                }
                LangList.Children.Add(PopupRow(it, it.Value == current, () => onPick(it.Value)));
            }
            LangDropdown.Width = width;
            // 设置面板也是 Root 里的 Canvas 子元素，且在 XAML 里排在 LangDropdown 之后 → 必须把下拉提到最上层，否则被面板盖住看不见
            Panel.SetZIndex(LangDropdown, 1000);
            _dropdownAnchor = anchor;
            LangDropdown.Visibility = Visibility.Visible;
            LangDropdown.UpdateLayout();
            AnimY(DropdownShift, 0, 200);
            ShowEl(LangDropdown, 200);
            PositionDropdown();
        }

        private void CloseDropdown()
        {
            if (LangDropdown.Visibility == Visibility.Visible)
            {
                LangDropdown.Visibility = Visibility.Collapsed;
                LangDropdown.BeginAnimation(OpacityProperty, null);
                LangDropdown.Opacity = 0;
            }
        }

        private void PositionDropdown()
        {
            if (LangDropdown.Visibility != Visibility.Visible) return;
            FrameworkElement anchor = _dropdownAnchor ?? LangBtn;
            double ddW = LangDropdown.Width;
            double ddH = CardHeight(LangDropdown);
            Point p = anchor.TranslatePoint(new Point(0, 0), Root);
            double lx = p.X + anchor.ActualWidth / 2 - ddW / 2;
            double ly = p.Y + anchor.ActualHeight + 8;
            if (ly + ddH > Height - MARGIN) ly = p.Y - 8 - ddH;
            if (ly < MARGIN) ly = MARGIN;
            if (lx + ddW > Width - MARGIN) lx = Width - MARGIN - ddW;
            if (lx < MARGIN) lx = MARGIN;
            Canvas.SetLeft(LangDropdown, lx);
            Canvas.SetTop(LangDropdown, ly);
        }

        private List<PopupItem> LangItems(bool includeAuto)
        {
            var list = new List<PopupItem>();
            foreach (var l in AppConfig.LANG_LIST)
            {
                if (!includeAuto && !l.CanBeTarget) continue;
                list.Add(new PopupItem { Value = l.Code, Text = l.Name, Badge = l.Badge });
            }
            return list;
        }

        private void LangBtn_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (LangDropdown.Visibility == Visibility.Visible) { CloseDropdown(); return; }
            ShowPopupList(LangBtn, LangItems(false), _cfg.target, code =>
            {
                _cfg.target = code;
                _cfg.Save();
                UpdateIrodoriBtn();
                _sameLangResult = false;
                HideCnRow();
                CnBtn.Visibility = Visibility.Collapsed;
                Toast("目标语言：" + AppConfig.LangName(code));
                if (InputBox.Text.Length > 0 && !_busy) _ = TranslateAsync();
            });
        }

        // ============================ 设置面板 ============================
        private void SettingsBtn_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (SettingsPanel.Visibility == Visibility.Visible) CloseSettingsPanel(); else OpenSettingsPanel();
        }

        private void OpenSettingsPanel()
        {
            BuildSettingsValues();
            // 自动化截图用：FT_TAB=translate|auto|voice|sub|ui 直接打开某一栏
            string tab = Environment.GetEnvironmentVariable("FT_TAB");
            if (!string.IsNullOrWhiteSpace(tab)) SwitchSettingsTab(tab.Trim());
            SettingsPanel.Visibility = Visibility.Visible;
            SettingsPanel.UpdateLayout();
            AnimScale(SettingsScale, 1, 240);
            ShowEl(SettingsPanel, 240);
            PositionSettingsPanel();
        }

        private void CloseSettingsPanel()
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsPanel.BeginAnimation(OpacityProperty, null);
                SettingsPanel.Opacity = 0;
            }
            CloseDropdown();
        }

        private TextBlock SectionTitle(string text)
        {
            var t = new TextBlock { Text = text, Style = (Style)FindResource("SectionTitle") };
            return t;
        }

        // ============================ 分栏标签 ============================
        // 设置项按「翻译 / 自动化 / 语音 / 字幕 / 界面」分五栏；每栏是一个独立的 StackPanel，
        // 切栏只是换 ScrollViewer 的内容，不重建控件（控件状态、焦点都还在）。
        private static readonly string[][] SettingsTabDefs =
        {
            new[] { "translate", "翻译" },
            new[] { "auto", "自动化" },
            new[] { "voice", "语音" },
            new[] { "sub", "字幕" },
            new[] { "ui", "界面" },
        };

        private void BuildSettingsTabs()
        {
            SettingsTabs.Children.Clear();
            _settingsTabs.Clear();
            for (int i = 0; i < SettingsTabDefs.Length; i++)
            {
                string key = SettingsTabDefs[i][0], title = SettingsTabDefs[i][1];
                var sp = new StackPanel
                {
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    Margin = new Thickness(1, 0, 1, 0),
                };
                var txt = new TextBlock
                {
                    Text = title,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)FindResource("DimBrush"),
                    Margin = new Thickness(0, 2, 0, 4),
                };
                var line = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = Brushes.Transparent };
                sp.Children.Add(txt);
                sp.Children.Add(line);
                sp.MouseLeftButtonUp += (s, e) => { e.Handled = true; SwitchSettingsTab(key); };
                sp.MouseEnter += (s, e) => { if (key != _settingsTabKey) txt.Foreground = (Brush)FindResource("TxtBrush"); };
                sp.MouseLeave += (s, e) => { if (key != _settingsTabKey) txt.Foreground = (Brush)FindResource("DimBrush"); };
                Grid.SetColumn(sp, i);
                SettingsTabs.Children.Add(sp);
                _settingsTabs.Add(new SettingsTab { Key = key, Panel = new StackPanel(), Text = txt, Underline = line });
            }
        }

        private void SwitchSettingsTab(string key)
        {
            SettingsTab hit = null;
            foreach (var t in _settingsTabs) if (t.Key == key) hit = t;
            if (hit == null && _settingsTabs.Count > 0) hit = _settingsTabs[0];
            if (hit == null) return;
            _settingsTabKey = hit.Key;
            _buildingTab = hit.Panel;
            foreach (var t in _settingsTabs)
            {
                bool on = ReferenceEquals(t, hit);
                t.Text.Foreground = (Brush)FindResource(on ? "AccentBrush" : "DimBrush");
                t.Underline.Background = on ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
            }
            SettingsScroll.Content = hit.Panel;
            SettingsScroll.ScrollToTop();
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                SettingsPanel.UpdateLayout();
                PositionSettingsPanel();
            }
        }

        /// <summary>构建期切到某一栏：之后的 SettingsStack.Children.Add 都落到这一栏里。</summary>
        private void BuildIntoTab(string key)
        {
            foreach (var t in _settingsTabs)
                if (t.Key == key) { _buildingTab = t.Panel; return; }
        }

        private FrameworkElement RowSide(string label, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var t = new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("RowLabel"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(t, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(t);
            g.Children.Add(control);
            return g;
        }

        private FrameworkElement RowFull(string label, FrameworkElement control)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("RowLabel"), Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(control);
            return sp;
        }

        /// <summary>一行短说明：面板上只留要点，完整解释放 ToolTip（鼠标停上去才看）。</summary>
        private TextBlock Hint(string text, string tip = null)
        {
            var t = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("HintText"),
                Margin = new Thickness(0, 1, 0, 4),
            };
            if (!string.IsNullOrEmpty(tip)) t.ToolTip = tip;
            return t;
        }

        private TextBox Field(string value, bool multiline = false)
        {
            var tb = new TextBox
            {
                Style = (Style)FindResource("FieldBox"),
                Text = value ?? "",
            };
            if (multiline)
            {
                tb.Height = 58;
                tb.TextWrapping = TextWrapping.Wrap;
                tb.AcceptsReturn = true;
                tb.VerticalContentAlignment = VerticalAlignment.Top;
                tb.Padding = new Thickness(8, 5, 8, 5);
                tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            return tb;
        }

        private CheckBox Check(string text, bool value)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = value,
                Style = (Style)FindResource("GlassCheck"),
                Margin = new Thickness(0, 4, 0, 4),
            };
        }

        private Slider Slider(double min, double max, double value, double tick)
        {
            return new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                SmallChange = tick,
                LargeChange = tick * 2,
                Style = (Style)FindResource("GlassSlider"),
                Margin = new Thickness(0, 4, 0, 4),
            };
        }

        /// <summary>可点击的下拉选择行</summary>
        private Button SelectButton(string value)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("TextBtn"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9, 4, 9, 4),
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new TextBlock { Text = value, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var a = new TextBlock { Text = "▾", FontSize = 10, Foreground = (Brush)FindResource("DimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(t, 0);
            Grid.SetColumn(a, 1);
            g.Children.Add(t);
            g.Children.Add(a);
            btn.Content = g;
            btn.Tag = t;
            return btn;
        }

        private void SetSelectText(Button btn, string text)
        {
            var t = btn.Tag as TextBlock;
            if (t != null) t.Text = text;
        }

        private Button SelectRow(string label, string current, Func<List<PopupItem>> items, Action<string> pick)
        {
            var btn = SelectButton(current);
            btn.Click += (s, e) =>
            {
                if (LangDropdown.Visibility == Visibility.Visible && _dropdownAnchor == btn) { CloseDropdown(); return; }
                ShowPopupList(btn, items(), _selectValues[btn], v =>
                {
                    SetSelectText(btn, LabelOf(items(), v));
                    _selectValues[btn] = v;
                    pick(v);
                }, 190);
            };
            _selectValues[btn] = current;
            SettingsStack.Children.Add(RowSide(label, btn));   // 加进面板（之前这行漏了，下拉行整个不显示）
            _selectRefreshers.Add(() => SetSelectText(btn, LabelOf(items(), _selectValues[btn])));
            return btn;
        }

        private readonly Dictionary<Button, string> _selectValues = new Dictionary<Button, string>();
        private readonly List<Action> _selectRefreshers = new List<Action>();

        private static string LabelOf(List<PopupItem> items, string value)
        {
            foreach (var it in items)
                if (it.Value == value) return it.Text;
            return value;
        }

        // ---- Edge 音色清单（常用神经音色；清单外的可以在配置文件里直接改） ----
        private static readonly string[][] ZhVoices =
        {
            new[] { "zh-CN-XiaoxiaoNeural", "晓晓 · 女 · 温柔（默认）" },
            new[] { "zh-CN-XiaoyiNeural",   "晓伊 · 女 · 活泼" },
            new[] { "zh-CN-YunxiNeural",    "云希 · 男 · 少年" },
            new[] { "zh-CN-YunjianNeural",  "云健 · 男 · 解说" },
            new[] { "zh-CN-YunyangNeural",  "云扬 · 男 · 新闻" },
            new[] { "zh-CN-YunxiaNeural",   "云夏 · 男 · 童声" },
            new[] { "zh-CN-liaoning-XiaobeiNeural", "晓北 · 女 · 东北话" },
            new[] { "zh-CN-shaanxi-XiaoniNeural",   "晓妮 · 女 · 陕西话" },
        };

        private static readonly string[][] JaVoices =
        {
            new[] { "ja-JP-NanamiNeural", "七海 · 女 · 亲和（默认）" },
            new[] { "ja-JP-AoiNeural",    "葵 · 女 · 轻快" },
            new[] { "ja-JP-MayuNeural",   "真夕 · 女 · 沉稳" },
            new[] { "ja-JP-ShioriNeural", "栞 · 女 · 温柔" },
            new[] { "ja-JP-KeitaNeural",  "圭太 · 男 · 沉稳" },
            new[] { "ja-JP-DaichiNeural", "大地 · 男 · 播报" },
            new[] { "ja-JP-NaokiNeural",  "直希 · 男 · 少年" },
        };

        /// <summary>下拉项：值 = 音色 ID，显示 = 友好名；配置里是清单外的值就补一条「自定义」在最前面。</summary>
        private static List<PopupItem> VoiceItems(string[][] table, string current)
        {
            var list = new List<PopupItem>();
            foreach (var row in table) list.Add(new PopupItem { Value = row[0], Text = row[1] });
            bool found = list.Exists(x => string.Equals(x.Value, current ?? "", StringComparison.OrdinalIgnoreCase));
            if (!found && !string.IsNullOrWhiteSpace(current))
                list.Insert(0, new PopupItem { Value = current, Text = "自定义 · " + current });
            return list;
        }

        private static string VoiceLabel(string[][] table, string current)
        {
            if (string.IsNullOrWhiteSpace(current)) return table[0][1];
            foreach (var row in table)
                if (string.Equals(row[0], current.Trim(), StringComparison.OrdinalIgnoreCase)) return row[1];
            return "自定义 · " + current.Trim();
        }

        private static string VoiceId(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "默认";
            return v.Trim().Replace("Neural", "");
        }

        private void SetEdgeVoiceHint()
        {
            if (_edgeVoiceHint == null) return;
            _edgeVoiceHint.Text = "当前 " + VoiceId(_cfg.edge_voice_zh)
                                + " · " + VoiceId(_cfg.edge_voice_ja) + "（点 🔊 试听）";
        }

        private void BuildSettings()
        {
            if (_settingsBuilt) return;
            _settingsBuilt = true;
            BuildSettingsTabs();
            foreach (var t in _settingsTabs) t.Panel.Children.Clear();
            _savers.Clear();
            _selectValues.Clear();
            _selectRefreshers.Clear();
            _irDesignRows.Clear();
            _irSamplingRows.Clear();
            _irLoraRows.Clear();
            _irIndepRows.Clear();
            _irJointRow = null;

            // ---------- 翻译 · 接口 ----------
            BuildIntoTab("translate");
            SettingsStack.Children.Add(SectionTitle("接口"));
            var fBase = Field(_cfg.api_base);
            SettingsStack.Children.Add(RowFull("接口地址", fBase));
            var fKey = Field(_cfg.api_key);
            SettingsStack.Children.Add(RowFull("API Key（留空即可）", fKey));
            var fTimeout = Field(_cfg.timeout.ToString());
            SettingsStack.Children.Add(RowSide("超时(秒)", fTimeout));

            // ---------- 翻译 · 模型与降级 ----------
            SettingsStack.Children.Add(SectionTitle("模型"));
            var bModel = SelectRow("翻译模型", AppConfig.ModelLabel(_cfg.model), ModelItems, v =>
            {
                _cfg.model = v;
                UpdateFallbackHint();
            });
            bModel.ToolTip = "35B 最准 · 9B 均衡 · 2B 最快；轻文档 / 长截图建议 9B 以上，三种都能翻图片";
            var cFallback = Check("无响应自动降级 / 重试", _cfg.model_fallback);
            cFallback.ToolTip = "当前模型在「首字超时」内没吐出第一个字，就换个模型重试：先试低档（35B → 9B → 2B），"
                              + "低档都不通再兜底回高档。长文分段翻译时，一旦换过档，后面的段直接沿用新档，不会每段都白等一次。";
            SettingsStack.Children.Add(cFallback);
            var fFallbackSec = Field(_cfg.model_fallback_timeout.ToString());
            fFallbackSec.ToolTip = "等第一个字最多几秒；超过就判定无响应，切下一档。建议 8~20 秒。";
            SettingsStack.Children.Add(RowSide("首字超时(秒)", fFallbackSec));
            _fallbackHint = Hint("");
            SettingsStack.Children.Add(_fallbackHint);
            cFallback.Click += (s, e) => UpdateFallbackHint();
            fFallbackSec.TextChanged += (s, e) =>
            {
                int sec;
                UpdateFallbackHint(int.TryParse(fFallbackSec.Text.Trim(), out sec) ? Math.Max(3, Math.Min(120, sec)) : -1);
            };
            UpdateFallbackHint();

            // ---------- 翻译 · 语言与参数 ----------
            SettingsStack.Children.Add(SectionTitle("语言与参数"));
            var bSrc = SelectRow("源语言", AppConfig.LangName(_cfg.source), () => LangItems(true), v =>
            {
                _cfg.source = v;
                _sameLangResult = false; HideCnRow(); CnBtn.Visibility = Visibility.Collapsed;
            });
            var bTgt = SelectRow("目标语言", AppConfig.LangName(_cfg.target), () => LangItems(false), v =>
            {
                _cfg.target = v;
                _sameLangResult = false; HideCnRow(); CnBtn.Visibility = Visibility.Collapsed;
            });
            var fMax = Field(_cfg.max_tokens.ToString());
            SettingsStack.Children.Add(RowSide("最大长度", fMax));
            var fTemp = Field(_cfg.temperature.ToString("0.##"));
            SettingsStack.Children.Add(RowSide("随机度", fTemp));
            var fChunk = Field(_cfg.chunk_limit.ToString());
            SettingsStack.Children.Add(RowSide("分段阈值", fChunk));

            // ---------- 翻译 · 指令与术语 ----------
            SettingsStack.Children.Add(SectionTitle("指令"));
            var fIns = Field(_cfg.instruction, true);
            _fIns = fIns;
            SettingsStack.Children.Add(RowFull("翻译指令", fIns));

            // 指令预设：点一下就填进上面的「翻译指令」（立即生效 + 写进配置），也可以把当前指令存成新预设
            _presetBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            SettingsStack.Children.Add(_presetBar);
            var presetAddRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            var fPresetName = Field("");
            fPresetName.Width = 110;
            fPresetName.ToolTip = "预设名，例如「敬体日语」；点「存为预设」会把上面「翻译指令」里的内容存成这个名字";
            var bPresetAdd = new Button { Content = "存为预设", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0) };
            var bPresetDel = new Button { Content = "删除预设", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0) };
            bPresetAdd.Click += (s, e) =>
            {
                string name = fPresetName.Text.Trim();
                string body = fIns.Text.Trim();
                if (name.Length == 0) { Toast("先给预设起个名字，例如「敬体日语」", true); return; }
                if (body.Length == 0) { Toast("上面的「翻译指令」还是空的，先写点内容", true); return; }
                SaveInstructionPreset(name, body);
                fPresetName.Text = "";
                RebuildPresetBar();
                Toast("已存为预设「" + name + "」" + (body.Length > 24 ? "（同名会覆盖）" : ""));
            };
            bPresetDel.Click += (s, e) =>
            {
                string name = fPresetName.Text.Trim();
                if (name.Length == 0) name = _activePreset;
                if (name.Length == 0) { Toast("先在左边填预设名，或先点一下要删的预设", true); return; }
                if (DeleteInstructionPreset(name)) { fPresetName.Text = ""; RebuildPresetBar(); Toast("已删除预设「" + name + "」"); }
                else Toast("没有叫「" + name + "」的预设", true);
            };
            presetAddRow.Children.Add(fPresetName);
            presetAddRow.Children.Add(bPresetAdd);
            presetAddRow.Children.Add(bPresetDel);
            SettingsStack.Children.Add(presetAddRow);
            _presetBar.ToolTip = "点预设按钮 = 套用（立即生效并写进配置）；预设存在配置文件里，可以删";
            RebuildPresetBar();

            var fGenre = Field(_cfg.genre);
            fGenre.ToolTip = "文体声明，例如：商务邮件 / 用户协议 / 游戏 UI / 工业制造（留空 = 文本）";
            SettingsStack.Children.Add(RowFull("文体（可留空）", fGenre));
            var fGloss = Field(_cfg.glossary, true);
            SettingsStack.Children.Add(RowFull("术语表（每行：原词=译词）", fGloss));
            SettingsStack.Children.Add(Hint("也支持 JSON 路径 / 内联 JSON / 逗号分隔",
                "每行「原词=译词」；也可以填 JSON 文件路径、内联 JSON（{\"原词\":\"译词\"}）或全角逗号分隔"));
            var cGloss = Check("启用术语表", _cfg.glossary_on);
            SettingsStack.Children.Add(cGloss);

            // 指令 / 文体 / 术语表改正即生效：以前必须点「保存」才写进内存配置，
            // 改了不保存就翻译，看上去像「指令不生效」。
            fIns.TextChanged += (s, e) =>
            {
                _cfg.instruction = fIns.Text;
                UpdateConstraintSummary();
                // 哪颗预设和当前指令一致就高亮哪颗；延后到本轮事件之后再重建，
                // 免得在预设按钮自己的 Click 里把它从可视化树上摘掉
                if (_presetBar != null) Dispatcher.BeginInvoke(new Action(RebuildPresetBar), DispatcherPriority.Background);
            };
            fGenre.TextChanged += (s, e) => { _cfg.genre = fGenre.Text.Trim(); UpdateConstraintSummary(); };
            fGloss.TextChanged += (s, e) => { _cfg.glossary = fGloss.Text; UpdateConstraintSummary(); };
            cGloss.Click += (s, e) => { _cfg.glossary_on = cGloss.IsChecked == true; UpdateConstraintSummary(); };
            _constraintSummary = new TextBlock
            {
                Style = (Style)FindResource("HintText"),
                Margin = new Thickness(0, 2, 0, 2),
            };
            SettingsStack.Children.Add(_constraintSummary);
            UpdateConstraintSummary();

            // ---------- 自动化 ----------
            BuildIntoTab("auto");
            SettingsStack.Children.Add(SectionTitle("触发与收尾"));
            var bAuto = SelectRow("自动翻译", AutoLabel(_cfg.auto_translate), AutoItems, v => { _cfg.auto_translate = v; });
            var fDelay = Field(_cfg.auto_delay.ToString());
            SettingsStack.Children.Add(RowSide("延迟(毫秒)", fDelay));
            var bHide = SelectRow("自动隐藏", HideLabel(_cfg.auto_hide), HideItems, v => { _cfg.auto_hide = int.Parse(v); });
            var cCopy = Check("完成后自动复制译文", _cfg.auto_copy);
            SettingsStack.Children.Add(cCopy);
            var cSpeak = Check("完成后自动朗读译文", _cfg.auto_speak);
            SettingsStack.Children.Add(cSpeak);

            SettingsStack.Children.Add(SectionTitle("朗读"));
            var sRate = Slider(-10, 10, _cfg.speak_rate, 1);
            SettingsStack.Children.Add(RowSide("朗读语速", sRate));

            var bEngine = SelectRow("朗读通道",
                string.Equals(_cfg.speak_engine, "edge", StringComparison.OrdinalIgnoreCase) ? "Edge 在线语音" : "系统语音",
                () => new List<PopupItem>
                {
                    new PopupItem { Value = "system", Text = "系统语音（离线，质感一般）" },
                    new PopupItem { Value = "edge", Text = "Edge 在线语音（自然，需联网）" },
                },
                v => { _cfg.speak_engine = v; });
            SelectRow("中文音色", VoiceLabel(ZhVoices, _cfg.edge_voice_zh),
                () => VoiceItems(ZhVoices, _cfg.edge_voice_zh),
                v => { _cfg.edge_voice_zh = v; SetEdgeVoiceHint(); });
            SelectRow("日语音色", VoiceLabel(JaVoices, _cfg.edge_voice_ja),
                () => VoiceItems(JaVoices, _cfg.edge_voice_ja),
                v => { _cfg.edge_voice_ja = v; SetEdgeVoiceHint(); });
            _edgeVoiceHint = Hint("断网会提示失败，切回系统语音即可；点 🔊 试听",
                "「Edge 在线语音」走 Edge 浏览器大声朗读那条通道，免费、不用 Key，长文按句切段、边合成边播；"
                + "在线模式下再点一次 🔊 就是停止。\n"
                + "想用列表外的音色：直接改配置文件里的 edge_voice_zh / edge_voice_ja，"
                + "填 Azure 的短音色名即可（例如 zh-CN-XiaoxiaoNeural、ja-JP-NanamiNeural）。");
            SettingsStack.Children.Add(_edgeVoiceHint);
            SetEdgeVoiceHint();

            // ---------- 界面 ----------
            BuildIntoTab("ui");
            SettingsStack.Children.Add(SectionTitle("热键"));
            var cHot = Check("启用全局热键", _cfg.hotkey_enabled);
            SettingsStack.Children.Add(cHot);
            var fHot1 = Field(_cfg.hotkey_toggle);
            SettingsStack.Children.Add(RowSide("呼出并翻译", fHot1));
            var fHot2 = Field(_cfg.hotkey_select);
            SettingsStack.Children.Add(RowSide("划词翻译", fHot2));
            SettingsStack.Children.Add(Hint("两键同效：取选中文本 → 没有就用剪贴板 → 打开翻译",
                "选中文本是模拟 Ctrl+C 拿的，之后会自动还原剪贴板。只想留一个键，把另一个留空即可。"));
            var cTop = Check("窗口置顶", _cfg.topmost);
            SettingsStack.Children.Add(SectionTitle("外观"));
            SettingsStack.Children.Add(cTop);
            var bSide = SelectRow("输入框弹出方向", _cfg.panel_side == "right" ? "优先右侧" : "优先左侧",
                () => new List<PopupItem>
                {
                    new PopupItem { Value = "left", Text = "优先左侧" },
                    new PopupItem { Value = "right", Text = "优先右侧" },
                },
                v => { _cfg.panel_side = v; PositionAll(); });
            bSide.ToolTip = "指输入框弹在圆形按钮的哪一侧；那一侧放不下会自动换到另一边。";
            var sOpacity = Slider(0.5, 1.0, _cfg.opacity, 0.05);
            SettingsStack.Children.Add(RowSide("不透明度", sOpacity));
            var fHist = Field(_cfg.history_max.ToString());
            SettingsStack.Children.Add(RowSide("历史上限", fHist));

            // ---------- 日语语音（第三方：本地 Irodori-TTS） ----------
            BuildIntoTab("voice");
            SettingsStack.Children.Add(SectionTitle("日语语音（Irodori-TTS）"));
            SettingsStack.Children.Add(Hint("「あ」按钮走本机 Irodori 服务；参数要和后端一致，否则会重新加载模型",
                "目标语言是日语时结果卡上会多出「あ」按钮（🔊 朗读通道仍在，两者并存）。"
                + "下面 5 个模型参数要和后端已加载的那份完全一致，否则服务端会重新加载模型，第一次合成会慢十几秒。"
                + "生成的语音是临时文件，翻下一句 / 关输入窗 / 退出程序时自动删除；有残留就点「清理语音缓存」。"));

            // ---- 模式切换（Tabs）：标准克隆模式 / 音色设计模式 / LoRA 模式 ----
            var irTabs = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            irTabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            irTabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            irTabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _tabCloneBtn = IrTabButton("标准克隆模式");
            _tabDesignBtn = IrTabButton("音色设计模式");
            _tabLoraBtn = IrTabButton("LoRA 模式");
            _tabCloneBtn.Click += (s, e) => SetIrodoriMode("clone");
            _tabDesignBtn.Click += (s, e) => SetIrodoriMode("design");
            _tabLoraBtn.Click += (s, e) => SetIrodoriMode("lora");
            Grid.SetColumn(_tabCloneBtn, 0);
            Grid.SetColumn(_tabDesignBtn, 1);
            Grid.SetColumn(_tabLoraBtn, 2);
            irTabs.Children.Add(_tabCloneBtn);
            irTabs.Children.Add(_tabDesignBtn);
            irTabs.Children.Add(_tabLoraBtn);
            SettingsStack.Children.Add(irTabs);

            // ---- 音色设计 / LoRA 模式：Caption（仅设计）、LoRA 文件夹（仅 LoRA）、无参考音频 / CFG / 采样控制（共用） ----
            var fIrCap = Field(_cfg.irodori_caption, true);
            fIrCap.Height = 54;
            _fIrCap = fIrCap;
            fIrCap.ToolTip = "用日语描述音色、情绪、语速、说话风格，例如：落ち着いた女性の声で、囁いてください";
            var capRow = RowFull("Caption / 风格描述（只用日语写）", fIrCap);
            SettingsStack.Children.Add(capRow);
            _irDesignRows.Add(capRow);
            _irCaptionWarn = new TextBlock { Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 2, 0, 2) };
            SettingsStack.Children.Add(_irCaptionWarn);
            _irDesignRows.Add(_irCaptionWarn);

            // ---- LoRA 模式：适配器文件夹（替代 Caption；采样 / CFG / 参考音频和设计模式共用） ----
            var fIrLora = Field(_cfg.irodori_lora_dir);
            fIrLora.ToolTip = "填 LoRA 适配器所在文件夹（服务端原样读 lora_adapter_raw），例如 E:\\Irodori\\lora\\small";
            _fIrLora = fIrLora;
            var bLoraPick = new Button { Content = "选择", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), FontSize = 11, ToolTip = "选一个 LoRA 文件夹" };
            var bLoraClear = new Button { Content = "清除", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), FontSize = 11, ToolTip = "不使用 LoRA" };
            bLoraPick.Click += (s, e) => PickLoraFolder();
            bLoraClear.Click += (s, e) => { _cfg.irodori_lora_dir = ""; if (_fIrLora != null) _fIrLora.Text = ""; _cfg.Save(); UpdateLoraHint(); Toast("已清除 LoRA 路径"); };
            var loraPanel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bLoraClear, Dock.Right);
            DockPanel.SetDock(bLoraPick, Dock.Right);
            loraPanel.Children.Add(bLoraClear);
            loraPanel.Children.Add(bLoraPick);
            loraPanel.Children.Add(fIrLora);
            var loraRow = RowFull("LoRA 文件夹", loraPanel);
            SettingsStack.Children.Add(loraRow);
            _irLoraRows.Add(loraRow);
            _irLoraHint = new TextBlock { Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            SettingsStack.Children.Add(_irLoraHint);
            _irLoraRows.Add(_irLoraHint);

            var cIrNoRef = Check("无参考音频模式（忽略参考音频，只用文本 + Caption / LoRA 生成）", _cfg.irodori_no_ref);
            SettingsStack.Children.Add(cIrNoRef);
            _irSamplingRows.Add(cIrNoRef);

            // ---- 参考音频（两种模式共用：克隆模式的主要输入，设计模式里是可选项） ----
            _irNoRef = cIrNoRef;
            _irRefName = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 84,
                Foreground = (Brush)FindResource("DimBrush"),
            };
            var bRefPick = new Button { Content = "选择", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), FontSize = 11, ToolTip = "选一个本地音频，会复制到程序目录的「参考音频」文件夹里" };
            var bRefPlay = new Button { Content = "试听", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), FontSize = 11, ToolTip = "试听选中的参考音频" };
            var bRefClear = new Button { Content = "清除", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), FontSize = 11, ToolTip = "不再使用参考音频" };
            bRefPick.Click += (s, e) => PickReferenceAudio();
            bRefPlay.Click += (s, e) => PlayReferenceAudio();
            bRefClear.Click += (s, e) => { _cfg.irodori_ref_wav = ""; _cfg.Save(); UpdateIrodoriRefRow(); Toast("已清除参考音频"); };
            var refPanel = new StackPanel { Orientation = Orientation.Horizontal };
            refPanel.Children.Add(_irRefName);
            refPanel.Children.Add(bRefPick);
            refPanel.Children.Add(bRefPlay);
            refPanel.Children.Add(bRefClear);
            SettingsStack.Children.Add(RowSide("参考音频", refPanel));
            _irRefHint = new TextBlock { Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 2, 0, 2) };
            SettingsStack.Children.Add(_irRefHint);

            // CFG 引导：joint 时只留一个全局滑块，其余三个独立滑块动态隐藏
            var bIrCfg = SelectRow("CFG 引导", CfgModeLabel(_cfg.irodori_cfg_mode), () => new List<PopupItem>
            {
                new PopupItem { Value = "independent", Text = "independent（文本 / 描述 / 音色分开调）" },
                new PopupItem { Value = "joint", Text = "joint（只用一个全局权重）" },
                new PopupItem { Value = "alternating", Text = "alternating（交替）" },
            }, v => { _cfg.irodori_cfg_mode = CfgModeValue(v); UpdateCfgRows(); });
            _irSamplingRows.Add((FrameworkElement)SettingsStack.Children[SettingsStack.Children.Count - 1]);

            var sCfgText = Slider(1, 10, _cfg.irodori_cfg_text, 0.5);
            var rCfgText = ValueSlider("文本权重", sCfgText, _cfg.irodori_cfg_text, "念字准确度（1~10）");
            SettingsStack.Children.Add(rCfgText);
            _irSamplingRows.Add(rCfgText);
            _irIndepRows.Add(rCfgText);
            var sCfgCap = Slider(1, 10, _cfg.irodori_cfg_caption, 0.5);
            var rCfgCap = ValueSlider("Caption 权重", sCfgCap, _cfg.irodori_cfg_caption, "多听话地遵循 Caption；性别 / 风格不对时调高（1~10）");
            SettingsStack.Children.Add(rCfgCap);
            _irSamplingRows.Add(rCfgCap);
            _irIndepRows.Add(rCfgCap);
            var sCfgSpk = Slider(1, 10, _cfg.irodori_cfg_speaker, 0.5);
            var rCfgSpk = ValueSlider("音色权重", sCfgSpk, _cfg.irodori_cfg_speaker, "既有参考音频又有 Caption 时更偏向谁（1~10）");
            SettingsStack.Children.Add(rCfgSpk);
            _irSamplingRows.Add(rCfgSpk);
            _irIndepRows.Add(rCfgSpk);
            var sCfgJoint = Slider(1, 10, _cfg.irodori_cfg_joint, 0.5);
            _irJointRow = ValueSlider("全局权重", sCfgJoint, _cfg.irodori_cfg_joint, "joint 模式：一个权重同时管文本 / 描述 / 音色（1~10）");
            _irSamplingRows.Add(_irJointRow);
            SettingsStack.Children.Add(_irJointRow);

            var bIrTS = SelectRow("时间调度", TScheduleLabel(_cfg.irodori_t_schedule), () => new List<PopupItem>
            {
                new PopupItem { Value = "sway", Text = "sway（配合低步数可加速）" },
                new PopupItem { Value = "linear", Text = "linear" },
            }, v => { _cfg.irodori_t_schedule = TScheduleValue(v); });
            _irSamplingRows.Add((FrameworkElement)SettingsStack.Children[SettingsStack.Children.Count - 1]);
            var fIrSteps = Field(_cfg.irodori_num_steps);
            fIrSteps.ToolTip = "采样步数：留空 = 服务端默认；配合 sway 建议 6~8（40 步 + linear 在 4G 显存上会非常慢，甚至显存不够）";
            var stepsRow = RowSide("采样步数", fIrSteps);
            SettingsStack.Children.Add(stepsRow);
            _irSamplingRows.Add(stepsRow);
            var fIrSway = Field(_cfg.irodori_sway_coeff.ToString("0.##"));
            fIrSway.ToolTip = "sway 系数：留 -1 = 服务端默认值（高级参数，一般不用改）";
            var swayRow = RowSide("sway 系数", fIrSway);
            SettingsStack.Children.Add(swayRow);
            _irSamplingRows.Add(swayRow);
            var irSampleHint = Hint("实测：sway + 8 步约 3.5 秒，linear 约 8.5 秒",
                "40 步 + linear 在 4G 显存上很慢，甚至可能显存不够。");
            SettingsStack.Children.Add(irSampleHint);
            _irSamplingRows.Add(irSampleHint);

            // ---- 模型参数（两种模式共用） ----
            var fIrUrl = Field(_cfg.irodori_url);
            SettingsStack.Children.Add(RowFull("服务地址", fIrUrl));
            var fIrCkpt = Field(_cfg.irodori_checkpoint);
            SettingsStack.Children.Add(RowFull("Checkpoint 路径", fIrCkpt));
            var fIrMd = Field(_cfg.irodori_model_device);
            SettingsStack.Children.Add(RowSide("模型设备", fIrMd));
            var bIrMp = SelectRow("模型精度", _cfg.irodori_model_precision, PrecisionItems,
                v => { _cfg.irodori_model_precision = v; });
            var fIrCd = Field(_cfg.irodori_codec_device);
            SettingsStack.Children.Add(RowSide("编解码设备", fIrCd));
            var bIrCp = SelectRow("编解码精度", _cfg.irodori_codec_precision, PrecisionItems,
                v => { _cfg.irodori_codec_precision = v; });
            var fIrDs = Field(_cfg.irodori_duration_scale.ToString("0.##"));
            SettingsStack.Children.Add(RowSide("时长缩放", fIrDs));
            // 长文分段：超过这个字数就切成多段，边放已经生成的、边合成下一段（0 = 不切，整段一次合成）
            var fIrChunk = Field(_cfg.irodori_chunk_chars.ToString());
            fIrChunk.ToolTip = "超过这个字数就切段，边放已生成的、边合成下一段；0 = 不切段，整段一次合成";
            SettingsStack.Children.Add(RowSide("分段字数", fIrChunk));
            // 切分前缀：只对 Irodori 朗读有效。切成 A/B/C 三段时，B、C 合成前拼上这段文字再发给服务
            //（第 1 段不加，它本来就是开头）；只影响合成请求，不改分段规则、也不会进译文。
            // 文本框是普通 WPF TextBox，emoji 这类代理对字符原样保留（配置也用 UnsafeRelaxedJsonEscaping 存）。
            var fIrPrefix = Field(_cfg.irodori_chunk_prefix);
            fIrPrefix.MaxLength = 200;   // 和保存时的截断上限一致（按 UTF-16 单元算）
            fIrPrefix.ToolTip = "切成 A/B/C 段时，B、C 段合成前在开头拼上这段文字（A 段不加）。"
                + "可以放 emoji，例如「（続き）🙂」；留空 = 不加。只影响合成，不影响分段与译文。";
            SettingsStack.Children.Add(RowSide("切分前缀", fIrPrefix));
            // 随机种子：「无」= 交给服务端每次随机；「随机」= 从自定义种子里抽一个；也可以固定选某个自定义种子
            var bIrSeed = SelectRow("随机种子", _cfg.irodori_seed_choice, SeedItems,
                v => { _cfg.irodori_seed_choice = v; });
            _irSeedBtn = bIrSeed;
            var seedAddRow = new StackPanel { Orientation = Orientation.Horizontal };
            seedAddRow.ToolTip = "添加种子：名字 + 数字（值留空自动生成）；「随机」会从这些自定义种子里抽一个。";
            var fSeedName = Field(""); fSeedName.Width = 100; fSeedName.ToolTip = "名字，例如 种子A";
            var fSeedVal = Field(""); fSeedVal.Width = 100; fSeedVal.Margin = new Thickness(6, 0, 0, 0); fSeedVal.ToolTip = "数字，留空则自动生成";
            var bSeedAdd = new Button { Content = "添加", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0) };
            bSeedAdd.Click += (s, e) => AddSeed(fSeedName.Text, fSeedVal.Text);
            seedAddRow.Children.Add(fSeedName);
            seedAddRow.Children.Add(fSeedVal);
            seedAddRow.Children.Add(bSeedAdd);
            SettingsStack.Children.Add(seedAddRow);
            var bSeedDel = new Button { Content = "删除当前种子", Style = (Style)FindResource("TextBtn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 2) };
            bSeedDel.Click += (s, e) => DeleteSeed();
            SettingsStack.Children.Add(bSeedDel);
            var bIrTest = new Button { Content = "检测服务", Style = (Style)FindResource("TextBtn") };
            bIrTest.Click += (s, e) => { _ = TestIrodoriAsync(); };
            var bIrReload = new Button { Content = "重新加载", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "清掉服务端内存里的模型，再按上面的参数重新加载（改了 checkpoint / 设备 / 精度之后点它）" };
            bIrReload.Click += (s, e) => { _ = ReloadIrodoriAsync(); };
            // 生成的语音是服务端写的临时文件，这里给一个手动清理的入口（顺带扫遗留的）
            var bIrClean = new Button { Content = "清理语音缓存", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(8, 0, 0, 0) };
            bIrClean.Click += (s, e) => CleanupIrodoriAudio(true, true);
            var irBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            irBtnRow.Children.Add(bIrTest);
            irBtnRow.Children.Add(bIrReload);
            irBtnRow.Children.Add(bIrClean);
            SettingsStack.Children.Add(irBtnRow);

            // ---------- 界面 · 其它按钮 ----------
            BuildIntoTab("ui");
            SettingsStack.Children.Add(SectionTitle("其它"));
            // 说明：原来这里有一颗「保存」。现在所有设置都是改完立刻生效（见 AttachLiveHandlers），
            // 所以按钮只剩「恢复默认」；写配置文件是自动的（防抖 350ms）。
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var bReset = new Button { Content = "恢复默认", Style = (Style)FindResource("TextBtn") };
            bReset.Click += (s, e) => { _cfg.ResetToDefault(); RebuildSettings(); ApplySettings(); Toast("已恢复默认设置"); };
            btns.Children.Add(bReset);
            SettingsStack.Children.Add(btns);
            SettingsStack.Children.Add(Hint("改完即生效，配置文件自动保存"));

            var btns2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            var bHist = new Button { Content = "历史记录", Style = (Style)FindResource("TextBtn") };
            bHist.Click += (s, e) => { CloseSettingsPanel(); OpenHistory(); };
            // 退出已改为设置小圆钮旁边的常驻图标按钮（见 MainWindow.xaml 的 ExitBtnHost）
            btns2.Children.Add(bHist);
            SettingsStack.Children.Add(btns2);

            // ---------- 保存动作 ----------
            _savers.Add(() =>
            {
                _cfg.api_base = fBase.Text.Trim();
                _cfg.api_key = fKey.Text.Trim();
                int t; if (int.TryParse(fTimeout.Text.Trim(), out t)) _cfg.timeout = Math.Max(5, t);
                _cfg.model_fallback = cFallback.IsChecked == true;
                int fb; if (int.TryParse(fFallbackSec.Text.Trim(), out fb)) _cfg.model_fallback_timeout = Math.Max(3, Math.Min(120, fb));
                int mx; if (int.TryParse(fMax.Text.Trim(), out mx)) _cfg.max_tokens = Math.Max(64, mx);
                double tp; if (double.TryParse(fTemp.Text.Trim(), out tp)) _cfg.temperature = Math.Max(0, Math.Min(2, tp));
                int ck; if (int.TryParse(fChunk.Text.Trim(), out ck)) _cfg.chunk_limit = Math.Max(100, ck);
                _cfg.instruction = fIns.Text;
                _cfg.genre = fGenre.Text.Trim();
                _cfg.glossary = fGloss.Text;
                _cfg.glossary_on = cGloss.IsChecked == true;
                int dl; if (int.TryParse(fDelay.Text.Trim(), out dl)) _cfg.auto_delay = Math.Max(100, dl);
                _cfg.auto_copy = cCopy.IsChecked == true;
                _cfg.auto_speak = cSpeak.IsChecked == true;
                _cfg.speak_rate = (int)Math.Round(sRate.Value);
                string eng = _selectValues.ContainsKey(bEngine) ? _selectValues[bEngine] : _cfg.speak_engine;
                _cfg.speak_engine = (eng == "edge") ? "edge" : "system";
                _cfg.hotkey_enabled = cHot.IsChecked == true;
                _cfg.hotkey_toggle = fHot1.Text.Trim();
                _cfg.hotkey_select = fHot2.Text.Trim();
                _cfg.topmost = cTop.IsChecked == true;
                _cfg.opacity = Math.Round(sOpacity.Value, 2);
                string side = _selectValues.ContainsKey(bSide) ? _selectValues[bSide] : _cfg.panel_side;
                _cfg.panel_side = (side == "right" || side == "优先右侧") ? "right" : "left";
                int hm; if (int.TryParse(fHist.Text.Trim(), out hm)) _cfg.history_max = Math.Max(10, hm);

                _cfg.irodori_url = fIrUrl.Text.Trim();
                _cfg.irodori_checkpoint = fIrCkpt.Text.Trim();
                _cfg.irodori_model_device = fIrMd.Text.Trim();
                _cfg.irodori_codec_device = fIrCd.Text.Trim();
                double ids; if (double.TryParse(fIrDs.Text.Trim(), out ids)) _cfg.irodori_duration_scale = Math.Max(0.2, Math.Min(3, ids));
                int ichunk; if (int.TryParse(fIrChunk.Text.Trim(), out ichunk)) _cfg.irodori_chunk_chars = Math.Max(0, Math.Min(1000, ichunk));
                // 前缀原样保存（不 Trim：用户可能真的想要开头的空格/换行），只挡一下离谱的长度。
                // 截断时别把 emoji 的代理对劈开：劈开会留下半个 UTF-16 单元，写配置时 JsonSerializer 会报错。
                string ipfx = fIrPrefix.Text ?? "";
                if (ipfx.Length > 200)
                {
                    int cut = 200;
                    if (char.IsHighSurrogate(ipfx[cut - 1])) cut--;   // 第 200 个是代理对前半 → 少要一个
                    ipfx = ipfx.Substring(0, cut);
                }
                _cfg.irodori_chunk_prefix = ipfx;

                // 音色设计模式
                _cfg.irodori_caption = fIrCap.Text;
                // LoRA 模式
                _cfg.irodori_lora_dir = (fIrLora.Text ?? "").Trim();
                _cfg.irodori_no_ref = cIrNoRef.IsChecked == true;
                _cfg.irodori_num_steps = (fIrSteps.Text ?? "").Trim();
                double isw;
                if (double.TryParse(fIrSway.Text.Trim(), out isw)) _cfg.irodori_sway_coeff = Math.Max(-10, Math.Min(10, isw));
                _cfg.irodori_cfg_mode = CfgModeValue(SelVal(bIrCfg, _cfg.irodori_cfg_mode));
                _cfg.irodori_cfg_text = Math.Round(sCfgText.Value, 1);
                _cfg.irodori_cfg_caption = Math.Round(sCfgCap.Value, 1);
                _cfg.irodori_cfg_speaker = Math.Round(sCfgSpk.Value, 1);
                _cfg.irodori_cfg_joint = Math.Round(sCfgJoint.Value, 1);
                _cfg.irodori_t_schedule = TScheduleValue(SelVal(bIrTS, _cfg.irodori_t_schedule));
            });

            // ---------- 字幕（实时字幕 + 词典，见 MainWindow.Asr.cs） ----------
            BuildIntoTab("sub");
            BuildAsrSettings();

            // 面板建好后：挂「改完即生效」，并按当前模式 / CFG 模式刷一次显隐
            AttachLiveHandlers();
            ApplyIrodoriModeUi();
            SwitchSettingsTab(_settingsTabKey);
        }

        private static List<PopupItem> PrecisionItems()
        {
            return new List<PopupItem>
            {
                new PopupItem { Value = "fp32", Text = "fp32" },
                new PopupItem { Value = "bf16", Text = "bf16" },
            };
        }

        /// <summary>「翻译模型」下拉：官方三档规格（都能翻图片）。</summary>
        private static List<PopupItem> ModelItems()
        {
            var items = new List<PopupItem>();
            foreach (var m in AppConfig.MODEL_LIST)
                items.Add(new PopupItem { Value = m, Text = AppConfig.ModelLabel(m) });
            return items;
        }

        /// <summary>「随机种子」下拉的候选项：内置的「无 / 随机」+ 用户自己加的种子。</summary>
        private List<PopupItem> SeedItems()
        {
            var items = new List<PopupItem>
            {
                new PopupItem { Value = "无", Text = "无（服务端随机）" },
                new PopupItem { Value = "随机", Text = "随机（从自定义里抽）" },
            };
            foreach (var s in _cfg.irodori_seeds)
                if (s != null && !string.IsNullOrEmpty(s.name))
                    items.Add(new PopupItem { Value = s.name, Text = s.name + " · " + s.value });
            return items;
        }

        /// <summary>添加一个自定义种子：名字必填且不能叫「无 / 随机」，值留空则自动生成一个 9 位数字。</summary>
        private void AddSeed(string name, string value)
        {
            name = (name ?? "").Trim();
            value = (value ?? "").Trim();
            if (name.Length == 0) { Toast("先给种子起个名字，例如「种子A」", true); return; }
            if (name == "无" || name == "随机") { Toast("「" + name + "」是内置选项，换个名字吧", true); return; }
            foreach (var s in _cfg.irodori_seeds)
                if (s != null && s.name == name) { Toast("已经有叫「" + name + "」的种子了", true); return; }
            if (value.Length == 0) value = new Random().Next(100000000, 999999999).ToString();
            long tmp;
            if (!long.TryParse(value, out tmp)) { Toast("种子值要填数字（留空则自动生成）", true); return; }
            if (_cfg.irodori_seeds == null) _cfg.irodori_seeds = new List<AppConfig.IrodoriSeed>();
            _cfg.irodori_seeds.Add(new AppConfig.IrodoriSeed { name = name, value = value });
            _cfg.irodori_seed_choice = name;
            SaveSettings();
            RebuildSettings();
            Toast("已添加种子「" + name + "」= " + value + "，并选中它");
        }

        /// <summary>删掉当前选中的自定义种子（内置的「无 / 随机」删不了）。</summary>
        private void DeleteSeed()
        {
            string c = (_cfg.irodori_seed_choice ?? "").Trim();
            if (c.Length == 0 || c == "无" || c == "随机")
            {
                Toast("当前选的是「" + (c.Length == 0 ? "无" : c) + "」，不是自定义种子", true);
                return;
            }
            int n = _cfg.irodori_seeds.RemoveAll(s => s != null && s.name == c);
            if (n == 0) { Toast("找不到种子「" + c + "」", true); return; }
            _cfg.irodori_seed_choice = "无";
            SaveSettings();
            RebuildSettings();
            Toast("已删除种子「" + c + "」");
        }

        private static List<PopupItem> AutoItems()
        {
            return new List<PopupItem>
            {
                new PopupItem { Value = "off", Text = "关闭" },
                new PopupItem { Value = "paste", Text = "粘贴后自动翻译" },
                new PopupItem { Value = "typing", Text = "停止输入后翻译" },
            };
        }

        private static List<PopupItem> HideItems()
        {
            var list = new List<PopupItem> { new PopupItem { Value = "0", Text = "从不" } };
            foreach (var s in new[] { 5, 10, 30, 60, 180 })
                list.Add(new PopupItem { Value = s.ToString(), Text = s + " 秒" });
            return list;
        }

        private static string AutoLabel(string v)
        {
            return v == "paste" ? "粘贴后自动翻译" : (v == "typing" ? "停止输入后翻译" : "关闭");
        }

        private static string HideLabel(int v)
        {
            return v <= 0 ? "从不" : v + " 秒";
        }

        private void BuildSettingsValues()
        {
            if (_settingsBuilt) return;
            BuildSettings();
        }

        /// <summary>清空重建（恢复默认后用）</summary>
        private void RebuildSettings()
        {
            _settingsBuilt = false;
            _selectValues.Clear();
            _selectRefreshers.Clear();
            BuildSettings();   // 各栏面板由 BuildSettings 自己清空 / 重建
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                SettingsPanel.UpdateLayout();
                PositionSettingsPanel();
            }
        }

        /// <summary>刷新「模型降级链路」提示（改了模型 / 超时 / 开关都更新）。</summary>
        private void UpdateFallbackHint(int secOverride = -1)
        {
            if (_fallbackHint == null) return;
            string cur = AppConfig.ModelShort(_cfg.model);
            if (!_cfg.model_fallback)
            {
                _fallbackHint.Text = "已关闭降级：只用 " + cur;
                return;
            }
            var chain = AppConfig.FallbackChain(_cfg.model);
            if (chain.Count <= 1)
            {
                _fallbackHint.Text = cur + " 已是最低档，没有可降的模型";
                return;
            }
            int sec = secOverride > 0 ? secOverride : _cfg.model_fallback_timeout;
            string path = "";
            foreach (var m in chain) path += (path.Length == 0 ? "" : " → ") + AppConfig.ModelShort(m);
            _fallbackHint.Text = path + "，每档最多等 " + sec + " 秒首字";
        }

        /// <summary>刷新「当前生效的约束」提示行（指令/文体/术语表一改就更新）。</summary>
        private void UpdateConstraintSummary()
        {
            if (_constraintSummary == null) return;
            _constraintSummary.Text = "当前生效：" + Prompt.ConstraintSummary(_cfg.instruction, _cfg.glossary, _cfg.glossary_on, _cfg.genre);
            _constraintSummary.ToolTip = "指令 / 文体 / 术语表改动即时生效，不用点保存（配置文件会自动写）";
        }

        /// <summary>重建预设按钮条：点一下就把该预设填进「翻译指令」（立即生效 + 落盘）；与当前指令一致的那颗高亮。</summary>
        private void RebuildPresetBar()
        {
            if (_presetBar == null) return;
            _presetBar.Children.Clear();
            if (_cfg.instruction_presets == null) _cfg.instruction_presets = AppConfig.DefaultPresets();
            string cur = _fIns == null ? "" : (_fIns.Text ?? "").Trim();
            foreach (var p in _cfg.instruction_presets)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.name)) continue;
                string name = p.name.Trim();
                string body = p.text ?? "";
                var btn = new Button
                {
                    Content = name,
                    Style = (Style)FindResource("TextBtn"),
                    Margin = new Thickness(0, 2, 6, 0),
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11.5,
                    ToolTip = body,
                };
                if (body.Trim().Length > 0 && string.Equals(body.Trim(), cur, StringComparison.Ordinal))
                    btn.Foreground = (Brush)FindResource("AccentBrush");
                btn.Click += (s, e) =>
                {
                    if (_fIns == null) return;
                    _activePreset = name;
                    if (string.Equals((_fIns.Text ?? "").Trim(), body.Trim(), StringComparison.Ordinal))
                    {
                        Toast("预设「" + name + "」已经在用了");
                        return;
                    }
                    _fIns.Text = body;      // TextChanged 会写进 _cfg.instruction，并刷新提示行与高亮
                    _fIns.CaretIndex = _fIns.Text.Length;
                    _cfg.Save();            // 套用预设 = 用户明确选了它，直接落盘，重启后还在
                    Toast("已套用预设「" + name + "」（立即生效）");
                };
                _presetBar.Children.Add(btn);
            }
            if (_presetBar.Children.Count == 0)
                _presetBar.Children.Add(new TextBlock
                {
                    Text = "（没有预设：写好指令后起个名字，点「存为预设」）",
                    Style = (Style)FindResource("HintText"),
                });
        }

        /// <summary>把「翻译指令」里的内容存成（或覆盖）一个预设，并立即写进配置文件。</summary>
        private void SaveInstructionPreset(string name, string text)
        {
            if (_cfg.instruction_presets == null) _cfg.instruction_presets = AppConfig.DefaultPresets();
            AppConfig.InstructionPreset hit = null;
            foreach (var p in _cfg.instruction_presets)
                if (p != null && string.Equals(p.name.Trim(), name, StringComparison.Ordinal)) { hit = p; break; }
            if (hit != null) hit.text = text;
            else _cfg.instruction_presets.Add(new AppConfig.InstructionPreset { name = name, text = text });
            _activePreset = name;
            _cfg.Save();
        }

        /// <summary>删掉一个预设（返回是否真删到了）。</summary>
        private bool DeleteInstructionPreset(string name)
        {
            if (_cfg.instruction_presets == null) return false;
            int n = _cfg.instruction_presets.RemoveAll(p => p != null && string.Equals(p.name.Trim(), name, StringComparison.Ordinal));
            if (n == 0) return false;
            if (string.Equals(_activePreset, name, StringComparison.Ordinal)) _activePreset = "";
            _cfg.Save();
            return true;
        }

        /// <summary>改造后的「保存」：只在种子增删 / 恢复默认这类明确动作里用。</summary>
        private void SaveSettings()
        {
            foreach (var s in _savers) s();
            ApplySettings();
            _cfg.Save();
            Toast("设置已保存");
        }

        // ============================ 动态生效（大部分设置改完立刻用上） ============================

        private string _hotkeySignature = "";   // 热键配置的指纹：变了才重新注册

        /// <summary>给设置面板上所有输入类控件挂「改完就生效」（每栏都要走一遍）。</summary>
        private void AttachLiveHandlers()
        {
            foreach (var el in WalkSettings())
            {
                var tb = el as TextBox;
                if (tb != null) { tb.TextChanged += (s, e) => LiveApply(); continue; }
                var cb = el as CheckBox;
                if (cb != null) { cb.Click += (s, e) => LiveApply(); continue; }
                var sl = el as Slider;
                if (sl != null) { sl.ValueChanged += (s, e) => LiveApply(); }
            }
        }

        /// <summary>遍历所有分栏里的控件（原来只走一个 SettingsStack）。</summary>
        private IEnumerable<DependencyObject> WalkSettings()
        {
            foreach (var t in _settingsTabs)
                foreach (var el in Walk(t.Panel))
                    yield return el;
        }

        private static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            foreach (var c in LogicalTreeHelper.GetChildren(root))
            {
                var d = c as DependencyObject;
                if (d == null) continue;
                yield return d;
                foreach (var x in Walk(d)) yield return x;
            }
        }

        /// <summary>跑一遍 saver → 立即应用 → 防抖写盘。</summary>
        private void LiveApply()
        {
            try
            {
                foreach (var a in _savers) a();
            }
            catch (Exception ex) { App.Log("设置即时生效(读取控件)失败: " + ex.Message); }
            try
            {
                ApplySettingsLight();
                UpdateCaptionWarn();
                UpdateIrodoriRefRow();
                UpdateLoraHint(false);   // 打字时不扫盘（大文件夹扫一次很慢），切模式 / 选文件夹时才校验
                ScheduleSave();
            }
            catch (Exception ex) { App.Log("设置即时生效(应用)失败: " + ex.Message); }
        }

        /// <summary>写配置文件防抖 350ms（敲字时不要每次都落盘）。</summary>
        private void ScheduleSave()
        {
            if (_liveSaveTimer == null)
            {
                _liveSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _liveSaveTimer.Tick += (s, e) => { _liveSaveTimer.Stop(); try { _cfg.Save(); } catch { } };
            }
            _liveSaveTimer.Stop();
            _liveSaveTimer.Start();
        }

        /// <summary>把改动应用到运行时：不重建面板、不弹 Toast、不反复重注册热键。</summary>
        private void ApplySettingsLight()
        {
            _client = new TranslateClient(_cfg.api_base, _cfg.api_key, _cfg.timeout);
            Topmost = _cfg.topmost;
            Opacity = Math.Max(0.3, Math.Min(1, _cfg.opacity));
            UpdateIrodoriBtn();
            string want = (_cfg.hotkey_enabled ? "1" : "0") + "|" + (_cfg.hotkey_toggle ?? "").Trim() + "|" + (_cfg.hotkey_select ?? "").Trim();
            if (want != _hotkeySignature)
            {
                _hotkeySignature = want;
                if (_hotkeys != null) { _hotkeys.Dispose(); _hotkeys = null; }
                SetupHotkeys();
            }
            PositionAll();
        }

        private void ApplySettings()
        {
            _client = new TranslateClient(_cfg.api_base, _cfg.api_key, _cfg.timeout);
            Topmost = _cfg.topmost;
            Opacity = _cfg.opacity;
            UpdateIrodoriBtn();
            if (_hotkeys != null) { _hotkeys.Dispose(); _hotkeys = null; }
            SetupHotkeys();
            PositionAll();
        }

        // ============================ Irodori 音色设计模式：界面联动 ============================

        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));

        /// <summary>模式切换的 tab 按钮。</summary>
        private Button IrTabButton(string text)
        {
            var b = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(0, 5, 0, 5),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("DimBrush"),
            };
            return b;
        }

        private void SetIrodoriMode(string mode)
        {
            string m = mode == "design" ? "design" : (mode == "lora" ? "lora" : "clone");
            if ((_cfg.irodori_mode ?? "clone") == m) return;
            _cfg.irodori_mode = m;
            ApplyIrodoriModeUi();
            _cfg.Save();
            Toast(m == "design" ? "已切到音色设计模式" : (m == "lora" ? "已切到 LoRA 模式" : "已切到标准克隆模式"));
        }

        /// <summary>按当前模式刷新：design / lora 行显隐、tab 高亮、CFG 行显隐、参考音频提示。</summary>
        private void ApplyIrodoriModeUi()
        {
            string m = _cfg.irodori_mode ?? "clone";
            bool design = m == "design";
            bool lora = m == "lora";
            bool sampling = design || lora;
            foreach (var el in _irDesignRows) if (el != null) el.Visibility = design ? Visibility.Visible : Visibility.Collapsed;
            foreach (var el in _irSamplingRows) if (el != null) el.Visibility = sampling ? Visibility.Visible : Visibility.Collapsed;
            foreach (var el in _irLoraRows) if (el != null) el.Visibility = lora ? Visibility.Visible : Visibility.Collapsed;
            if (_tabCloneBtn != null) _tabCloneBtn.Foreground = (Brush)FindResource(sampling ? "DimBrush" : "AccentBrush");
            if (_tabDesignBtn != null) _tabDesignBtn.Foreground = (Brush)FindResource(design ? "AccentBrush" : "DimBrush");
            if (_tabLoraBtn != null) _tabLoraBtn.Foreground = (Brush)FindResource(lora ? "AccentBrush" : "DimBrush");
            UpdateCfgRows();
            UpdateLoraHint();
            UpdateIrodoriRefRow();
        }

        /// <summary>CFG 选 joint 时只留一个全局滑块，其他三个独立滑块藏起来（克隆模式下整组都藏）。</summary>
        private void UpdateCfgRows()
        {
            string m = _cfg.irodori_mode ?? "clone";
            bool sampling = m == "design" || m == "lora";
            bool joint = CfgModeValue(_cfg.irodori_cfg_mode) == "joint";
            foreach (var el in _irIndepRows) if (el != null) el.Visibility = sampling && !joint ? Visibility.Visible : Visibility.Collapsed;
            if (_irJointRow != null) _irJointRow.Visibility = sampling && joint ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>参考音频行：文件名 + 按模式 / no-ref 的提示。</summary>
        private void UpdateIrodoriRefRow()
        {
            string p = IrodoriTts.ResolveReference(_cfg.irodori_ref_wav);
            bool has = !string.IsNullOrEmpty(p);
            if (_irRefName != null)
            {
                _irRefName.Text = has ? System.IO.Path.GetFileName(p) : "（未选择）";
                _irRefName.ToolTip = has ? p : "点「选择」挑一段音频，会复制到程序目录的「参考音频」文件夹里";
            }
            string m = _cfg.irodori_mode ?? "clone";
            bool clone = m != "design" && m != "lora";
            bool noRef = _cfg.irodori_no_ref;
            if (_irRefHint != null)
            {
                if (clone)
                    _irRefHint.Text = has ? "克隆模式：用这段音频的音色念译文。" : "克隆模式还没有参考音频，合成会失败——先点「选择」挑一段（日语最好）。";
                else if (noRef)
                    _irRefHint.Text = "已勾「无参考音频模式」：只用文本" + (m == "lora" ? " + LoRA" : " + Caption") + " 生成，参考音频会被忽略。";
                else if (m == "lora")
                    _irRefHint.Text = has ? "LoRA 模式：LoRA 决定音色，参考音频可选（填了会一起送去当说话人参考）。" : "LoRA 模式：没选参考音频时靠 LoRA 定音色。";
                else
                    _irRefHint.Text = has ? "设计模式：参考音频 + Caption 一起决定音色。" : "设计模式：没选参考音频时按「文本 + Caption」生成。";
            }
            UpdateCaptionWarn();
        }

        /// <summary>Caption 只允许日语的软校验（没有假名就提醒，但不拦着）。</summary>
        private void UpdateCaptionWarn()
        {
            if (_irCaptionWarn == null) return;
            string cap = (_cfg.irodori_caption ?? "").Trim();
            if (cap.Length == 0)
            {
                _irCaptionWarn.Text = "留空 = 不描述，由服务端自由发挥。用日语写效果最稳（例：落ち着いた女性の声で、囁いてください）。";
                _irCaptionWarn.Foreground = (Brush)FindResource("DimBrush");
                return;
            }
            bool hasKana = false;
            foreach (char c in cap)
                if ((c >= '\u3041' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF')) { hasKana = true; break; }
            _irCaptionWarn.Text = hasKana
                ? "Caption 用日语写 ✓（检测到假名）。"
                : "⚠ Caption 里没看到日文假名 —— 这个参数是按日语描述训的，中文 / 英文可能读成怪音，建议换成日语。";
            _irCaptionWarn.Foreground = hasKana ? (Brush)FindResource("DimBrush") : WarnBrush;
        }

        /// <summary>LoRA 模式：挑一个适配器文件夹（原样交给服务端的 lora_adapter_raw）。</summary>
        private void PickLoraFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选一个 LoRA 适配器文件夹" };
            if (dlg.ShowDialog() != true) return;
            string dir = (dlg.FolderName ?? "").Trim();
            if (dir.Length == 0) return;
            _cfg.irodori_lora_dir = dir;
            if (_fIrLora != null) _fIrLora.Text = dir;
            _cfg.Save();
            UpdateLoraHint();
            Toast("已选 LoRA 文件夹：" + System.IO.Path.GetFileName(dir.TrimEnd('\\', '/')));
        }

        /// <summary>LoRA 路径提示：空 / 不存在 / 目录里有 safetensors 都给出说明。probe=false 时不扫盘。</summary>
        private void UpdateLoraHint(bool probe = true)
        {
            if (_irLoraHint == null) return;
            if ((_cfg.irodori_mode ?? "clone") != "lora") return;   // 非 LoRA 模式这一行本来就藏着
            string dir = (_cfg.irodori_lora_dir ?? "").Trim();
            if (dir.Length == 0)
            {
                _irLoraHint.Text = "不填 = 不加载 LoRA（采样参数与音色设计模式相同，只是没有风格描述）。";
                _irLoraHint.Foreground = (Brush)FindResource("DimBrush");
                return;
            }
            if (!probe)
            {
                _irLoraHint.Text = "将使用：" + dir;
                _irLoraHint.Foreground = (Brush)FindResource("DimBrush");
                return;
            }
            try
            {
                if (!System.IO.Directory.Exists(dir))
                {
                    _irLoraHint.Text = "⚠ 文件夹不存在（服务端加载会失败）：" + dir;
                    _irLoraHint.Foreground = WarnBrush;
                    return;
                }
                bool hasWeight = System.IO.Directory.GetFiles(dir, "*.safetensors", System.IO.SearchOption.AllDirectories).Length > 0;
                _irLoraHint.Text = hasWeight ? "✓ 找到适配器权重：" + dir : "⚠ 这个文件夹里没找到 *.safetensors：" + dir;
                _irLoraHint.Foreground = hasWeight ? (Brush)FindResource("DimBrush") : WarnBrush;
            }
            catch (Exception ex)
            {
                _irLoraHint.Text = "读取文件夹失败：" + ex.Message;
                _irLoraHint.Foreground = WarnBrush;
            }
        }

        private void PickReferenceAudio()
        {            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选一段参考音频（日语最好）",
                Filter = "音频 (*.wav;*.mp3;*.flac;*.m4a;*.ogg)|*.wav;*.mp3;*.flac;*.m4a;*.ogg|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string dir = IrodoriTts.ReferenceDir;
                string name = System.IO.Path.GetFileName(dlg.FileName);
                string dest = System.IO.Path.Combine(dir, name);
                if (!string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Copy(dlg.FileName, dest, true);
                _cfg.irodori_ref_wav = name;
                _cfg.Save();
                UpdateIrodoriRefRow();
                Toast("已选参考音频：" + name);
            }
            catch (Exception ex)
            {
                Toast("复制参考音频失败：" + ex.Message, true);
            }
        }

        private void PlayReferenceAudio()
        {
            string p = IrodoriTts.ResolveReference(_cfg.irodori_ref_wav);
            if (string.IsNullOrEmpty(p)) { Toast("还没选参考音频", true); return; }
            try
            {
                if (_audio.IsPlaying) _audio.Stop();
                _audio.Play(p);
                Toast("试听中…");
            }
            catch (Exception ex) { Toast("试听失败：" + ex.Message, true); }
        }

        private static string CfgModeValue(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v.StartsWith("joint")) return "joint";
            if (v.StartsWith("alt")) return "alternating";
            return "independent";
        }

        private static string CfgModeLabel(string v)
        {
            switch (CfgModeValue(v))
            {
                case "joint": return "joint（只用一个全局权重）";
                case "alternating": return "alternating（交替）";
                default: return "independent（文本 / 描述 / 音色分开调）";
            }
        }

        private static string TScheduleValue(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            return v.StartsWith("lin") ? "linear" : "sway";
        }

        private static string TScheduleLabel(string v)
        {
            return TScheduleValue(v) == "linear" ? "linear" : "sway（配合低步数可加速）";
        }

        private string SelVal(Button btn, string fallback)
        {
            string v;
            return btn != null && _selectValues.TryGetValue(btn, out v) ? v : fallback;
        }

        /// <summary>滑块 + 右侧实时数字的一行。</summary>
        private FrameworkElement ValueSlider(string label, Slider s, double value, string tip)
        {
            var val = new TextBlock
            {
                Text = value.ToString("0.#"),
                FontSize = 12,
                MinWidth = 26,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TxtBrush"),
            };
            s.ValueChanged += (a, b) => val.Text = b.NewValue.ToString("0.#");
            s.ToolTip = tip;
            s.Width = 118;
            s.Margin = new Thickness(0);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(s);
            panel.Children.Add(val);
            return RowSide(label, panel);
        }

        /// <summary>清掉服务端内存里的模型，再按面板上的参数重新加载。</summary>
        private async Task ReloadIrodoriAsync()
        {
            Toast("正在让服务端重新加载模型…");
            try
            {
                var tts = IrodoriTts.FromConfig(_cfg);
                string cleared = await tts.ClearRuntimeAsync(CancellationToken.None);
                App.Log("Irodori 清缓存: " + cleared);
                string status = await tts.LoadModelAsync(CancellationToken.None);
                string one = (status ?? "").Replace("\r", "").Replace("\n", " ").Trim();
                App.Log("Irodori 模型状态: " + one);
                if (one.Length == 0) one = "服务已就绪";
                Toast("Irodori：" + (one.Length > 56 ? one.Substring(0, 56) + "…" : one));
            }
            catch (Exception ex)
            {
                App.Log("Irodori 重新加载失败: " + ex.Message);
                Toast("重新加载失败：" + ex.Message, true);
            }
        }

        /// <summary>设置面板里的「检测服务」：先探活，再让服务端确认这份模型配置。</summary>
        private async Task TestIrodoriAsync()
        {
            Toast("正在检测 Irodori-TTS…");
            try
            {
                var tts = IrodoriTts.FromConfig(_cfg);
                string ping = await tts.PingAsync(CancellationToken.None);
                App.Log("Irodori 探活: " + ping);
                string status = await tts.LoadModelAsync(CancellationToken.None);
                string one = (status ?? "").Replace("\r", "").Replace("\n", " ").Trim();
                App.Log("Irodori 模型状态: " + one);
                if (one.Length == 0) one = "服务已就绪";
                Toast("Irodori：" + (one.Length > 56 ? one.Substring(0, 56) + "…" : one));
            }
            catch (Exception ex)
            {
                App.Log("Irodori 检测失败: " + ex.Message);
                Toast("Irodori 检测失败：" + ex.Message, true);
            }
        }

        // ============================ 历史窗口 ============================
        private HistoryWindow _historyWin;
        private WordBookWindow _wordWin;

        public void OpenHistory()
        {
            if (_historyWin != null && _historyWin.IsVisible)
            {
                _historyWin.Activate();
                return;
            }
            _historyWin = new HistoryWindow(this);
            _historyWin.Owner = null;
            _historyWin.Show();
            _historyWin.Refresh();
            _historyWin.PlaceNear(this, _orbX, _orbY);
        }

        /// <summary>打开生词本（查词浮层收藏进来的词）。</summary>
        public void OpenWordBook()
        {
            if (_wordWin != null && _wordWin.IsVisible)
            {
                _wordWin.Activate();
                _wordWin.Refresh();
                return;
            }
            _wordWin = new WordBookWindow(this);
            _wordWin.Owner = null;
            _wordWin.Show();
            _wordWin.Refresh();
            _wordWin.PlaceNear(this, _orbX, _orbY);
        }

        public void UseHistory(HistoryItem it)
        {
            if (it == null) return;
            if (!_inputOpen) OpenInput();
            _suppressAuto = true;
            InputBox.Text = it.input;
            _suppressAuto = false;
            _sameLangResult = false;
            HideCnRow();
            CnBtn.Visibility = Visibility.Collapsed;
            SetResult(it.output ?? "");
            ResultMeta.Text = "来自历史 · " + AppConfig.LangName(it.g);
            ShowResultCard();
            PositionAll();
        }

        // ============================ 演示（--demo，自动截图验证用） ============================
        /// <summary>把整个窗口渲染成 PNG（沙箱里窗口不一定在最前，用这个做视觉核对）。</summary>
        private void SaveShot(string path)
        {
            UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            App.Log("demo shot: 已写出 " + path + " (" + (int)ActualWidth + "x" + (int)ActualHeight + ")");
        }

        public async void RunDemo(string name)
        {
            App.Log("demo: " + name);
            _demoMode = true;
            try
            {
                if (name == "input" || name == "tr" || name == "result" || name == "dropdown" || name == "toast" || name == "settings")
                {
                    OpenInput();
                    InputBox.Text = "こんにちは世界。今日はいい天気ですね。";
                    InputBox.CaretIndex = InputBox.Text.Length;
                }

                switch (name)
                {
                    case "tr":
                        await TranslateAsync();
                        break;

                    case "result":
                        SetResult("你好，世界。今天天气真好啊。\n\n这是一段用于检查排版与换行的较长译文，用来验证行高、颜色与滚动表现是否正常。");
                        ResultMeta.Text = "0.6s · 中文";
                        ShowResultCard();
                        break;

                    // 出图用：输入框一句日文 + 结果卡一句中文，配合 FT_SHOT 生成 README 头图
                    case "hero":
                        OpenInput();
                        _suppressAuto = true;
                        InputBox.Text = "この曲、歌詞が好きで何回も聴いてる。特にサビのところがいい。";
                        InputBox.CaretIndex = InputBox.Text.Length;
                        _suppressAuto = false;
                        SetResult("这首歌的歌词我很喜欢，已经听了好多遍，尤其是副歌那段。");
                        ResultMeta.Text = "0.8s · 中文";
                        ShowResultCard();
                        PositionAll();
                        await Task.Delay(400);
                        {
                            string shot = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (!string.IsNullOrWhiteSpace(shot)) SaveShot(shot);
                        }
                        break;

                    // 真跑一遍「日→日改写 → 点中补译中文」，核对两步提示词与结果
                    case "cnreal":
                        OpenInput();
                        _cfg.source = "auto"; _cfg.target = "ja";   // 自动检测到日语 = 目标，也应算同语言改写
                        _suppressAuto = true;
                        InputBox.Text = "今日は新しい本を読んだ。とても面白かった。";
                        _suppressAuto = false;
                        App.Log("demo cnreal: 检测语言=" + Prompt.Detect(InputBox.Text)
                                + " 提示词=\n" + Prompt.BuildSameLang(InputBox.Text, "ja"));
                        await TranslateAsync();
                        App.Log("demo cnreal: 改写结果=" + ResultText.Text + " 中按钮=" + CnBtn.Visibility);
                        if (CnBtn.Visibility == Visibility.Visible)
                        {
                            CnBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            for (int i = 0; i < 40 && _cnBusy; i++) await Task.Delay(500);
                            App.Log("demo cnreal: 中文行=" + CnText.Text + " 元信息=" + ResultMeta.Text);
                        }
                        break;

                    // 同语言改写 + 「中」按钮补出来的第三行，配合 FT_SHOT 核对布局
                    case "cnline":
                        OpenInput();
                        _suppressAuto = true;
                        InputBox.Text = "今日は新しい本を読んだ。とても面白かった。";
                        _suppressAuto = false;
                        SetResult("今日、私は新しい本を読みました。その本はとても興味深い内容でした。");
                        ResultMeta.Text = "0.8s · 日语改写 · 已补中文";
                        _sameLangResult = true;
                        UpdateCnBtn();
                        CnRow.Visibility = Visibility.Visible;
                        CnText.Text = "今天我读了一本新书。那本书的内容非常有意思。";
                        ShowResultCard();
                        PositionAll();
                        App.Log("demo cnline: 中按钮=" + CnBtn.Visibility + " 中文行=" + CnRow.Visibility
                                + " 卡片高=" + CardHeight(ResultCard).ToString("0.0"));
                        await Task.Delay(500);   // 等显隐动画跑完再截图
                        {
                            string shot = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (!string.IsNullOrWhiteSpace(shot)) SaveShot(shot);
                        }
                        break;

                    // 开发钩子：语音识别卡片 / 词典面板（自动截图检查用）
                    case "asr":
                        OpenInput();
                        OpenAsrCard();
                        SetAsrStatus("示例状态：点「开始录音」后，这里显示录音秒数与识别结果。", false);
                        break;

                    case "dict":
                        SetResult("今日は新しい本を読んだ。とても面白かった。");
                        ResultMeta.Text = "0.8s · 日语";
                        ShowResultCard();
                        {
                            string dd = System.IO.Path.Combine(App.BaseDir, "demo_dict");
                            if (System.IO.Directory.Exists(dd) && _dict != null)
                            {
                                _dict.Load(dd);
                                var hit = _dict.Lookup("読んだ");
                                if (hit != null && hit.Entries.Count > 0) ShowDict(hit, "読んだ");
                            }
                        }
                        break;

                    case "dropdown":
                        ShowPopupList(LangBtn, LangItems(false), _cfg.target, code =>
                        {
                            _cfg.target = code; _cfg.Save();
                            Toast("目标语言：" + AppConfig.LangName(code));
                        });
                        break;

                    case "wide":
                        OpenInput();
                        InputBox.Text = "長文のテストです。日本語の文章をたくさん入力すると、入力欄の高さが伸びて右側のボタンが格子状に並びます。"
                                      + "ここでは入力欄の中に縦スクロールバーが出るかどうかも確認します。"
                                      + "もっと長くしてもう一行、さらに長くしてもう一行、さらに長くしてもう一行、さらに長くしてもう一行。";
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetWideInput(ShouldWidenInput());
                            PositionAll();
                            App.Log("demo wide: 文字宽=" + NaturalTextWidth(InputBox.Text).ToString("0.0")
                                    + " 窄模式可用宽=" + NarrowTextWidth.ToString("0.0")
                                    + " LineCount=" + InputBox.LineCount + " ActualHeight=" + InputBox.ActualHeight
                                    + " wide=" + _wideInput + " 卡片宽=" + InputCard.Width
                                    + " 按钮格=" + BtnBlock.Rows + "x" + BtnBlock.Columns);
                            string shot = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (!string.IsNullOrWhiteSpace(shot)) SaveShot(shot);
                        }), DispatcherPriority.Background);
                        break;

                    // 防抖验证：一个字一个字地加进去，宽度只应该从 400 跳 468 一次（以前会来回跳上百次）；
                    // 再一个字一个字删回去，也只应该跳回 400 一次。
                    case "wideflap":
                        OpenInput();
                        {
                            const string sample = "長文のテストです。日本語の文章をたくさん入力すると、入力欄が伸びて右側のボタンが格子状に並びます。";
                            _suppressAuto = true;
                            InputBox.Clear();
                            var seq = new System.Text.StringBuilder();
                            int flips = 0;
                            double last = InputCard.Width;
                            for (int i = 0; i < sample.Length; i++)
                            {
                                InputBox.Text = sample.Substring(0, i + 1);
                                InputBox.CaretIndex = InputBox.Text.Length;
                                if (InputCard.Width != last) { flips++; last = InputCard.Width; seq.Append(i + 1).Append('→').Append(last).Append(' '); }
                            }
                            int growFlips = flips;
                            double wideW = InputCard.Width;
                            {
                                string shot0 = Environment.GetEnvironmentVariable("FT_SHOT");
                                if (!string.IsNullOrWhiteSpace(shot0)) SaveShot(shot0);   // 加宽后的样子
                            }
                            for (int i = sample.Length; i > 0; i--)
                            {
                                InputBox.Text = sample.Substring(0, i - 1);
                                if (InputCard.Width != last) { flips++; last = InputCard.Width; seq.Append("删" + (i - 1) + "→").Append(last).Append(' '); }
                            }
                            App.Log("demo wideflap: 逐字加长 " + sample.Length + " 字，宽度变化 " + growFlips + " 次（加宽后=" + wideW
                                    + "）；逐字删空后总计 " + flips + " 次 | 轨迹 " + seq.ToString().Trim()
                                    + " | 窄模式可用宽=" + NarrowTextWidth.ToString("0.0"));

                            // 第二轮：1~6 行、每行只有「あ」一个字，验证「按行数加宽」那条分支也不抖
                            last = InputCard.Width;
                            int nlFlips = 0;
                            var nlSeq = new System.Text.StringBuilder();
                            for (int n = 1; n <= 6; n++)
                            {
                                var sb2 = new System.Text.StringBuilder();
                                for (int k = 0; k < n; k++) { if (k > 0) sb2.Append('\n'); sb2.Append('あ'); }
                                InputBox.Text = sb2.ToString();
                                if (InputCard.Width != last) { nlFlips++; last = InputCard.Width; nlSeq.Append(n + "行→" + last + " "); }
                            }
                            App.Log("demo wideflap 换行: 1→6 行宽度变化 " + nlFlips + " 次 | 轨迹 " + nlSeq.ToString().Trim());
                            InputBox.Clear();
                            _suppressAuto = false;
                        }
                        break;

                    case "side-left":
                    case "side-right":
                        _cfg.panel_side = name == "side-right" ? "right" : "left";
                        // 把圆钮挪到屏幕中间，这样左右两侧都放得下，才能看出「优先哪一侧」的差别
                        _orbX = 900; _orbY = 400;
                        OpenInput();
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            PositionAll();
                            App.Log("demo " + name + ": 卡片 x=" + Canvas.GetLeft(InputCard) + " 圆钮 x=" + _orbX
                                    + " 侧=" + _cfg.panel_side);
                        }), DispatcherPriority.Background);
                        break;

                    // 粘贴长文本：验证换行有没有被吞（走的是 TextBox 自己的粘贴通道，等同 Ctrl+V）
                    case "paste":
                        OpenInput();
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int i = 1; i <= 8; i++) sb.Append("第 " + i + " 行：长文本粘贴测试 abcdefghijklmnopqrstuvwxyz\n");
                            string sample = sb.ToString();
                            try { Clipboard.SetText(sample); }
                            catch (Exception ex) { App.Log("demo paste: 写剪贴板失败 " + ex.Message); }
                            InputBox.Clear();
                            InputBox.Paste();
                            var sv = InputBox.Template.FindName("PART_ContentHost", InputBox) as ScrollViewer;
                            App.Log("demo paste: 剪贴板 " + sample.Length + " 字/" + (sample.Split('\n').Length - 1) + " 个换行 → 粘贴后 "
                                    + InputBox.Text.Length + " 字/" + (InputBox.Text.Split('\n').Length - 1) + " 个换行 LineCount="
                                    + InputBox.LineCount + " ScrollableHeight=" + (sv == null ? -1 : sv.ScrollableHeight));
                        }
                        break;

                    // 滚轮：模拟连续向下滚三格，看 VerticalOffset 有没有变（滚轮事件本身没法在沙箱里注入）
                    case "scroll":
                        OpenInput();
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int i = 1; i <= 20; i++) sb.Append("第 " + i + " 行 abcdefghij\n");
                            InputBox.Text = sb.ToString();
                            InputBox.UpdateLayout();
                            _ = Dispatcher.BeginInvoke(new Action(() =>
                            {
                                var sv = InputBox.Template.FindName("PART_ContentHost", InputBox) as ScrollViewer;
                                if (sv == null) { App.Log("demo scroll: 找不到 PART_ContentHost"); return; }
                                double before = sv.VerticalOffset;
                                for (int i = 0; i < 3; i++) ScrollInputBy(-1);   // -1 = 往下滚
                                sv.UpdateLayout();
                                double after = sv.VerticalOffset;
                                sv.ScrollToVerticalOffset(200); sv.UpdateLayout();
                                double direct = sv.VerticalOffset;
                                sv.LineDown(); sv.UpdateLayout();
                                App.Log("demo scroll: ScrollableHeight=" + sv.ScrollableHeight + " 滚动前=" + before
                                        + " 滚三格后=" + after + " 直接设200后=" + direct + " LineDown后=" + sv.VerticalOffset);
                            }), DispatcherPriority.Background);
                        }
                        break;

                    case "settings":
                        OpenSettingsPanel();
                        break;

                    // 设置面板滚到底：看一眼「日语语音（Irodori-TTS）」那一组
                    case "settings-irodori":
                        OpenSettingsPanel();
                        SettingsScroll.UpdateLayout();
                        SettingsScroll.ScrollToEnd();
                        break;

                    // 展开「随机种子」下拉（沙箱里点不到鼠标，用代码触发同一个 Click 处理）
                    case "seed":
                        OpenSettingsPanel();
                        SettingsScroll.UpdateLayout();
                        SettingsScroll.ScrollToEnd();
                        await Task.Delay(200);
                        if (_irSeedBtn != null)
                            _irSeedBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        await Task.Delay(400);
                        App.Log("demo seed: 下拉可见=" + (LangDropdown.Visibility == Visibility.Visible)
                                + " 位置=(" + Canvas.GetLeft(LangDropdown).ToString("0") + "," + Canvas.GetTop(LangDropdown).ToString("0") + ")"
                                + " 尺寸=" + LangDropdown.ActualWidth.ToString("0") + "x" + LangDropdown.ActualHeight.ToString("0")
                                + " Z=" + Panel.GetZIndex(LangDropdown)
                                + " 项数=" + LangList.Children.Count);
                        break;

                    // 指令即时生效：不点「保存」，直接在输入框里改字，看内存配置有没有跟上
                    case "inst":
                        OpenSettingsPanel();
                        await Task.Delay(200);
                        if (_fIns == null) { App.Log("demo inst: 找不到翻译指令输入框"); break; }
                        _fIns.Text = "把下面的文本翻译成日语，使用敬体(です・ます调)，语气客气得体。";
                        if (_constraintSummary != null) _constraintSummary.BringIntoView();
                        App.Log("demo inst: 没点保存时 _cfg.instruction 长度=" + (_cfg.instruction ?? "").Length
                                + "；提示行=" + (_constraintSummary == null ? "(空)" : _constraintSummary.Text));
                        App.Log("demo inst: 实际发出的提示词=\n" + Prompt.Build("我来了", _cfg.target, _cfg.source,
                                _cfg.instruction, _cfg.glossary_on ? _cfg.glossary : "", _cfg.genre));
                        break;

                    // 指令预设：点内置预设 / 存自定义 / 删掉，各走一遍并打印实际提示词
                    case "preset":
                        OpenSettingsPanel();
                        await Task.Delay(250);
                        App.Log("demo preset: 预设数=" + (_cfg.instruction_presets == null ? -1 : _cfg.instruction_presets.Count)
                                + " 按钮数=" + (_presetBar == null ? -1 : _presetBar.Children.Count));
                        if (_presetBar != null && _presetBar.Children.Count > 1)
                        {
                            var pb = _presetBar.Children[1] as Button;
                            if (pb != null)
                            {
                                pb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                await Task.Delay(200);
                                App.Log("demo preset: 套用「" + pb.Content + "」后 instruction=[" + _cfg.instruction + "]");
                                App.Log("demo preset: 提示行=" + (_constraintSummary == null ? "(空)" : _constraintSummary.Text));
                                App.Log("demo preset: 实际发出的提示词=\n" + Prompt.Build("Hello **world** and `code`", _cfg.target, _cfg.source,
                                        _cfg.instruction, _cfg.glossary_on ? _cfg.glossary : "", _cfg.genre));
                            }
                        }
                        if (_fIns != null)
                        {
                            _fIns.Text = "把文本翻译成日语，尽量口语化，像日常聊天。";
                            SaveInstructionPreset("口语日语", _fIns.Text);
                            RebuildPresetBar();
                            App.Log("demo preset: 存自定义后 预设数=" + _cfg.instruction_presets.Count
                                    + " 按钮数=" + (_presetBar == null ? -1 : _presetBar.Children.Count)
                                    + " 名字=" + string.Join("/", _cfg.instruction_presets.ConvertAll(p => p.name)));
                            App.Log("demo preset: 删「口语日语」=" + DeleteInstructionPreset("口语日语")
                                    + " 之后预设数=" + _cfg.instruction_presets.Count);
                        }
                        break;

                    // 热键合流：卡片没开时按热键 = 先取词（此刻卡片仍没开）→ 取不到回落剪贴板 → 再开卡片翻译
                    case "hotkey":
                        if (_inputOpen) CloseInput();
                        SetClipboardText("热键兜底测试：剪贴板里的这句话应当被翻出来。");
                        App.Log("demo hotkey: 开始前 卡片已开=" + _inputOpen
                                + " 前台是本窗口=" + (SelectionGrab.ForegroundWindow() == WindowHandle())
                                + " 剪贴板=" + GetClipboardText());
                        await HotkeyTranslateAsync();
                        await Task.Delay(800);
                        App.Log("demo hotkey: 结束后 卡片已开=" + _inputOpen + " 输入框=[" + InputBox.Text + "]"
                                + " 剪贴板还原=[" + GetClipboardText() + "]");
                        break;

                    // 热键取词的完整链路：开一个「别的窗口」并选中文字 → 按热键，应当取到选中的文字而不是剪贴板
                    case "hotkey-sel":
                        {
                            var w = new Window
                            {
                                Width = 460, Height = 170, Left = 320, Top = 320, Topmost = true,
                                Title = "取词目标窗口", WindowStyle = WindowStyle.ToolWindow,
                                Background = System.Windows.Media.Brushes.White,
                            };
                            var tb = new TextBox
                            {
                                Text = "这段选中的文字应当被热键取走并翻译。",
                                FontSize = 16, Margin = new Thickness(12), AcceptsReturn = true,
                                TextWrapping = TextWrapping.Wrap,
                            };
                            w.Content = tb;
                            w.Show();
                            tb.SelectAll();
                            w.Activate();
                            tb.Focus();
                            System.Windows.Input.Keyboard.Focus(tb);
                            await Task.Delay(700);
                            SetClipboardText("剪贴板里的旧内容（不该被翻出来）");
                            IntPtr target = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                            App.Log("demo hotkey-sel: 目标窗口在前台=" + (SelectionGrab.ForegroundWindow() == target)
                                    + " 剪贴板(取词前)=[" + GetClipboardText() + "]");
                            await HotkeyTranslateAsync();
                            await Task.Delay(800);
                            App.Log("demo hotkey-sel: 输入框=[" + InputBox.Text + "] 取词后剪贴板=[" + GetClipboardText() + "]");
                            w.Close();
                        }
                        break;

                    // 实时字幕 + 点词浮层：确认「字幕里点词」能弹词典，且浮层贴着点的位置（截图用 FT_SHOT）
                    case "capdict":
                        {
                            OpenInput();
                            OpenAsrCard();
                            _liveItems.Clear();
                            _liveItems.Add(new LiveItem { Id = 1, Jp = "今日は新しい本を読んだ。", Zh = "今天读了新书。" });
                            _liveItems.Add(new LiveItem { Id = 2, Jp = "とても面白かった。", Zh = "非常有趣。" });
                            RenderCaptions();
                            string dd = System.IO.Path.Combine(App.BaseDir, "demo_dict");
                            bool real = Environment.GetEnvironmentVariable("FT_DICT_REAL") == "1";   // 用真词典跑「按需加载」
                            if (_dict != null)
                            {
                                if (real)
                                {
                                    string envDir = Environment.GetEnvironmentVariable("FT_DICT_DIR");
                                    if (!string.IsNullOrWhiteSpace(envDir)) _cfg.dict_dir = envDir;
                                    bool ok = await EnsureDictionaryAsync();
                                    App.Log("demo capdict: 按需加载=" + ok + " 条=" + _dict.EntryCount
                                            + " 内存=" + (_dict.MemoryBytes / 1048576) + "MB 耗时=" + _dict.LoadMs + "ms");
                                }
                                else if (System.IO.Directory.Exists(dd))
                                {
                                    await _dict.LoadAsync(dd, CancellationToken.None);
                                }
                                else dd = "";
                                if (real || dd.Length > 0)
                                {
                                    _dictAnchor = new Point(Width / 2 - 40, Height - 250);   // 模拟点在字幕上
                                    var hit = _dict.Lookup(real ? "読んだ" : "読んだ");
                                    if (hit != null && hit.Entries.Count > 0) ShowDict(hit, "読んだ");
                                    else App.Log("demo capdict: 词典里没查到「読んだ」");
                                    if (Environment.GetEnvironmentVariable("FT_STAR") == "1") ToggleWordStar();
                                    App.Log("demo capdict: 词典=" + _dict.Ready + " 条=" + _dict.EntryCount
                                            + " 浮层=(" + Canvas.GetLeft(DictPanel) + "," + Canvas.GetTop(DictPanel) + ")"
                                            + " 字幕卡=(" + Canvas.GetLeft(AsrCard) + "," + Canvas.GetTop(AsrCard) + ")");
                                }
                            }
                            await Task.Delay(300);
                            string shot = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (!string.IsNullOrWhiteSpace(shot))
                            {
                                UpdateLayout();
                                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                    (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96,
                                    System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(this);
                                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                                using (var fs = System.IO.File.Create(shot)) enc.Save(fs);
                                App.Log("demo capdict: 已写出 " + shot);
                            }
                            if (Environment.GetEnvironmentVariable("FT_SHOT_EXIT") == "1")
                            {
                                await Task.Delay(300);
                                Close();
                            }
                        }
                        break;

                    // 生词本窗口：空的话先塞两条示例，再打开（FT_SHOT 截图）
                    case "wordbook":
                        {
                            if (App.Words != null && App.Words.Items.Count == 0)
                            {
                                App.Words.Add("食べる", "たべる", "1-dan",
                                    "• to eat\n• to live on; to live off", "毎朝パンを食べる。");
                                App.Words.Add("面白い", "おもしろい", "adjective",
                                    "interesting; amusing; funny", "とても面白かった。");
                            }
                            OpenWordBook();
                            await Task.Delay(600);
                            string shot = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (!string.IsNullOrWhiteSpace(shot) && _wordWin != null)
                            {
                                _wordWin.UpdateLayout();
                                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                    (int)Math.Ceiling(_wordWin.ActualWidth), (int)Math.Ceiling(_wordWin.ActualHeight),
                                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(_wordWin);
                                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                                using (var fs = System.IO.File.Create(shot)) enc.Save(fs);
                                App.Log("demo wordbook: 已写出 " + shot + " 词数=" + App.Words.Items.Count);
                            }
                            if (Environment.GetEnvironmentVariable("FT_SHOT_EXIT") == "1")
                            {
                                await Task.Delay(300);
                                Close();
                            }
                        }
                        break;

                    // 把窗口渲染成 PNG（沙箱里窗口不保证在最前，用这个做视觉核对；路径取 FT_SHOT）
                    case "shot":
                        OpenSettingsPanel();
                        await Task.Delay(250);
                        if (_fIns != null)
                        {
                            _fIns.Text = "把下面的文本翻译成日语，使用敬体(です・ます调)，语气客气得体。";
                            if (_constraintSummary != null) _constraintSummary.BringIntoView();
                            await Task.Delay(250);
                        }
                        if (Environment.GetEnvironmentVariable("FT_SHOT_TOP") == "1")
                        {
                            SettingsScroll.ScrollToTop();
                            await Task.Delay(200);
                        }
                        {
                            string path = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (string.IsNullOrWhiteSpace(path))
                                path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot.png");
                            UpdateLayout();
                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96,
                                System.Windows.Media.PixelFormats.Pbgra32);
                            rtb.Render(this);
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                            App.Log("demo shot: 已写出 " + path + " (" + (int)ActualWidth + "x" + (int)ActualHeight + ")");
                        }
                        {
                            // 想看设置面板下半截（热键等）时用 FT_SHOT_SCROLL=bottom（或像素偏移）
                            string sc = Environment.GetEnvironmentVariable("FT_SHOT_SCROLL");
                            if (!string.IsNullOrWhiteSpace(sc))
                            {
                                double off;
                                if (sc == "bottom") SettingsScroll.ScrollToEnd();
                                else if (sc.StartsWith("-") && double.TryParse(sc.Substring(1), System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out off))
                                    SettingsScroll.ScrollToVerticalOffset(Math.Max(0, SettingsScroll.ScrollableHeight - off));
                                else if (double.TryParse(sc, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out off))
                                    SettingsScroll.ScrollToVerticalOffset(off);
                                else App.Log("demo shot: FT_SHOT_SCROLL 无法识别: " + sc);
                                await Task.Delay(250);
                                App.Log("demo shot: 滚动 " + sc + " 后 VerticalOffset=" + SettingsScroll.VerticalOffset
                                        + " ScrollableHeight=" + SettingsScroll.ScrollableHeight);
                                string path2 = Environment.GetEnvironmentVariable("FT_SHOT2");
                                if (!string.IsNullOrWhiteSpace(path2))
                                {
                                    UpdateLayout();
                                    var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                        (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96,
                                        System.Windows.Media.PixelFormats.Pbgra32);
                                    rtb2.Render(this);
                                    var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                    enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb2));
                                    using (var fs2 = System.IO.File.Create(path2)) enc2.Save(fs2);
                                    App.Log("demo shot: 滚动后已写出 " + path2);
                                }
                            }
                        }
                        // 供自动化截图：写完图后自动退出，避免高权限进程无法从沙箱结束
                        if (Environment.GetEnvironmentVariable("FT_SHOT_EXIT") == "1")
                        {
                            await Task.Delay(400);
                            Close();
                        }
                        break;

                    // 音色设计模式：caption + 无参考音频 + sway 8 步，真合成一次（演示完把配置改回去）
                    // ir-joint 用来验证 joint 时只留一个全局权重（走 cfg_scale_raw）
                    case "ir-design":
                    case "ir-joint":
                        {
                            string m0 = _cfg.irodori_mode, c0 = _cfg.irodori_caption, s0 = _cfg.irodori_num_steps,
                                   t0 = _cfg.irodori_t_schedule, g0 = _cfg.irodori_cfg_mode, r0 = _cfg.irodori_ref_wav;
                            bool n0 = _cfg.irodori_no_ref;
                            double w0 = _cfg.irodori_sway_coeff, j0 = _cfg.irodori_cfg_joint;
                            _cfg.irodori_mode = "design";
                            _cfg.irodori_caption = "落ち着いた女性の声で、囁いてください";
                            _cfg.irodori_no_ref = true;
                            _cfg.irodori_ref_wav = "";
                            _cfg.irodori_num_steps = "8";
                            _cfg.irodori_t_schedule = "sway";
                            _cfg.irodori_sway_coeff = -1.0;
                            _cfg.irodori_cfg_mode = name == "ir-joint" ? "joint" : "independent";
                            _cfg.irodori_cfg_joint = 4.0;
                            var tts = IrodoriTts.FromConfig(_cfg, ResolveSeed());
                            App.Log("demo " + name + ": caption=[" + tts.Caption + "] no_ref=" + _cfg.irodori_no_ref
                                    + " steps=[" + tts.NumSteps + "] t=" + tts.TScheduleMode + " sway=" + tts.SwayCoeff
                                    + " cfg=" + tts.CfgGuidanceMode + " raw=[" + tts.CfgScaleRaw + "] refs=" + tts.RefWavs.Count);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                string wav = await tts.SynthesizeAsync("こんにちは、今日はいい天気ですね。", CancellationToken.None);
                                sw.Stop();
                                App.Log("demo " + name + ": 合成成功 " + sw.ElapsedMilliseconds + "ms wav=" + wav);
                                App.Log("demo " + name + ": RunLog=" + tts.LastRunLog);
                            }
                            catch (Exception ex)
                            {
                                sw.Stop();
                                App.Log("demo " + name + ": 合成失败(" + sw.ElapsedMilliseconds + "ms) " + ex.Message);
                            }
                            _cfg.irodori_mode = m0; _cfg.irodori_caption = c0; _cfg.irodori_no_ref = n0;
                            _cfg.irodori_num_steps = s0; _cfg.irodori_t_schedule = t0; _cfg.irodori_cfg_mode = g0;
                            _cfg.irodori_ref_wav = r0; _cfg.irodori_sway_coeff = w0; _cfg.irodori_cfg_joint = j0;
                            break;
                        }

                    // LoRA 模式：只带 LoRA 文件夹（无 Caption、无参考音频），采样参数与设计模式相同，真合成一次
                    case "ir-lora":
                        {
                            string m0 = _cfg.irodori_mode, c0 = _cfg.irodori_caption, s0 = _cfg.irodori_num_steps,
                                   t0 = _cfg.irodori_t_schedule, g0 = _cfg.irodori_cfg_mode, r0 = _cfg.irodori_ref_wav,
                                   l0 = _cfg.irodori_lora_dir;
                            bool n0 = _cfg.irodori_no_ref;
                            double w0 = _cfg.irodori_sway_coeff;
                            _cfg.irodori_mode = "lora";
                            _cfg.irodori_lora_dir = Environment.GetEnvironmentVariable("FT_LORA") ?? l0;
                            _cfg.irodori_caption = "这段文字在 LoRA 模式下应该被忽略";
                            _cfg.irodori_no_ref = true;
                            _cfg.irodori_ref_wav = "";
                            ApplyIrodoriModeUi();
                            var tts = IrodoriTts.FromConfig(_cfg, ResolveSeed());
                            App.Log("demo ir-lora: lora=[" + tts.LoraAdapterDir + "] caption=[" + tts.Caption + "] no_ref=" + _cfg.irodori_no_ref
                                    + " steps=[" + tts.NumSteps + "] t=" + tts.TScheduleMode + " sway=" + tts.SwayCoeff
                                    + " cfg=" + tts.CfgGuidanceMode + "/" + tts.CfgScaleText + "," + tts.CfgScaleCaption + "," + tts.CfgScaleSpeaker
                                    + " refs=" + tts.RefWavs.Count + " lora行可见=" + (_irLoraRows.Count > 0 && _irLoraRows[0].Visibility == Visibility.Visible)
                                    + " 设计行可见=" + (_irDesignRows.Count > 0 && _irDesignRows[0].Visibility == Visibility.Visible));
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                string wav = await tts.SynthesizeAsync("こんにちは、今日はいい天気ですね。", CancellationToken.None);
                                sw.Stop();
                                App.Log("demo ir-lora: 合成成功 " + sw.ElapsedMilliseconds + "ms wav=" + wav);
                                App.Log("demo ir-lora: RunLog=" + tts.LastRunLog);
                            }
                            catch (Exception ex)
                            {
                                sw.Stop();
                                App.Log("demo ir-lora: 合成失败(" + sw.ElapsedMilliseconds + "ms) " + ex.Message);
                            }
                            _cfg.irodori_mode = m0; _cfg.irodori_caption = c0; _cfg.irodori_no_ref = n0;
                            _cfg.irodori_num_steps = s0; _cfg.irodori_t_schedule = t0; _cfg.irodori_cfg_mode = g0;
                            _cfg.irodori_ref_wav = r0; _cfg.irodori_sway_coeff = w0; _cfg.irodori_lora_dir = l0;
                            break;
                        }

                    // 动态生效自检：往 Caption 里写字（不点任何按钮）→ 看配置 / 提示行 / 面板上还有没有「保存」
                    case "ir-live":
                        {
                            OpenSettingsPanel();
                            await Task.Delay(200);
                            _cfg.irodori_mode = "design";
                            ApplyIrodoriModeUi();
                            await Task.Delay(150);
                            if (_fIrCap != null)
                            {
                                _fIrCap.Text = "落ち着いた女性の声で、囁いてください";
                                await Task.Delay(200);
                                App.Log("demo ir-live: 没点保存时 _cfg.irodori_caption=[" + _cfg.irodori_caption + "]");
                                App.Log("demo ir-live: warnNull=" + (_irCaptionWarn == null) + " capNull=" + (_fIrCap == null)
                                        + " 提示行=[" + (_irCaptionWarn == null ? "" : _irCaptionWarn.Text) + "]");
                            }
                            int rows = _irDesignRows.Count, indep = 0;
                            foreach (var r in _irIndepRows) if (r != null && r.Visibility == Visibility.Visible) indep++;
                            App.Log("demo ir-live: 模式=" + _cfg.irodori_mode + " design行=" + rows + " 可见独立滑块=" + indep
                                    + " 全局滑块可见=" + (_irJointRow != null && _irJointRow.Visibility == Visibility.Visible));
                            // joint：三个独立滑块应隐藏，全局滑块出现
                            _cfg.irodori_cfg_mode = "joint";
                            UpdateCfgRows();
                            int indep2 = 0;
                            foreach (var r in _irIndepRows) if (r != null && r.Visibility == Visibility.Visible) indep2++;
                            App.Log("demo ir-live: 切 joint 后 可见独立滑块=" + indep2
                                    + " 全局滑块可见=" + (_irJointRow != null && _irJointRow.Visibility == Visibility.Visible));
                            // clone：design 行应全部隐藏
                            _cfg.irodori_mode = "clone";
                            _cfg.irodori_cfg_mode = "independent";
                            ApplyIrodoriModeUi();
                            int vis = 0;
                            foreach (var r in _irDesignRows) if (r != null && r.Visibility == Visibility.Visible) vis++;
                            App.Log("demo ir-live: 切克隆模式后 可见 design 行=" + vis);
                            bool hasSave = false;
                            foreach (var el in WalkSettings())
                                if (el is Button && string.Equals(((Button)el).Content as string, "保存", StringComparison.Ordinal)) { hasSave = true; break; }
                            App.Log("demo ir-live: 面板里还有「保存」按钮吗 = " + hasSave);
                            await Task.Delay(100);
                            break;
                        }

                    // 音色设计模式的设置面板截图（FT_SHOT / FT_SHOT_SCROLL 同 shot）
                    case "ir-shot":
                        OpenSettingsPanel();
                        await Task.Delay(200);
                        _cfg.irodori_mode = "design";
                        ApplyIrodoriModeUi();
                        await Task.Delay(200);
                        if (_fIrCap != null)
                        {
                            _fIrCap.Text = "落ち着いた女性の声で、囁いてください";
                            _fIrCap.BringIntoView();
                            await Task.Delay(250);
                        }
                        {
                            // 想继续往下看（CFG / 采样控制）时用 FT_SHOT_SCROLL=400 之类
                            string sc0 = Environment.GetEnvironmentVariable("FT_SHOT_SCROLL");
                            double off0;
                            if (!string.IsNullOrWhiteSpace(sc0) && double.TryParse(sc0, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out off0))
                            {
                                SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset + off0);
                                await Task.Delay(250);
                                App.Log("demo ir-shot: 滚动 +" + off0 + " 后 VerticalOffset=" + SettingsScroll.VerticalOffset
                                        + " ScrollableHeight=" + SettingsScroll.ScrollableHeight);
                            }
                        }
                        {
                            string path = Environment.GetEnvironmentVariable("FT_SHOT");
                            if (string.IsNullOrWhiteSpace(path))
                                path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "irodori_shot.png");
                            UpdateLayout();
                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96,
                                System.Windows.Media.PixelFormats.Pbgra32);
                            rtb.Render(this);
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                            App.Log("demo ir-shot: 已写出 " + path + " (" + (int)ActualWidth + "x" + (int)ActualHeight + ")");
                        }
                        break;

                    case "toast":
                        Toast("已复制译文");
                        break;

                    case "history":
                        if (App.History.Items.Count == 0)
                        {
                            App.History.Add("こんにちは世界。今日はいい天気ですね。", "你好，世界。今天天气真好啊。", "auto", "zh");
                            App.History.Add("The quick brown fox jumps over the lazy dog.", "敏捷的棕色狐狸跳过了懒狗。", "auto", "zh");
                            App.History.Add("人工智能改变了我们的生活。", "人工知能は私たちの生活を変えました。", "zh", "ja");
                        }
                        OpenHistory();
                        break;

                    // 不点按钮也能试听，用来判断朗读在当前环境下是否可用
                    case "speak":
                        SetResult("你好，世界。今天天气真好啊。");
                        ShowResultCard();
                        SpeakResult(true);
                        break;

                    // 模拟鼠标经过圆钮（沙箱里无法真正注入鼠标移动，只能直接调处理函数）
                    case "hover":
                        OrbHost_MouseEnter(OrbHost, null);
                        break;

                    // 走一遍退出按钮的处理链路（沙箱里点不到鼠标，用它验证「点退出 → 存设置 → 关进程」）
                    case "exit":
                        QuitApp();
                        break;

                    // 只看切分规则（不联网、不合成）：把内置长文的切分结果写进日志，便于离线核对
                    case "split":
                        {
                            string sample =
                                "こんにちは世界。今日はいい天気ですね。昨日は一日中雨が降っていました。"
                                + "明日は友達と一緒に図書館へ行く予定です。日本語の勉強を続けるつもりです。"
                                + "夜には新しい単語を五十個覚えました。少しずつ上手になっていると感じます。"
                                + "しかし、まだ聞き取りは苦手です。もっと練習しなければならないと思います。"
                                + "これはとても長い一文で、途中に読点がなく、文節の切れ目として安全な場所を探しながら"
                                + "五十文字を超えたところで折り返す必要があるかどうかを確かめるための例文です。";
                            var cs = SplitForSpeech(sample);
                            string spfx = _cfg.irodori_chunk_prefix ?? "";
                            App.Log("split: " + sample.Length + " 字 → " + cs.Count + " 段（上限 " + _cfg.irodori_chunk_chars + " 字）"
                                + (spfx.Length > 0 ? "，切分前缀=\"" + spfx + "\"（第 2 段起拼接）" : "，无切分前缀"));
                            for (int i = 0; i < cs.Count; i++)
                                App.Log("  [" + (i + 1) + "/" + cs.Count + "] " + cs[i].Length + " 字: " + cs[i]);
                            // 顺带把「真正发给 API 的文本」也打出来，方便离线核对前缀有没有加对位置
                            if (spfx.Length > 0 && cs.Count > 1)
                                for (int i = 1; i < cs.Count; i++)
                                    App.Log("  发送[" + (i + 1) + "/" + cs.Count + "]: " + spfx + cs[i]);
                            break;
                        }

                    // JSON 源文 + 提到 JSON 的指令：应走官方的 JSON 专用收尾语（演示不保存设置）
                    case "json":
                        {
                            OpenInput();
                            _suppressAuto = true;
                            InputBox.Text = "{\"title\": \"ユーザー規約\", \"version\": \"1.0.0\"}";
                            _suppressAuto = false;
                            _cfg.instruction = "严格保留合法 JSON 语法格式，仅翻译 value 文本";
                            _cfg.genre = "";
                            _ = TranslateAsync();
                            break;
                        }

                    // 图片翻译：把 exe 同目录的 demo.png 挂上并直接翻译（沙箱里点不到文件对话框）
                    case "image":
                        {
                            string p = System.IO.Path.Combine(App.BaseDir, "demo.png");
                            if (System.IO.File.Exists(p))
                            {
                                OpenInput();
                                LoadImageFile(p, true);
                            }
                            else App.Log("demo image: 找不到 " + p);
                            break;
                        }

                    // 目标语言=日语：结果卡上应多出「あ」按钮（第三方语音，🔊 仍在）
                    // irodori-say 会真的合成并播放（演示不修改用户保存的设置）
                    // irodori-long 放一段多句长文，用来验证「分段 + 边放边合成」
                    case "irodori":
                    case "irodori-say":
                    case "irodori-long":
                    case "irodori-hard":
                    case "irodori-cut":
                    case "irodori-replay":
                        {
                            string oldTarget = _cfg.target;
                            _cfg.target = "ja";
                            string demoText = "こんにちは世界。今日はいい天気ですね。";
                            if (name == "irodori-long")
                                demoText = "こんにちは世界。今日はいい天気ですね。昨日は一日中雨が降っていました。"
                                  + "明日は友達と一緒に図書館へ行く予定です。日本語の勉強を続けるつもりです。"
                                  + "夜には新しい単語を五十個覚えました。少しずつ上手になっていると感じます。";
                            else if (name == "irodori-hard")
                                // 用户报过「第二段读到一半被跳过」的原句（多段 + 长段，回归用）
                                demoText = "何度言っても構わない。他人が何と言おうと、私たちは自分らしい人生を生きていく権利がある。"
                                  + "私の言っていることはおかしいだろうか？わがままだろうか？"
                                  + "しかし、誰と比べて「おかしい」と言われるのだろうか？その人の生き方が正しいと、誰が証明できるというのだろう？";
                            else if (name == "irodori-cut")
                                // 第二次报掐断时给的原句（两段，正好会出现「第 2 段被切」）
                                demoText = "私たちは学校ではあまり話さず、放課後になってわざわざ学校から遠く離れた、人気のない海岸で会うことにしていた。"
                                  + "この島では、高校生の男女が少しでも親しくしていると、付き合っているのだと思われてしまう。"
                                  + "ベッドで眠る女を思い出した。色白の寝顔、長いまつ毛、メッシュを入れたスモーキーグレーの巻き髪が華奢な肩にかかっている。"
                                  + "暁海とは正反対のタイプだ。";
                            SetResult(demoText);
                            ResultMeta.Text = "0.6s · 日语";
                            ShowResultCard();
                            if (name != "irodori")
                            {
                                await SpeakIrodoriAsync();
                                App.Log("demo " + name + ": 第一轮结束，重播按钮可见=" + (ReplayBtn.Visibility == Visibility.Visible)
                                        + " 记下的段数=" + _lastSpeakWavs.Count);
                                string shotPath = Environment.GetEnvironmentVariable("FT_SHOT");
                                if (!string.IsNullOrWhiteSpace(shotPath))
                                {
                                    await Task.Delay(300);
                                    var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                        (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96,
                                        System.Windows.Media.PixelFormats.Pbgra32);
                                    rtb2.Render(this);
                                    var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                    enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb2));
                                    using (var fs2 = System.IO.File.Create(shotPath)) enc2.Save(fs2);
                                    App.Log("demo " + name + ": 已写出 " + shotPath);
                                }
                                if (name == "irodori-replay")
                                {
                                    await ReplayIrodoriAsync();
                                    App.Log("demo irodori-replay: 重播结束，重播按钮可见=" + (ReplayBtn.Visibility == Visibility.Visible)
                                            + " 记下的段数=" + _lastSpeakWavs.Count);
                                }
                            }
                            _cfg.target = oldTarget;
                            break;
                        }
                }
            }
            catch (Exception ex) { App.Log("demo 失败: " + ex.Message); }
        }
    }
}
