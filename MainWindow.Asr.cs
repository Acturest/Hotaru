using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FloatingTranslator
{
    // 实时字幕（本地 sherpa-onnx / Parakeet 日语 INT8，听电脑播放的声音）与点词查词（Yomitan 词典）
    public partial class MainWindow
    {
        private const string AsrModelUrl =
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt_ctc-0.6b-ja-35000-int8.tar.bz2";
        private const string JitendexUrl = "https://jitendex.org/";

        private SherpaAsr _asr;
        private YomitanDictionary _dict;
        private LoopbackCapture _loop;
        private CancellationTokenSource _liveCts;
        private readonly object _liveLock = new object();
        private readonly List<float> _liveBuf = new List<float>();
        private readonly List<string> _liveLines = new List<string>();
        private readonly List<LiveItem> _liveItems = new List<LiveItem>();
        private readonly StringBuilder _liveTranscript = new StringBuilder();
        private readonly SemaphoreSlim _transGate = new SemaphoreSlim(2, 2);
        private DispatcherTimer _captionTimer;
        private CancellationTokenSource _liveTransCts;
        private volatile bool _captionDirty;
        private bool _liveFollow = true;
        private bool _liveTransErrorNotified;
        private int _liveItemId;
        private string _livePartial = "";
        private bool _liveRunning;
        private bool _liveLoading;
        private bool _liveHasVoice;
        private int _liveRate = 48000;
        private int _liveFinalCount;
        private DateTime _liveLastPartial;
        private DateTime _liveVoiceAt;
        private bool _dictLoading;
        private Point _dictAnchor;              // 点词的位置（Root 坐标）：词典浮层贴着它弹
        private DictResult _dictHit;            // 浮层当前显示的查词结果（收藏按钮要用）
        private string _dictUsed = "";          // 触发这次查询的词（可能是活用形）
        private string _dictSentence = "";      // 这个词所在的原句
        private bool _asrInited;
        private CancellationTokenSource _dictLoadCts;
        private TextBlock _asrHint;
        private TextBlock _dictHint;
        private FrameworkElement _asrSectionAnchor;
        private Brush _asrIdleFill;
        private Brush _asrIdleStroke;

        private static readonly FontFamily JpFont =
            new FontFamily("Yu Gothic UI, Meiryo, Microsoft YaHei UI");

        /// <summary>一条字幕：日语原文 + 中文译文（译文可能还在流式生成）。</summary>
        private sealed class LiveItem
        {
            public int Id;
            public string Jp;
            public string Zh = "";
            public bool Translating;
            public bool Failed;
        }

        private Brush Res(string key) { return (Brush)FindResource(key); }

        // ============================ 初始化 ============================
        private void InitAsr()
        {
            if (_asrInited) return;
            _asrInited = true;

            _asr = new SherpaAsr();
            _dict = new YomitanDictionary();
            _dict.Progress += (p, msg) => Dispatcher.BeginInvoke(new Action(() => SetDictHint(msg, false)));

            _asrIdleFill = AsrBtnBg.Fill;
            _asrIdleStroke = AsrBtnBg.Stroke;

            AsrBtnHost.PreviewMouseLeftButtonDown += (s, e) => { e.Handled = true; OnAsrButton(); };
            AsrBtnHost.MouseEnter += (s, e) => { if (!_liveRunning) AsrBtnIcon.Stroke = Res("AccentBrush"); };
            AsrBtnHost.MouseLeave += (s, e) =>
            {
                if (!_liveRunning) AsrBtnIcon.Stroke = Res("MutedBrush");
                AsrBtnHost.Opacity = 1;
            };
            AsrActionBtn.Click += (s, e) => OnAsrButton();
            AsrTranslateBtn.Click += async (s, e) => await TranslateCaptionsAsync();
            AsrCloseBtn.Click += (s, e) => CloseAsrCard(true);
            AsrSettingsBtn.Click += (s, e) =>
            {
                if (SettingsPanel.Visibility == Visibility.Visible) CloseSettingsPanel();
                else OpenSettingsPanel();
            };
            DictCloseBtn.Click += (s, e) => HideDictPanel();
            DictStarBtn.Click += (s, e) => ToggleWordStar();
            // 释义长短不同 → 浮层高度会变，量到真实高度后重新贴一次，免得压住字幕
            DictPanel.SizeChanged += (s, e) => PositionDictPanel();

            ResultText.PreviewMouseLeftButtonUp += ResultText_MouseLeftButtonUp;
            ResultText.ToolTip = "点击日语单词查词典（自动还原活用形）";
            AsrText.PreviewMouseLeftButtonUp += Caption_MouseLeftButtonUp;
            AsrText.ToolTip = "点击字幕里的日语单词查词典；滚轮上翻可回看历史字幕";
            AsrTextScroll.ScrollChanged += (s, e) =>
            {
                if (e.VerticalChange == 0) return;
                _liveFollow = AsrTextScroll.ScrollableHeight - AsrTextScroll.VerticalOffset < 16;
            };

            _captionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _captionTimer.Tick += (s, e) =>
            {
                if (!_captionDirty) return;
                _captionDirty = false;
                RenderCaptions();
            };
            _captionTimer.Start();

            Root.MouseLeftButtonDown += Root_AsrOutsideClick;
            Closed += (s, e) => CleanupAsr();

            UpdateAsrHint();
            // 索引常驻（Jitendex 44 万条约 70 MB），释义在查到时按需读。默认第一次点词时才建索引，
            // 免得不开字幕也白占内存；不想每次等这十几秒，可以在设置里勾「启动时预加载」。
            if (_cfg.dict_preload && !string.IsNullOrWhiteSpace(_cfg.dict_dir)) ReloadDictionary(_cfg.dict_dir, false);
            else if (!string.IsNullOrWhiteSpace(_cfg.dict_dir))
                SetDictHint("词典未加载：点词时自动建索引（约 3 秒 / 70 MB）", false);
            else SetDictHint("未加载词典：在设置底部的「词典（点词查词）」里选择 Jitendex 文件夹。", false);
        }

        private void Root_AsrOutsideClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;
            Point p = e.GetPosition(Root);
            if (AsrCard.Visibility == Visibility.Visible &&
                AsrCard.IsVisible && !IsInside(AsrCard, p) && !IsInside(AsrBtnHost, p))
            {
                // 聆听中不自动收起，避免字幕突然消失
                if (!_liveRunning && !_liveLoading) CloseAsrCard(false);
            }
            if (DictPanel.Visibility == Visibility.Visible &&
                DictPanel.IsVisible && !IsInside(DictPanel, p) && !IsInside(ResultCard, p))
                HideDictPanel();
        }

        private bool IsInside(FrameworkElement el, Point rootPoint)
        {
            if (el == null || !el.IsVisible) return false;
            try
            {
                Point p = el.TranslatePoint(new Point(0, 0), Root);
                return rootPoint.X >= p.X && rootPoint.X <= p.X + el.ActualWidth
                    && rootPoint.Y >= p.Y && rootPoint.Y <= p.Y + el.ActualHeight;
            }
            catch { return false; }
        }

        private void CleanupAsr()
        {
            try { StopListening(); } catch { }
            try { if (_asr != null) _asr.Dispose(); } catch { }
            _asr = null;
            _dict = null;
        }

        // ============================ 实时字幕按钮 / 字幕卡 ============================
        private void OnAsrButton()
        {
            if (_liveLoading) { StopListening(); return; }
            if (_liveRunning) { StopListening(); return; }
            StartListening();
        }

        private void OpenAsrCard()
        {
            if (AsrCard.Visibility != Visibility.Visible)
            {
                AsrCard.Visibility = Visibility.Visible;
                ShowEl(AsrCard, 220);
                AnimScale(AsrScale, 1, 220);
            }
            PositionAsrCard();
            UpdateAsrHint();
        }

        private void CloseAsrCard(bool stopListening)
        {
            if (_liveRunning || _liveLoading)
            {
                if (!stopListening) return;
                StopListening();
            }
            HideEl(AsrCard, 160);
            HideDictPanel();
        }

        private async void StartListening()
        {
            if (_liveRunning || _liveLoading) return;

            string mp, tp, err;
            if (!TryReadModel(out mp, out tp, out err))
            {
                OpenAsrCard();
                SetAsrStatus(err, true);
                UpdateAsrHint();
                OpenSettingsPanel();
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { _asrSectionAnchor?.BringIntoView(); } catch { }
                }), DispatcherPriority.Background);
                return;
            }

            OpenAsrCard();
            _liveLoading = true;
            SetAsrButtonVisual(true);
            AsrActionBtn.Content = "停止聆听";
            _liveFinalCount = 0;
            _livePartial = "";
            _liveHasVoice = false;
            lock (_liveLock)
            {
                _liveBuf.Clear();
                _liveLines.Clear();
                _liveItems.Clear();
                _liveTranscript.Clear();
            }
            _liveFollow = true;
            _captionDirty = true;
            _liveTransErrorNotified = false;
            AsrText.Inlines.Clear();
            AsrTextScroll.Visibility = Visibility.Collapsed;
            SetAsrStatus("正在加载语音模型…（首次可能要几秒到几十秒）", false);

            try
            {
                await Task.Run(() => _asr.Load(_cfg.asr_model_dir, _cfg.asr_threads));
                if (!_liveLoading || _liveRunning) return;

                LoopbackCapture loop = new LoopbackCapture();
                loop.Data += OnLoopData;
                loop.Failed += OnLoopFailed;
                try
                {
                    loop.Start();
                }
                catch
                {
                    try { loop.Dispose(); } catch { }
                    throw;
                }
                _loop = loop;
                _liveRate = loop.SampleRate;

                _liveCts = new CancellationTokenSource();
                try { if (_liveTransCts != null) _liveTransCts.Cancel(); } catch { }
                _liveTransCts = new CancellationTokenSource();
                _liveRunning = true;
                UpdateAsrHint();
                SetAsrStatus("正在聆听电脑播放的声音…", false);
                PositionAsrCard();
                _ = Task.Run(() => LiveLoopAsync(_liveCts.Token));
            }
            catch (SEHException ex)
            {
                App.Log("实时字幕原生库异常：" + ex);
                StopListening();
                SetAsrStatus("模型加载失败：onnx 文件无效或已损坏，请重新下载解压后再试。", true);
                Toast("实时字幕启动失败：模型文件无效或已损坏", true);
            }
            catch (Exception ex)
            {
                App.Log("实时字幕启动失败：" + ex);
                StopListening();
                SetAsrStatus("启动聆听失败：" + ex.Message, true);
                Toast("无法聆听系统声音：" + ex.Message, true);
            }
            finally
            {
                _liveLoading = false;
                if (!_liveRunning)
                {
                    SetAsrButtonVisual(false);
                    AsrActionBtn.Content = "开始聆听";
                }
            }
        }

        private void StopListening()
        {
            _liveLoading = false;
            bool was = _liveRunning;
            _liveRunning = false;
            try { if (_liveCts != null) _liveCts.Cancel(); } catch { }
            try { if (_liveTransCts != null) _liveTransCts.Cancel(); } catch { }
            _liveTransCts = null;
            LoopbackCapture cap = _loop;
            _loop = null;
            if (cap != null)
            {
                try { cap.Stop(); } catch { }
                try { cap.Dispose(); } catch { }
            }
            _liveCts = null;
            _captionDirty = true;
            lock (_liveLock)
            {
                _liveBuf.Clear();
                _liveHasVoice = false;
                _livePartial = "";
            }
            SetAsrButtonVisual(false);
            AsrActionBtn.Content = "开始聆听";
            if (was) SetAsrStatus("已停止聆听 · 共 " + _liveFinalCount + " 句", false);
        }

        private void OnLoopData(float[] samples, int rate)
        {
            lock (_liveLock)
            {
                _liveRate = rate;
                int cap = rate * 20;
                int overflow = _liveBuf.Count + samples.Length - cap;
                if (overflow > 0)
                {
                    if (overflow >= _liveBuf.Count) _liveBuf.Clear();
                    else _liveBuf.RemoveRange(0, overflow);
                }
                _liveBuf.AddRange(samples);
            }
        }

        private void OnLoopFailed(string msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopListening();
                SetAsrStatus("系统声音捕获中断：" + msg, true);
                Toast("实时字幕已停止：音频设备不可用", true);
            }));
        }

        private async Task LiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);

                    int rate = _liveRate;
                    float[] chunk;
                    lock (_liveLock)
                    {
                        if (_liveBuf.Count < rate / 8) continue;
                        chunk = _liveBuf.ToArray();
                    }

                    int tail = Math.Min(chunk.Length, Math.Max(1, rate / 4));
                    double sum = 0;
                    for (int i = chunk.Length - tail; i < chunk.Length; i++) sum += chunk[i] * chunk[i];
                    double rms = Math.Sqrt(sum / tail);
                    DateTime now = DateTime.UtcNow;
                    if (rms > 0.0045)
                    {
                        _liveHasVoice = true;
                        _liveVoiceAt = now;
                    }

                    bool silence = _liveHasVoice && (now - _liveVoiceAt).TotalMilliseconds > 600;
                    bool tooLong = _liveHasVoice && chunk.Length > rate * 12;
                    if (silence || tooLong)
                    {
                        lock (_liveLock)
                        {
                            chunk = _liveBuf.ToArray();
                            _liveBuf.Clear();
                        }
                        _liveHasVoice = false;
                        _liveLastPartial = DateTime.MinValue;
                        string text = await DecodeAsync(chunk, rate, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) break;
                        _livePartial = "";
                        if (!string.IsNullOrWhiteSpace(text)) AddFinalLine(text);
                        UpdateCaptionText();
                        continue;
                    }

                    bool partialDue = _liveHasVoice && chunk.Length > rate * 1.2
                        && (now - _liveLastPartial).TotalMilliseconds > 700;
                    if (partialDue)
                    {
                        _liveLastPartial = now;
                        int max = rate * 8;
                        float[] part;
                        if (chunk.Length > max)
                        {
                            part = new float[max];
                            Array.Copy(chunk, chunk.Length - max, part, 0, max);
                        }
                        else part = chunk;

                        string text = await DecodeAsync(part, rate, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) break;
                        if (!string.IsNullOrWhiteSpace(text) && text != _livePartial)
                        {
                            _livePartial = text;
                            UpdateCaptionText();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                App.Log("实时字幕识别失败：" + ex);
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    StopListening();
                    SetAsrStatus("识别失败：" + ex.Message, true);
                    Toast("实时字幕已停止：识别失败", true);
                }));
            }
        }

        private Task<string> DecodeAsync(float[] samples, int rate, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return _asr.Recognize(samples, rate);
            }, ct);
        }

        private void AddFinalLine(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return;
            LiveItem item = null;
            List<string> ctx = new List<string>();
            lock (_liveLock)
            {
                if (_liveItems.Count > 0 && _liveItems[_liveItems.Count - 1].Jp == t) return;
                for (int i = Math.Max(0, _liveItems.Count - 3); i < _liveItems.Count; i++)
                    ctx.Add(_liveItems[i].Jp);
                item = new LiveItem { Id = ++_liveItemId, Jp = t, Translating = _cfg.asr_auto_translate };
                _liveItems.Add(item);
                while (_liveItems.Count > 200) _liveItems.RemoveAt(0);
                _liveLines.Add(t);
                while (_liveLines.Count > 200) _liveLines.RemoveAt(0);
                _liveFinalCount++;
                if (_liveTranscript.Length < 4000)
                {
                    if (_liveTranscript.Length > 0) _liveTranscript.AppendLine();
                    _liveTranscript.Append(t);
                }
            }
            _captionDirty = true;
            if (_cfg.asr_auto_translate) QueueTranslation(item, ctx);
        }

        // 逐句把识别结果翻译成中文；只翻 final，避免 partial 频繁调用接口。
        private void QueueTranslation(LiveItem item, List<string> context)
        {
            CancellationTokenSource cts = _liveTransCts;
            if (item == null || cts == null) return;
            CancellationToken ct = cts.Token;
            _ = Task.Run(async () =>
            {
                bool held = false;
                StringBuilder sb = new StringBuilder();
                try
                {
                    await _transGate.WaitAsync(ct).ConfigureAwait(false);
                    held = true;
                    TranslateClient client = _client;
                    if (client == null) return;
                    string prompt = BuildSubtitlePrompt(item.Jp, context);
                    string full = await client.TranslateAsync(prompt, d =>
                    {
                        if (string.IsNullOrEmpty(d)) return;
                        lock (_liveLock) { sb.Append(d); item.Zh = sb.ToString().Trim(); }
                        _captionDirty = true;
                    }, ct).ConfigureAwait(false);
                    // 有些接口不流式返回增量，只在最终返回值里给全文；这时用 full 兜底。
                    if (!string.IsNullOrWhiteSpace(full))
                    {
                        lock (_liveLock)
                        {
                            if (string.IsNullOrWhiteSpace(item.Zh))
                                item.Zh = full.Trim();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    item.Failed = true;
                    App.Log("字幕翻译失败：" + ex.Message);
                    NotifyTransError(ex.Message);
                }
                finally
                {
                    lock (_liveLock) { item.Translating = false; }
                    if (held) _transGate.Release();
                    _captionDirty = true;
                }
            });
        }

        private static string BuildSubtitlePrompt(string jp, List<string> context)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("你在为日语视频做实时字幕。把【本句】翻译成简体中文，只输出译文，不要引号、不要解释、不要加原文。");
            if (context != null && context.Count > 0)
            {
                sb.AppendLine("上文（仅供理解，不要翻译）：");
                foreach (string c in context) sb.AppendLine(c);
            }
            sb.AppendLine("【本句】");
            sb.Append(jp);
            return sb.ToString();
        }

        private void NotifyTransError(string msg)
        {
            if (_liveTransErrorNotified) return;
            _liveTransErrorNotified = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                Toast("字幕翻译不可用：" + msg, true);
            }));
        }

        private void UpdateCaptionText()
        {
            _captionDirty = true;
        }

        // 上日文、下中文两行一条；UI 线程按 90ms 节流重绘，避免流式回调解码时抖动。
        private void RenderCaptions()
        {
            if (AsrText == null) return;
            List<LiveItem> items = new List<LiveItem>();
            string partial;
            lock (_liveLock)
            {
                for (int i = Math.Max(0, _liveItems.Count - 60); i < _liveItems.Count; i++)
                {
                    LiveItem it = _liveItems[i];
                    items.Add(new LiveItem
                    {
                        Id = it.Id,
                        Jp = it.Jp,
                        Zh = it.Zh,
                        Translating = it.Translating,
                        Failed = it.Failed
                    });
                }
                partial = _livePartial;
            }

            AsrText.Inlines.Clear();
            int translated = 0;
            foreach (LiveItem it in items)
            {
                AsrText.Inlines.Add(new Run(it.Jp) { Foreground = Res("TxtBrush"), FontSize = 19 });
                string zh = it.Zh;
                if (zh.Length == 0)
                {
                    if (it.Translating) zh = "翻译中…";
                    else if (it.Failed) zh = "（翻译失败）";
                }
                if (zh.Length > 0)
                {
                    if (it.Zh.Length > 0) translated++;
                    AsrText.Inlines.Add(new LineBreak());
                    AsrText.Inlines.Add(new Run(zh)
                    {
                        Foreground = it.Zh.Length > 0 ? Res("MutedBrush") : Res("DimBrush"),
                        FontSize = 14
                    });
                }
                AsrText.Inlines.Add(new LineBreak());
            }
            if (!string.IsNullOrEmpty(partial))
                AsrText.Inlines.Add(new Run(partial) { Foreground = Res("AccentBrush"), FontSize = 19 });

            bool has = items.Count > 0 || !string.IsNullOrEmpty(partial);
            AsrTextScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has && _liveFollow) AsrTextScroll.ScrollToEnd();
            PositionAsrCard();
            if (_liveRunning)
            {
                SetAsrStatus("正在聆听电脑播放的声音 · 已 " + _liveFinalCount + " 句"
                    + (translated > 0 ? " · 译文 " + translated + " 条" : ""), false);
            }
        }

        // 字幕是 TextBlock：逐个 Run 用字符矩形做命中测试（TextBlock 没有 GetCharacterIndexFromPoint）。
        private async void Caption_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_cfg.dict_on) return;
            string text;
            int idx;
            if (!HitCaption(e.GetPosition(AsrText), out text, out idx)) return;
            e.Handled = true;
            _dictAnchor = e.GetPosition(Root);
            await LookupAtAsync(text, idx);
        }

        /// <summary>把字幕里的鼠标位置换算成「哪个 Run 的第几个字」。</summary>
        private bool HitCaption(Point p, out string text, out int idx)
        {
            text = "";
            idx = -1;
            try
            {
                foreach (Inline inl in AsrText.Inlines)
                {
                    Run run = inl as Run;
                    if (run == null) continue;
                    string s = run.Text ?? "";
                    if (s.Length == 0) continue;
                    TextPointer pos = run.ContentStart;
                    for (int i = 0; i < s.Length; i++)
                    {
                        Rect r = pos.GetCharacterRect(LogicalDirection.Forward);
                        if (!r.IsEmpty && r.Contains(p)) { text = s; idx = i; return true; }
                        TextPointer next = pos.GetPositionAtOffset(1, LogicalDirection.Forward);
                        if (next == null) break;
                        pos = next;
                    }
                }
            }
            catch (Exception ex) { App.Log("字幕点词失败：" + ex.Message); }
            return false;
        }

        /// <summary>点词后的统一入口：词典没加载就按需加载，再查；查不到要说话，不能静默。</summary>
        private async Task LookupAtAsync(string text, int idx)
        {
            if (!_cfg.dict_on)
            {
                Toast("词典已关闭：设置 → 词典（点词查词）里勾上开关", true);
                return;
            }
            if ((_dict == null || !_dict.Ready) && !await EnsureDictionaryAsync()) return;
            if (!LookupAndShow(text, idx))
            {
                string ch = (idx >= 0 && idx < text.Length) ? text.Substring(idx, 1) : "";
                Toast(ch.Length > 0 ? "词典里没找到「" + ch + "」或它所在的词" : "词典里没找到这个词");
            }
        }

        /// <summary>确保词典已经在内存里：没加载就按需加载一次。</summary>
        private async Task<bool> EnsureDictionaryAsync()
        {
            if (_dict == null) return false;
            if (_dict.Ready) return true;
            string dir = (_cfg.dict_dir ?? "").Trim();
            if (dir.Length == 0)
            {
                Toast("还没选词典：设置最底部「词典（点词查词）」→ 浏览选 Jitendex 文件夹", true);
                return false;
            }
            if (_dictLoading)
            {
                Toast("词典正在加载，稍等几秒再点");
                return false;
            }
            Toast("正在建词典索引，首次约 3～5 秒…");
            await LoadDictionaryAsync(dir, false);
            return _dict.Ready;
        }

        private bool LookupAndShow(string text, int idx)
        {
            foreach (int anchor in AnchorIndexes(idx))
            {
                if (anchor < 0 || anchor >= text.Length) continue;
                foreach (string cand in WordCandidates(text, anchor))
                {
                    DictResult r = null;
                    try { r = _dict.Lookup(cand); }
                    catch (Exception ex) { App.Log("词典查询失败：" + ex.Message); }
                    if (r != null && r.Entries.Count > 0)
                    {
                        ShowDict(r, cand, SentenceAround(text, anchor, cand.Length));
                        return true;
                    }
                }
            }
            return false;
        }

        private async Task TranslateCaptionsAsync()
        {
            string text;
            lock (_liveLock)
            {
                text = _liveTranscript.ToString().Trim();
                if (text.Length == 0) text = _livePartial.Trim();
            }
            if (text.Length == 0)
            {
                Toast("还没有识别到字幕");
                return;
            }
            if (!_inputOpen) OpenInput();
            InputBox.Text = text;
            await TranslateAsync();
        }

        private void SetAsrButtonVisual(bool active)
        {
            try
            {
                AsrBtnBg.Fill = active
                    ? new SolidColorBrush(Color.FromArgb(0x55, 0xF8, 0x71, 0x71))
                    : _asrIdleFill;
                AsrBtnBg.Stroke = active ? Res("ErrBrush") : _asrIdleStroke;
                AsrBtnIcon.Stroke = active ? Res("ErrBrush") : Res("MutedBrush");
            }
            catch { }
        }

        private void SetAsrStatus(string text, bool error)
        {
            AsrStatus.Text = text;
            AsrStatus.Foreground = error ? Res("ErrBrush") : Res("MutedBrush");
        }

        // ============================ 位置 ============================
        private void PositionAsrCard()
        {
            if (AsrCard.Visibility != Visibility.Visible) return;
            double w = Width, h = Height;
            double cw = Math.Min(AsrCard.Width, Math.Max(200, w - MARGIN * 2));
            double ch = CardHeight(AsrCard);
            double x = (w - cw) / 2.0;
            double y = h - MARGIN * 2 - ch;   // 字幕固定在屏幕底部居中
            if (x < MARGIN) x = MARGIN;
            if (y < MARGIN) y = MARGIN;
            Canvas.SetLeft(AsrCard, x);
            Canvas.SetTop(AsrCard, y);
        }

        private void PositionDictPanel()
        {
            if (DictPanel.Visibility != Visibility.Visible) return;
            DictPanel.UpdateLayout();
            double w = Width, h = Height;
            double cw = DictPanel.Width, ch = CardHeight(DictPanel);

            // 优先贴着点词的位置弹：实时字幕里结果卡可能是收起的，
            // 以前只按 ResultCard 定位（Canvas.Left 还是 NaN）会直接不定位，浮层跑到屏幕左上角。
            Point a = _dictAnchor;
            double x, y;
            if (a.X > 0 || a.Y > 0)
            {
                if (IsInside(AsrCard, a))
                {
                    // 点在字幕卡里：浮层贴到字幕卡上方，别糊住正在看的字幕；上面放不下就放下面
                    double ay = Canvas.GetTop(AsrCard);
                    if (double.IsNaN(ay)) ay = a.Y - 120;
                    y = ay - ch - 10;
                    if (y < MARGIN) y = ay + CardHeight(AsrCard) + 10;
                    x = a.X - cw / 2;
                }
                else
                {
                    x = a.X + 14;
                    if (x + cw > w - MARGIN) x = a.X - 14 - cw;
                    y = a.Y - 30;
                }
            }
            else
            {
                double rx = Canvas.GetLeft(ResultCard);
                double ry = Canvas.GetTop(ResultCard);
                if (!double.IsNaN(rx) && !double.IsNaN(ry))
                {
                    x = rx + ResultCard.Width + GAP;
                    if (x + cw > w - MARGIN) x = rx - GAP - cw;
                    y = ry;
                }
                else
                {
                    x = (w - cw) / 2.0;
                    y = (h - ch) / 2.0;
                }
            }

            if (x < MARGIN) x = MARGIN;
            if (x + cw > w - MARGIN) x = w - MARGIN - cw;
            if (y + ch > h - MARGIN) y = h - MARGIN - ch;
            if (y < MARGIN) y = MARGIN;
            Canvas.SetLeft(DictPanel, x);
            Canvas.SetTop(DictPanel, y);
        }

        // ============================ 模型提示 ============================
        private bool TryReadModel(out string modelPath, out string tokensPath, out string error)
        {
            modelPath = "";
            tokensPath = "";
            error = "";
            if (string.IsNullOrWhiteSpace(_cfg.asr_model_dir))
            {
                error = "还没设置语音模型：下载 Parakeet 日语 INT8，解压后在上面选含 model.int8.onnx 的文件夹。";
                return false;
            }
            if (!SherpaAsr.TryResolve(_cfg.asr_model_dir, out modelPath, out tokensPath, out error))
            {
                error = "模型无效：" + error;
                return false;
            }
            return true;
        }

        private void UpdateAsrHint()
        {
            if (_asrHint == null) return;
            string mp, tp, err;
            if (TryReadModel(out mp, out tp, out err))
            {
                string dirName = "";
                try { dirName = Path.GetFileName(Path.GetDirectoryName(mp)); } catch { }
                _asrHint.Text = "已找到模型：" + dirName + "（CPU INT8，sherpa-onnx 1.13.8）";
                _asrHint.Foreground = Res("AccentBrush");
            }
            else
            {
                _asrHint.Text = err;
                _asrHint.Foreground = Res("DimBrush");
            }
        }

        // ============================ 设置面板：语音识别与词典 ============================
        private void BuildAsrSettings()
        {
            var asrTitle = SectionTitle("实时字幕（日语 · 本地 CPU）");
            _asrSectionAnchor = asrTitle;
            SettingsStack.Children.Add(asrTitle);

            var liveHint = new TextBlock
            {
                Text = "点麦克风按钮听电脑正在播放的声音，字幕浮在屏幕底部；再点一次停止。",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = Res("DimBrush"),
                ToolTip = "走系统回环录音，不需要麦克风权限。字幕里的日语单词可以直接点，弹出本地词典释义。",
            };
            SettingsStack.Children.Add(liveHint);

            var fModel = Field(_cfg.asr_model_dir);
            fModel.TextChanged += (s, e) => UpdateAsrHint();
            var browse = new Button { Content = "浏览", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += (s, e) => PickAsrModel(fModel);
            var modelRow = new Grid();
            modelRow.ColumnDefinitions.Add(new ColumnDefinition());
            modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(fModel, 0);
            Grid.SetColumn(browse, 1);
            modelRow.Children.Add(fModel);
            modelRow.Children.Add(browse);
            SettingsStack.Children.Add(RowFull("模型文件夹（model.int8.onnx + tokens.txt）", modelRow));

            _asrHint = new TextBlock
            {
                Text = "",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
                Foreground = Res("DimBrush"),
            };
            SettingsStack.Children.Add(_asrHint);

            var sThreads = Slider(0, 12, Math.Max(0, Math.Min(12, _cfg.asr_threads)), 1);
            SettingsStack.Children.Add(RowSide("线程（0=自动）", sThreads));

            var cAuto = Check("自动翻译字幕（日 → 中）", _cfg.asr_auto_translate);
            cAuto.ToolTip = "需要翻译接口可用；字幕出完一条就译一条。";
            SettingsStack.Children.Add(cAuto);

            var dl = new Button { Content = "下载模型", Style = (Style)FindResource("TextBtn") };
            dl.Click += (s, e) => OpenUrl(AsrModelUrl);
            var hint = new TextBlock
            {
                Text = "约 625 MB，解压后选文件夹",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = Res("DimBrush"),
                ToolTip = "下载的是 .tar.bz2，用 7-Zip 解压（或 tar -xjf），选解压出的文件夹即可。",
                VerticalAlignment = VerticalAlignment.Center,
            };
            var dlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            dlRow.Children.Add(dl);
            dlRow.Children.Add(hint);
            SettingsStack.Children.Add(dlRow);

            SettingsStack.Children.Add(SectionTitle("词典（点词查词）"));

            var fDict = Field(_cfg.dict_dir);
            var browseDict = new Button { Content = "浏览", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 0, 0) };
            browseDict.Click += (s, e) => PickDictFolder(fDict);
            var dictRow = new Grid();
            dictRow.ColumnDefinitions.Add(new ColumnDefinition());
            dictRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(fDict, 0);
            Grid.SetColumn(browseDict, 1);
            dictRow.Children.Add(fDict);
            dictRow.Children.Add(browseDict);
            SettingsStack.Children.Add(RowFull("Yomitan 文件夹（term_bank_*.json）", dictRow));

            var cDict = Check("在翻译结果里点词查词典", _cfg.dict_on);
            SettingsStack.Children.Add(cDict);
            var cDictSpeak = Check("查到词后念一遍", _cfg.dict_speak);
            cDictSpeak.ToolTip = "用日语发音念辞书形（和上面「朗读通道」同一套语音）；连续点不同的词会打断上一个。";
            SettingsStack.Children.Add(cDictSpeak);

            // 索引常驻（Jitendex 44 万条约 70 MB，释义按需读），默认不预建，第一次点词时再读
            var cPreload = Check("启动时预建词典索引（多占 70 MB）", _cfg.dict_preload);
            cPreload.ToolTip = "不勾：第一次点词时才建索引，等约 3 秒；勾上：程序启动就建好，点词零等待。索引约 70 MB，释义不进内存。";
            SettingsStack.Children.Add(cPreload);

            _dictHint = new TextBlock
            {
                Text = "未加载词典",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
                Foreground = Res("DimBrush"),
            };
            SettingsStack.Children.Add(_dictHint);

            var reload = new Button { Content = "重新加载词典", Style = (Style)FindResource("TextBtn") };
            reload.Click += (s, e) => ReloadDictionary(fDict.Text, true);
            var jit = new Button { Content = "下载 Jitendex", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(8, 0, 0, 0) };
            jit.Click += (s, e) => OpenUrl(JitendexUrl);
            var wordBtn = new Button { Content = "生词本", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(8, 0, 0, 0) };
            wordBtn.ToolTip = "查词浮层上点 ☆ 收藏的词都在这里；可搜索、朗读、导出 CSV。";
            wordBtn.Click += (s, e) => OpenWordBook();
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            btns.Children.Add(reload);
            btns.Children.Add(jit);
            btns.Children.Add(wordBtn);
            SettingsStack.Children.Add(btns);

            _savers.Add(() =>
            {
                _cfg.asr_model_dir = (fModel.Text ?? "").Trim();
                _cfg.asr_threads = (int)Math.Round(sThreads.Value);
                _cfg.asr_auto_translate = cAuto.IsChecked == true;
                _cfg.dict_dir = (fDict.Text ?? "").Trim();
                _cfg.dict_on = cDict.IsChecked == true;
                _cfg.dict_preload = cPreload.IsChecked == true;
                _cfg.dict_speak = cDictSpeak.IsChecked == true;
            });

            // 面板可能被重建（RebuildSettings）：已有运行时状态就直接刷新提示
            if (_asr != null)
            {
                UpdateAsrHint();
                if (_dict != null && _dict.Ready)
                    SetDictHint("已加载 " + _dict.EntryCount.ToString("n0") + " 条 · "
                                + (_dict.MemoryBytes / 1048576) + " MB 内存", false);
                else if (!string.IsNullOrWhiteSpace(_cfg.dict_dir))
                    SetDictHint("未加载：点词时自动建索引（约 3 秒 / 70 MB）", false);
                else
                    SetDictHint("未加载词典：选择 Jitendex 解压后的文件夹。", false);
            }
        }

        private void PickAsrModel(TextBox box)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择语音模型文件夹（含 model.int8.onnx 与 tokens.txt）",
            };
            if (dlg.ShowDialog() != true) return;
            box.Text = dlg.FolderName;
            UpdateAsrHint();
        }

        private void PickDictFolder(TextBox box)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择词典文件夹（含 term_bank_*.json，可在子目录里）",
            };
            if (dlg.ShowDialog() != true) return;
            box.Text = dlg.FolderName;
            ReloadDictionary(dlg.FolderName, true);
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log("打开链接失败：" + ex.Message); }
        }

        // ============================ 词典加载 ============================
        private async void ReloadDictionary(string dir, bool notify)
        {
            await LoadDictionaryAsync(dir, notify);
        }

        /// <summary>读词典（后台线程）。返回是否可用，供「点词时按需加载」等待。</summary>
        private async Task<bool> LoadDictionaryAsync(string dir, bool notify)
        {
            if (_dict == null) return false;
            dir = (dir ?? "").Trim();
            if (dir.Length == 0)
            {
                SetDictHint("未选择词典文件夹", true);
                if (notify) Toast("先选择词典文件夹");
                return false;
            }
            if (_dictLoading)
            {
                if (notify) Toast("词典正在加载，请稍等");
                return false;
            }

            _dictLoading = true;
            try { _dictLoadCts?.Cancel(); } catch { }
            _dictLoadCts = new CancellationTokenSource();
            CancellationToken ct = _dictLoadCts.Token;
            SetDictHint("正在建词典索引…（Jitendex 约 44 万词条，需要 3～5 秒）", false);
            try
            {
                await _dict.LoadAsync(dir, ct);
                if (_dict.Ready)
                {
                    string info = "已索引 " + _dict.EntryCount.ToString("n0") + " 条 · "
                                  + (_dict.MemoryBytes / 1048576) + " MB 内存 · "
                                  + (_dict.LoadMs / 1000.0).ToString("0.0") + " 秒（释义按需读）";
                    SetDictHint(info, false);
                    if (notify) Toast("词典已就绪：" + _dict.EntryCount.ToString("n0") + " 条，索引 " + (_dict.MemoryBytes / 1048576) + " MB");
                    return true;
                }
                string err = string.IsNullOrWhiteSpace(_dict.LastError) ? "没有找到 term_bank_*.json" : _dict.LastError;
                SetDictHint("加载失败：" + err, true);
                if (notify) Toast("词典加载失败：" + err, true);
                return false;
            }
            catch (OperationCanceledException) { SetDictHint("词典加载已取消", true); return false; }
            catch (Exception ex)
            {
                App.Log("词典加载失败：" + ex);
                SetDictHint("加载失败：" + ex.Message, true);
                if (notify) Toast("词典加载失败：" + ex.Message, true);
                return false;
            }
            finally
            {
                _dictLoading = false;
            }
        }

        private void SetDictHint(string text, bool error)
        {
            if (_dictHint == null) return;
            _dictHint.Text = text;
            _dictHint.Foreground = error ? Res("ErrBrush") : Res("DimBrush");
        }

        private void HideDictPanel()
        {
            if (DictPanel.Visibility == Visibility.Visible) HideEl(DictPanel, 140);
        }

        // ============================ 点词查词 ============================
        private async void ResultText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_cfg.dict_on) return;
            string text = ResultText.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            Point p = e.GetPosition(ResultText);
            int raw = ResultText.GetCharacterIndexFromPoint(p, false);
            int idx = HitTestIndex(text, raw, p.X);
            if (idx < 0 || idx >= text.Length) return;

            e.Handled = true;
            _dictAnchor = e.GetPosition(Root);
            await LookupAtAsync(text, idx);
        }

        // TextBox 的 GetCharacterIndexFromPoint 返回的是插入位置：点在字符右半边会指到下一个字符。
        // 用字符矩形把插入位置换算成真正被点到的字符。
        private int HitTestIndex(string text, int raw, double x)
        {
            if (raw < 0) raw = text.Length - 1;
            if (raw >= text.Length) raw = text.Length - 1;
            if (raw < 0) return -1;
            try
            {
                Rect at = ResultText.GetRectFromCharacterIndex(raw, false);
                if (at.Width > 0 && x >= at.Left && x <= at.Right) return raw;
                if (raw > 0)
                {
                    Rect prev = ResultText.GetRectFromCharacterIndex(raw - 1, false);
                    if (prev.Width > 0 && x >= prev.Left && x <= prev.Right) return raw - 1;
                }
                if (at.Width > 0)
                    return x < at.Left + at.Width / 2 ? raw : Math.Min(raw + 1, text.Length - 1);
            }
            catch { }
            return raw;
        }

        private static List<int> AnchorIndexes(int idx)
        {
            var list = new List<int>();
            foreach (int i in new[] { idx, idx - 1, idx + 1 })
            {
                if (i >= 0 && !list.Contains(i)) list.Add(i);
            }
            return list;
        }

        private static List<string> WordCandidates(string text, int index)
        {
            var list = new List<string>();
            if (text == null || index < 0 || index >= text.Length) return list;

            int anchor = index;
            if (!IsJapaneseChar(text[anchor]))
            {
                if (index > 0 && IsJapaneseChar(text[index - 1])) anchor = index - 1;
                else if (index + 1 < text.Length && IsJapaneseChar(text[index + 1])) anchor = index + 1;
                else return list;
            }

            int runStart = anchor, runEnd = anchor;
            while (runStart > 0 && IsJapaneseChar(text[runStart - 1])) runStart--;
            while (runEnd < text.Length - 1 && IsJapaneseChar(text[runEnd + 1])) runEnd++;

            // 1) 多字词，长词优先：Jitendex 里「読んだ」「食べた」这类活用形由 Lookup 还原
            int maxLen = Math.Min(12, runEnd - runStart + 1);
            int from = Math.Max(runStart, anchor - 8);
            int to = Math.Min(anchor, runEnd);
            for (int len = maxLen; len >= 2; len--)
            {
                for (int start = from; start <= to; start++)
                {
                    if (start + len - 1 > runEnd) continue;
                    if (anchor < start || anchor > start + len - 1) continue;
                    string cand = text.Substring(start, len);
                    if (!list.Contains(cand)) list.Add(cand);
                    if (list.Count >= 30) return list;
                }
            }

            // 2) 单字兜底：无论上下文多长，点击的字本身一定能查
            foreach (int i in new[] { anchor, anchor - 1, anchor + 1 })
            {
                if (i < 0 || i >= text.Length) continue;
                if (!IsJapaneseChar(text[i])) continue;
                string one = text.Substring(i, 1);
                if (!list.Contains(one)) list.Add(one);
            }
            return list;
        }

        private static bool IsJapaneseChar(char c)
        {
            return (c >= '\u3040' && c <= '\u30FF')   // 平假名 + 片假名
                || (c >= '\u4E00' && c <= '\u9FFF')   // 汉字
                || (c >= '\u3400' && c <= '\u4DBF')   // 扩展 A
                || c == '\u30FC' || c == '\u3005';    // 长音、叠字
        }

        private void ShowDict(DictResult r, string used, string sentence = null)
        {
            _dictHit = r;
            _dictUsed = used ?? "";
            _dictSentence = sentence ?? "";

            DictWord.Text = string.IsNullOrEmpty(r.BaseForm) ? used : r.BaseForm;
            DictReading.Text = string.IsNullOrEmpty(r.Reading) ? "" : "［" + r.Reading + "］";
            DictBody.Children.Clear();

            for (int i = 0; i < r.Entries.Count && i < 6; i++)
            {
                DictEntry en = r.Entries[i];
                if (i > 0)
                {
                    DictBody.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Res("DividerBrush"),
                        Margin = new Thickness(0, 8, 0, 8),
                        Opacity = 0.7,
                    });
                }

                var head = new TextBlock
                {
                    FontFamily = JpFont,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("TxtBrush"),
                    TextWrapping = TextWrapping.Wrap,
                };
                head.Text = en.Term;
                DictBody.Children.Add(head);

                if (!string.IsNullOrEmpty(en.Reading) || !string.IsNullOrEmpty(en.Pos))
                {
                    var sub = new TextBlock
                    {
                        FontSize = 10.5,
                        Foreground = Res("AccentBrush"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 3),
                    };
                    string rd = string.IsNullOrEmpty(en.Reading) ? "" : "【" + en.Reading + "】";
                    sub.Text = rd + (string.IsNullOrEmpty(en.Pos) ? "" : " " + en.Pos);
                    DictBody.Children.Add(sub);
                }

                var def = new TextBlock
                {
                    Text = en.Definition,
                    FontSize = 12,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD5, 0xDD, 0xEE)),
                };
                DictBody.Children.Add(def);
            }

            var foot = new StringBuilder();
            if (r.Deinflected) foot.Append("「").Append(used).Append("」→ 原形「").Append(r.BaseForm).Append("」｜");
            foot.Append("本地 Yomitan 词典 · ").Append(_dict.EntryCount.ToString("n0")).Append(" 条");
            DictFoot.Text = foot.ToString();

            if (DictPanel.Visibility != Visibility.Visible)
            {
                DictPanel.Visibility = Visibility.Visible;
                ShowEl(DictPanel, 160);
                AnimScale(DictScale, 1, 160);
            }
            PositionDictPanel();
            UpdateStarState();

            // 点词即发音：念的是辞书形（活用已经还原），用日语发音
            if (_cfg.dict_speak)
            {
                string word = string.IsNullOrEmpty(r.BaseForm) ? _dictUsed : r.BaseForm;
                SpeakWord(word);
            }
        }

        /// <summary>取这个词所在的那一句（前后各留一点，最多 120 字），收藏时当作例句记下来。</summary>
        private static string SentenceAround(string text, int anchor, int wordLen)
        {
            if (string.IsNullOrEmpty(text) || anchor < 0 || anchor >= text.Length) return "";
            const string stops = "。！？!?";
            int start = anchor;
            while (start > 0 && stops.IndexOf(text[start - 1]) < 0 && anchor - start < 60) start--;
            int end = anchor + Math.Max(1, wordLen);
            while (end < text.Length && stops.IndexOf(text[end]) < 0 && end - anchor < 60) end++;
            string s = text.Substring(start, end - start).Replace("\r", "").Replace("\n", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        /// <summary>浮层标题上的词（优先辞书形）。</summary>
        private string CurrentWord()
        {
            if (_dictHit == null) return "";
            return string.IsNullOrEmpty(_dictHit.BaseForm) ? (_dictUsed ?? "") : _dictHit.BaseForm;
        }

        private void UpdateStarState()
        {
            string term = CurrentWord();
            bool on = term.Length > 0 && App.Words != null && App.Words.Contains(term);
            DictStarPath.Fill = on ? Res("AccentBrush") : Brushes.Transparent;
            DictStarPath.Stroke = on ? Res("AccentBrush") : Res("MutedBrush");
            DictStarBtn.ToolTip = on ? "已收藏（点一下取消）" : "收藏到生词本";
        }

        private void ToggleWordStar()
        {
            if (_dictHit == null || App.Words == null) return;
            string term = CurrentWord();
            if (term.Length == 0) return;

            if (App.Words.Contains(term))
            {
                App.Words.Remove(App.Words.Find(term));
                Toast("已从生词本移除「" + term + "」");
            }
            else
            {
                DictEntry e0 = _dictHit.Entries.Count > 0 ? _dictHit.Entries[0] : null;
                bool added = App.Words.Add(term, _dictHit.Reading, e0 != null ? e0.Pos : "",
                                           e0 != null ? e0.Definition : "", _dictSentence);
                Toast(added ? "已收藏「" + term + "」· 共 " + App.Words.Items.Count + " 个"
                            : "「" + term + "」已在生词本里，已更新");
            }
            UpdateStarState();
            // 生词本窗口正好开着的话，让它跟着刷新
            if (_wordWin != null && _wordWin.IsVisible) _wordWin.Refresh();
        }
    }
}
