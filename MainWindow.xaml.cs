using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FloatingTranslator
{
    public partial class MainWindow : Window
    {
        private const double MARGIN = 12;
        private const double GAP = 12;
        private const double ORB = 64;
        private const double GEAR = 34;

        // 圆钮缩放：鼠标离开且翻译器关闭时缩小，鼠标经过或翻译器打开时回到原大小
        private const double ORB_SCALE_IDLE = 0.82;
        private const double ORB_SCALE_ON = 1.0;

        private AppConfig _cfg;
        private TranslateClient _client;
        private Speaker _speaker;
        private EdgeTts _edge;              // Edge 在线语音（设置里把朗读通道切成 edge 时才用）
        private HotkeyManager _hotkeys;

        // ---- 第三方语音：本地 Irodori-TTS（只在目标语言为日语时出现的那颗「あ」按钮） ----
        private AudioPlayer _audio;
        private bool _irodoriBusy;
        private Stopwatch _irodoriSw;       // 本次朗读的计时器（日志里标记播放/合成的相对时刻）
        private CancellationTokenSource _irodoriCts;

        private DispatcherTimer _autoTimer;     // 停止输入后自动翻译
        private DispatcherTimer _hideTimer;     // 空输入自动隐藏
        private DispatcherTimer _toastTimer;

        private CancellationTokenSource _cts;
        private bool _busy;
        private readonly StringBuilder _stream = new StringBuilder();

        private double _orbX, _orbY;
        private bool _dragging, _dragMoved;
        private bool _orbHover;
        private Point _dragFrom;
        private double _dragOrbX, _dragOrbY;

        private bool _inputOpen;
        private bool _suppressAuto;
        private bool _demoMode;

        // 图片翻译：当前挂着的图片（null = 走文本翻译）
        private byte[] _imageBytes;
        private string _imageMime = "image/png";
        private string _imageName = "";
        public MainWindow()
        {
            InitializeComponent();
            _cfg = App.Config;
            _client = new TranslateClient(_cfg.api_base, _cfg.api_key, _cfg.timeout);
            // 模型降级时给个提示（否则用户只看到「变慢了」却不知道换了档）
            TranslateClient.Fallback += (from, to, reason) => Dispatcher.InvokeAsync(() =>
            {
                if (ResultCard.Visibility == Visibility.Visible)
                    ResultMeta.Text = "「" + AppConfig.ModelShort(from) + "」无响应，改用 "
                                      + AppConfig.ModelShort(to) + " 重试…";
                Toast("「" + AppConfig.ModelShort(from) + "」无响应，已改用 "
                      + AppConfig.ModelShort(to) + " 重试");
            });
            _speaker = new Speaker();
            _edge = new EdgeTts();
            _audio = new AudioPlayer();
            _audio.Ended += AudioEnded;
            _audio.Opened += d =>
            {
                _lastAudioLen = d;
                App.Log("Irodori 开始播放，时长 " + d.TotalSeconds.ToString("0.0") + "s");
            };
            _audio.Failed += m =>
            {
                App.Log("Irodori 播放失败: " + m);
                Toast("Irodori 播放失败：" + m, true);
            };

            var wa = SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;

            _autoTimer = new DispatcherTimer();
            _autoTimer.Tick += (s, e) => { _autoTimer.Stop(); if (!_busy) _ = TranslateAsync(); };
            _hideTimer = new DispatcherTimer();
            _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); if (!_busy && InputBox.Text.Length == 0) CloseInput(); };
            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); HideEl(ToastBox, 200, 180); };

            OrbIcon.RenderTransformOrigin = new Point(0.5, 0.5);
            OrbIcon.RenderTransform = new RotateTransform(0);
            SettingsBtnIcon.RenderTransformOrigin = new Point(0.5, 0.5);
            SettingsBtnIcon.RenderTransform = new RotateTransform(0);

            Loaded += OnLoaded;
            Deactivated += (s, e) => { if (!_demoMode) { CloseDropdown(); CloseSettingsPanel(); } };
            PreviewKeyDown += OnWindowKeyDown;
            WireEvents();
        }

        /// <summary>把所有交互事件挂上（XAML 里没有写事件属性）</summary>
        private void WireEvents()
        {
            OrbHost.MouseLeftButtonDown += OrbHost_MouseLeftButtonDown;
            OrbHost.MouseMove += OrbHost_MouseMove;
            OrbHost.MouseLeftButtonUp += OrbHost_MouseLeftButtonUp;
            OrbHost.MouseRightButtonUp += (s, e) => { if (SettingsPanel.Visibility == Visibility.Visible) CloseSettingsPanel(); else OpenSettingsPanel(); e.Handled = true; };
            OrbHost.MouseEnter += OrbHost_MouseEnter;
            OrbHost.MouseLeave += OrbHost_MouseLeave;

            SettingsBtnHost.PreviewMouseLeftButtonDown += SettingsBtn_Click;

            // 退出按钮：只有图标，悬停变红提示这是关闭动作
            ExitBtnHost.PreviewMouseLeftButtonDown += (s, e) => { e.Handled = true; QuitApp(); };
            ExitBtnHost.MouseEnter += (s, e) => ExitBtnIcon.Stroke = (Brush)FindResource("ErrBrush");
            ExitBtnHost.MouseLeave += (s, e) => ExitBtnIcon.Stroke = (Brush)FindResource("MutedBrush");

            LangBtn.PreviewMouseLeftButtonDown += LangBtn_Click;

            InputBox.TextChanged += InputBox_TextChanged;
            InputBox.SizeChanged += InputBox_SizeChanged;
            InputBox.PreviewKeyDown += InputBox_PreviewKeyDown;
            InputBox.PreviewMouseWheel += InputBox_PreviewMouseWheel;
            DataObject.AddPastingHandler(InputBox, InputBox_Pasting);
            InputCard.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!InputBox.IsKeyboardFocusWithin) { InputBox.Focus(); Keyboard.Focus(InputBox); }
            };

            PasteBtn.Click += PasteBtn_Click;
            TranslateBtn.Click += TranslateBtn_Click;
            ClearBtn.Click += ClearBtn_Click;
            CopyBtn.Click += CopyBtn_Click;
            SpeakBtn.Click += SpeakBtn_Click;
            IrodoriBtn.Click += IrodoriBtn_Click;

            // 图片翻译：按钮选文件 / 拖进来 / Ctrl+V 粘贴截图
            ImageBtn.Click += ImageBtn_Click;
            ImageClearBtn.Click += (s, e) => { ClearImage(); Toast("已移除图片"); };
            Drop += Window_Drop;
            DragOver += Window_DragOver;

            // 点空白/卡片其它地方时收起浮层
            Root.MouseLeftButtonDown += (s, e) =>
            {
                if (LangDropdown.Visibility == Visibility.Visible && !LangDropdown.IsMouseOver && !LangBtn.IsMouseOver)
                    CloseDropdown();
                if (SettingsPanel.Visibility == Visibility.Visible && !SettingsPanel.IsMouseOver && !SettingsBtnHost.IsMouseOver)
                    CloseSettingsPanel();
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _orbX = _cfg.orb_x >= 0 ? _cfg.orb_x : Width - MARGIN - 24 - ORB;
            _orbY = _cfg.orb_y >= 0 ? _cfg.orb_y : Height - MARGIN - 24 - ORB;
            ClampOrb();
            PositionAll();
            BuildSettings();
            InitAsr();
            SetupHotkeys();
            if (_cfg.panel_open) OpenInput();
            App.Log("窗口就绪 屏幕=" + (int)Width + "x" + (int)Height + " 圆钮=(" + (int)_orbX + "," + (int)_orbY + ")");
        }

        // ============================ 布局 ============================
        /// <summary>退出：先存设置再关（设置面板里的「退出程序」已改为圆钮旁的图标按钮）</summary>
        private void QuitApp()
        {
            App.Log("退出：点击了退出图标");
            try { SaveSettings(); } catch { }
            Application.Current.Shutdown();
        }

        private void ClampOrb()
        {
            double maxX = Width - MARGIN - ORB;
            double maxY = Height - MARGIN - ORB;
            if (_orbX < MARGIN) _orbX = MARGIN;
            if (_orbY < MARGIN) _orbY = MARGIN;
            if (_orbX > maxX) _orbX = maxX;
            if (_orbY > maxY) _orbY = maxY;
        }

        private double CardHeight(FrameworkElement el)
        {
            // Collapsed 的元素量不到高度，临时点亮再量（同一帧内不会闪烁）
            // 注意：Measure() 对「测量仍然有效」的元素是空操作，会返回上一次的旧高度，
            // 所以必须先 InvalidateMeasure() 再量，否则改完内容卡片会被摆错位置。
            Visibility old = el.Visibility;
            if (old != Visibility.Visible) el.Visibility = Visibility.Visible;
            el.InvalidateMeasure();
            el.UpdateLayout();                       // 同步跑完布局，拿到真实渲染高度
            double h = el.ActualHeight;
            if (h <= 0)
            {
                el.Measure(new Size(el.Width, double.PositiveInfinity));
                h = el.DesiredSize.Height;
            }
            if (old != Visibility.Visible) el.Visibility = old;
            return h;
        }

        // 输入框单行时卡片 400 宽、右侧四颗按钮排成一行；
        // 文本一行放不下时把卡片加宽、按钮排成 2×2 格子，免得按钮把文本框挤成两三个字宽。
        private const double CARD_W_NARROW = 400;
        private const double CARD_W_WIDE = 468;
        private bool _wideInput;

        // 加宽后文本框可用宽度会从 ~164px 跳到 ~312px，如果还按「现在换没换行」来判断，
        // 同一段字就会在「变宽→能放下了→变窄→又放不下→变宽」之间死循环，看到的就是来回闪。
        // 所以改成量「这段字不换行时有多宽」，而且两个方向的阈值不对称：
        //   窄 → 宽：自然宽 >  窄模式可用宽
        //   宽 → 窄：自然宽 <  窄模式可用宽 - WIDE_BACK_HYST
        // 自然宽度跟卡片当前宽窄无关，不会自激；中间那条带子就是防抖。
        private const double WIDE_BACK_HYST = 28;
        private const double NARROW_TEXT_W_FALLBACK = 164;    // 400 卡片去掉左右按钮和内边距后的估宽
        private double _narrowTextW;                          // 窄模式下文本框真实可用宽（布局跑完后量到）

        private void SetWideInput(bool wide)
        {
            if (_wideInput == wide) return;
            _wideInput = wide;
            InputCard.Width = wide ? CARD_W_WIDE : CARD_W_NARROW;
            ResultCard.Width = InputCard.Width;
            BtnBlock.Rows = wide ? 2 : 1;
            BtnBlock.Columns = wide ? 2 : 4;
        }

        /// <summary>窄模式下文本框的可用宽度；还没量到就用估宽。</summary>
        private double NarrowTextWidth
        {
            get { return _narrowTextW > 1 ? _narrowTextW : NARROW_TEXT_W_FALLBACK; }
        }

        /// <summary>这段文字「不换行」时有多宽（有显式换行就取最长的一行）。</summary>
        private double NaturalTextWidth(string text)
        {
            var tf = new Typeface(InputBox.FontFamily, InputBox.FontStyle, InputBox.FontWeight, InputBox.FontStretch);
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double max = 0;
            int start = 0;
            while (start <= text.Length)
            {
                int nl = text.IndexOf('\n', start);
                string line = nl < 0 ? text.Substring(start) : text.Substring(start, nl - start);
                if (line.EndsWith("\r", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);
                if (line.Length > 200) return double.MaxValue;   // 一行都两百字了，不用细量，肯定是宽模式
                if (line.Length > 0)
                {
                    var ft = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        tf, InputBox.FontSize, Brushes.Black, ppd);
                    if (ft.WidthIncludingTrailingWhitespace > max) max = ft.WidthIncludingTrailingWhitespace;
                }
                if (nl < 0) break;
                start = nl + 1;
            }
            return max;
        }

        /// <summary>要不要切成加宽模式（带防抖带，见上面注释）。整段判断只看文本和字体，跟卡片当前宽窄无关。</summary>
        private bool ShouldWidenInput()
        {
            string text = InputBox.Text;
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > 240) return true;
            // 行数只看「用户自己敲的换行」，也跟卡片宽窄无关（换成按实际高度就会自激）。
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            if (lines >= 3) return true;
            double need = NaturalTextWidth(text);
            return _wideInput ? need > NarrowTextWidth - WIDE_BACK_HYST : need > NarrowTextWidth;
        }

        private void PositionAll()
        {
            Canvas.SetLeft(OrbHost, _orbX);
            Canvas.SetTop(OrbHost, _orbY);

            double w = Width, h = Height;
            double cardW = InputCard.Width;
            double inH = CardHeight(InputCard);
            double outH = CardHeight(ResultCard);

            // 卡片优先弹在圆钮的哪一侧（设置里可选，默认左侧）
            bool leftFirst = !string.Equals(_cfg.panel_side, "right", StringComparison.OrdinalIgnoreCase);
            double xLeft = _orbX - GAP - cardW;
            double xRight = _orbX + ORB + GAP;
            double x = leftFirst ? xLeft : xRight;
            double other = leftFirst ? xRight : xLeft;
            if (x < MARGIN || x + cardW > w - MARGIN) x = other;   // 首选那侧放不下就换另一侧
            if (x < MARGIN) x = MARGIN;
            if (x + cardW > w - MARGIN) x = w - MARGIN - cardW;

            double y = _orbY + ORB / 2 - 28;
            if (y < MARGIN) y = MARGIN;
            if (y + inH > h - MARGIN) y = h - MARGIN - inH;
            if (y < MARGIN) y = MARGIN;

            Canvas.SetLeft(InputCard, x);
            Canvas.SetTop(InputCard, y);

            // 三个小圆钮：麦克风固定在设置按钮正上方，退出在设置下方；
            // 圆钮下方放不下整列时整体挪到圆钮上方（顺序仍是 退出 → 麦克风 → 设置）。
            double gx = _orbX + ORB / 2 - GEAR / 2;
            double below = _orbY + ORB + GAP;
            double gy, ay, ey;
            bool fitBelow = below + GEAR * 3 + 16 <= h - MARGIN;
            if (fitBelow)
            {
                ay = below;
                gy = ay + GEAR + 8;
                ey = gy + GEAR + 8;
            }
            else
            {
                gy = _orbY - GAP - GEAR;
                ay = gy - 8 - GEAR;
                ey = ay - 8 - GEAR;
            }
            if (gx < MARGIN) gx = MARGIN;
            if (gx + GEAR > w - MARGIN) gx = w - MARGIN - GEAR;
            if (ay < MARGIN) ay = MARGIN;
            if (gy < MARGIN) gy = MARGIN;
            if (ey < MARGIN) ey = MARGIN;
            if (ey + GEAR > h - MARGIN) ey = h - MARGIN - GEAR;
            Canvas.SetLeft(AsrBtnHost, gx);
            Canvas.SetTop(AsrBtnHost, ay);
            Canvas.SetLeft(SettingsBtnHost, gx);
            Canvas.SetTop(SettingsBtnHost, gy);
            Canvas.SetLeft(ExitBtnHost, gx);
            Canvas.SetTop(ExitBtnHost, ey);

            // 结果窗：优先放在输入窗下方
            double ry = y + inH + GAP;
            if (ry + outH > h - MARGIN) ry = y - GAP - outH;
            if (ry < MARGIN) ry = Math.Min(h - MARGIN - outH, y + inH + GAP);
            if (ry < MARGIN) ry = MARGIN;
            Canvas.SetLeft(ResultCard, x);
            Canvas.SetTop(ResultCard, ry);

            PositionDropdown();
            PositionSettingsPanel();
            PositionAsrCard();
            PositionDictPanel();
            if (ToastBox.Visibility == Visibility.Visible) PositionToast();
        }

        private void PositionSettingsPanel()
        {
            if (SettingsPanel.Visibility != Visibility.Visible) return;
            double pw = SettingsPanel.Width;
            double ph = CardHeight(SettingsPanel);
            double gx = Canvas.GetLeft(SettingsBtnHost);
            double gy = Canvas.GetTop(SettingsBtnHost);

            double px = gx - pw - GAP;
            if (px < MARGIN) px = gx + GEAR + GAP;
            if (px + pw > Width - MARGIN) px = Width - MARGIN - pw;
            if (px < MARGIN) px = MARGIN;

            double py = gy + GEAR / 2 - ph / 2;
            if (py < MARGIN) py = MARGIN;
            if (py + ph > Height - MARGIN) py = Height - MARGIN - ph;
            if (py < MARGIN) py = MARGIN;

            Canvas.SetLeft(SettingsPanel, px);
            Canvas.SetTop(SettingsPanel, py);
        }

        private void PositionToast()
        {
            double tw = ToastBox.ActualWidth > 0 ? ToastBox.ActualWidth : 180;
            double th = ToastBox.ActualHeight > 0 ? ToastBox.ActualHeight : 32;
            double cx = InputCard.Visibility == Visibility.Visible
                ? Canvas.GetLeft(InputCard) + InputCard.Width / 2
                : _orbX + ORB / 2;
            double x = cx - tw / 2;
            if (x < MARGIN) x = MARGIN;
            if (x + tw > Width - MARGIN) x = Width - MARGIN - tw;
            // 提示条放在最上面那张卡片的**上方**：以前放在卡片下方，会正好压住结果卡右下角的
            // 「あ / 🔊 / ⧉」按钮，看起来就像播放结束后界面卡住、点不动。
            double y = _orbY - GAP - th;
            if (ResultCard.Visibility == Visibility.Visible)
                y = Canvas.GetTop(ResultCard) - GAP - th;
            else if (InputCard.Visibility == Visibility.Visible)
                y = Canvas.GetTop(InputCard) - GAP - th;
            if (y < MARGIN) y = _orbY + ORB + GAP + GEAR + 8;   // 上面没地方了，退回圆钮下方
            if (y + th > Height - MARGIN) y = Height - MARGIN - th;
            if (y < MARGIN) y = MARGIN;
            Canvas.SetLeft(ToastBox, x);
            Canvas.SetTop(ToastBox, y);
        }

        // ============================ 动画 ============================
        private static void AnimOpacity(UIElement el, double to, double ms, bool fromZero = false)
        {
            var a = new DoubleAnimation(fromZero ? 0 : el.Opacity, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            el.BeginAnimation(OpacityProperty, a);
        }

        private static void AnimScale(ScaleTransform t, double to, double ms)
        {
            var e = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var ax = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = e };
            var ay = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = e };
            t.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
        }

        private static void AnimY(TranslateTransform t, double to, double ms)
        {
            var e = new CubicEase { EasingMode = EasingMode.EaseOut };
            t.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = e });
        }

        private static void AnimAngle(RotateTransform t, double to, double ms)
        {
            var e = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
            t.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = e });
        }

        private void ShowEl(FrameworkElement el, double ms = 240)
        {
            el.Visibility = Visibility.Visible;
            AnimOpacity(el, 1, ms, true);
        }

        private void HideEl(FrameworkElement el, double ms = 180, double delay = 0)
        {
            var a = new DoubleAnimation(el.Opacity, 0, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            a.Completed += (s, e) => { el.Visibility = Visibility.Collapsed; el.BeginAnimation(OpacityProperty, null); el.Opacity = 0; };
            el.BeginAnimation(OpacityProperty, a);
        }

        // ============================ 悬浮钮交互 ============================
        private void OrbHost_MouseEnter(object sender, MouseEventArgs e)
        {
            _orbHover = true;
            AnimScale(OrbScale, ORB_SCALE_ON, 220);
        }

        private void OrbHost_MouseLeave(object sender, MouseEventArgs e)
        {
            _orbHover = false;
            if (_dragging) return;      // 拖动中保持放大
            AnimScale(OrbScale, _inputOpen ? ORB_SCALE_ON : ORB_SCALE_IDLE, 200);
        }

        private void OrbHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragMoved = false;
            _dragFrom = e.GetPosition(this);
            _dragOrbX = _orbX;
            _dragOrbY = _orbY;
            OrbHost.CaptureMouse();
        }

        private void OrbHost_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point p = e.GetPosition(this);
            double dx = p.X - _dragFrom.X;
            double dy = p.Y - _dragFrom.Y;
            if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            _dragMoved = true;
            _orbX = _dragOrbX + dx;
            _orbY = _dragOrbY + dy;
            ClampOrb();
            PositionAll();
        }

        private void OrbHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            OrbHost.ReleaseMouseCapture();
            if (_dragMoved)
            {
                _cfg.orb_x = _orbX;
                _cfg.orb_y = _orbY;
                _cfg.Save();
            }
            else
            {
                ToggleInput();
            }
        }

        private void ToggleInput()
        {
            if (_inputOpen) CloseInput(); else OpenInput();
        }

        private void OpenInput()
        {
            _inputOpen = true;
            _cfg.panel_open = true;
            InputCard.Visibility = Visibility.Visible;
            ShowEl(InputCard, 260);
            AnimScale(InputScale, 1, 260);
            AnimScale(OrbScale, ORB_SCALE_ON, 260);
            AnimAngle((RotateTransform)OrbIcon.RenderTransform, 45, 260);
            ShowEl(SettingsBtnHost, 240);
            ShowEl(AsrBtnHost, 240);
            ShowEl(ExitBtnHost, 240);
            SetWideInput(ShouldWidenInput());   // 关掉再打开时上次的长文本还在，宽度要跟着还原
            PositionAll();
            if (!IsActive) { try { Activate(); } catch { } }
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            if (_cfg.auto_hide > 0 && InputBox.Text.Length == 0) StartHideTimer();
        }

        private void CloseInput()
        {
            _inputOpen = false;
            _cfg.panel_open = false;
            StopIrodori();
            // 输入窗关了，刚才那句的语音文件也不再需要
            CleanupIrodoriAudio();
            CloseDropdown();
            CloseSettingsPanel();
            // 聆听中保留底部字幕卡：收起翻译面板后仍然继续出字幕，方便一边看视频一边看字幕。
            // 没在聆听时才顺手收起卡片；正在加载模型也保留，避免用户误以为已经停止。
            if (!_liveRunning && !_liveLoading) CloseAsrCard(false);
            HideEl(InputCard, 180);
            HideEl(ResultCard, 180);
            HideEl(SettingsBtnHost, 180);
            HideEl(AsrBtnHost, 180);
            HideEl(ExitBtnHost, 180);
            AnimScale(OrbScale, _orbHover ? ORB_SCALE_ON : ORB_SCALE_IDLE, 220);
            AnimAngle((RotateTransform)OrbIcon.RenderTransform, 0, 200);
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V：剪贴板里是图片（截图）就当图片翻译，否则交给输入框走默认的文本粘贴
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0
                && _inputOpen && Clipboard.ContainsImage() && !Clipboard.ContainsText())
            {
                e.Handled = true;
                ClearImage();
                LoadImageFromClipboard(true);
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (LangDropdown.Visibility == Visibility.Visible) { CloseDropdown(); e.Handled = true; return; }
                if (SettingsPanel.Visibility == Visibility.Visible) { CloseSettingsPanel(); e.Handled = true; return; }
                if (DictPanel.Visibility == Visibility.Visible) { HideDictPanel(); e.Handled = true; return; }
                if (AsrCard.Visibility == Visibility.Visible) { CloseAsrCard(true); e.Handled = true; return; }
                if (_busy && _cts != null) { _cts.Cancel(); Toast("已取消翻译"); e.Handled = true; return; }
                if (_imageBytes != null) { ClearImage(); Toast("已移除图片"); e.Handled = true; return; }
                if (_inputOpen) { CloseInput(); e.Handled = true; }
            }
        }

        // ============================ 输入框 ============================
        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool empty = InputBox.Text.Length == 0;
            InputPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            // 文本变长后卡片要加宽、四颗按钮改成 2×2 格子，免得按钮挤在一行把文本框压成两三个字宽。
            // 判断只靠「这段字有多宽」（不依赖 Layout 才有的 LineCount/ActualHeight），所以可以当场决定、
            // 当场排版一次搞定；以前是同步排一次 + 延后再排一次，中间那一帧就是肉眼看到的抖。
            if (_inputOpen)
            {
                SetWideInput(ShouldWidenInput());
                PositionAll();
            }

            if (_cfg.auto_hide > 0)
            {
                if (empty) StartHideTimer(); else _hideTimer.Stop();
            }

            if (_suppressAuto || _busy) return;
            if (_cfg.auto_translate == "typing" && !empty)
            {
                _autoTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, _cfg.auto_delay));
                _autoTimer.Stop();
                _autoTimer.Start();
            }
        }

        /// <summary>窄模式下量一次文本框的真实可用宽度，作为加宽判断的基准。</summary>
        private void InputBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 加宽后量到的是宽模式的宽度，不能拿来当阈值，所以只在窄模式记。
            if (_wideInput) return;
            double w = e.NewSize.Width - InputBox.Padding.Left - InputBox.Padding.Right;
            if (w > 1) _narrowTextW = w;
        }

        private void StartHideTimer()
        {
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.auto_hide));
            _hideTimer.Start();
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 回车 = 翻译；Shift+回车 = 在输入框里换行（AcceptsReturn=True 才能插进换行）
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = TranslateAsync();
            }
        }

        // 输入框的滚动条是自己画的（4px 细条），TextBox 默认那套按行滚动的逻辑在这种细条上不灵，
        // 所以滚轮自己算：一格滚动 48px（约 3 行）。
        private void InputBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ScrollInputBy(e.Delta)) e.Handled = true;
        }

        /// <summary>按滚轮方向滚动输入框，返回 true 表示确实滚动了（没内容可滚就返回 false，交给默认处理）。</summary>
        private bool ScrollInputBy(double delta)
        {
            var sv = InputBox.Template.FindName("PART_ContentHost", InputBox) as ScrollViewer;
            if (sv == null || sv.ScrollableHeight <= 0) return false;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - Math.Sign(delta) * 48);
            return true;
        }

        private void InputBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (_cfg.auto_translate == "paste")
            {
                Dispatcher.InvokeAsync(() => { _autoTimer.Interval = TimeSpan.FromMilliseconds(80); _autoTimer.Stop(); _autoTimer.Start(); },
                    DispatcherPriority.Background);
            }
        }

        // ============================ 图片翻译 ============================
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

        private static string MimeOf(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".gif": return "image/gif";
                default: return "image/png";
            }
        }

        private void ImageBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要翻译的图片",
                Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            LoadImageFile(dlg.FileName, true);
        }

        /// <summary>从磁盘读一张图片进来（图片按钮 / 拖拽都走这里）。</summary>
        private bool LoadImageFile(string path, bool autoTranslate)
        {
            try
            {
                if (!File.Exists(path)) { Toast("找不到图片文件"); return false; }
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > 12 * 1024 * 1024) { Toast("图片太大了（超过 12MB）"); return false; }
                SetImage(bytes, MimeOf(path), Path.GetFileName(path), true);
                App.Log("图片翻译: 载入 " + path + "（" + (bytes.Length / 1024) + " KB）");
                if (autoTranslate) _ = TranslateAsync();
                return true;
            }
            catch (Exception ex) { App.Log("图片载入失败: " + ex.Message); Toast("图片载入失败：" + ex.Message); return false; }
        }

        /// <summary>从剪贴板取图片（Ctrl+V 截图 / 粘贴按钮）。剪贴板里没图片就返回 false。</summary>
        private bool LoadImageFromClipboard(bool autoTranslate)
        {
            try
            {
                if (!Clipboard.ContainsImage()) return false;
                var src = Clipboard.GetImage();
                if (src == null) return false;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    SetImage(ms.ToArray(), "image/png", "剪贴板截图.png", true);
                }
                App.Log("图片翻译: 从剪贴板载入截图 " + src.PixelWidth + "x" + src.PixelHeight);
                if (autoTranslate) _ = TranslateAsync();
                return true;
            }
            catch (Exception ex) { App.Log("剪贴板图片读取失败: " + ex.Message); Toast("剪贴板图片读取失败：" + ex.Message); return false; }
        }

        private void SetImage(byte[] bytes, string mime, string name, bool notify)
        {
            _imageBytes = bytes;
            _imageMime = mime;
            _imageName = name;
            ImageName.Text = name;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立刻解码，不锁文件
                bmp.DecodePixelWidth = 56;                    // 缩略图不需要原图分辨率
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                ImageThumb.Source = bmp;
            }
            catch (Exception ex) { App.Log("缩略图生成失败: " + ex.Message); ImageThumb.Source = null; }
            ImageStrip.Visibility = Visibility.Visible;
            if (_inputOpen) PositionAll();
            if (notify) Toast("已载入图片，按 Enter 翻译");
        }

        /// <summary>移除挂着的图片（清空 / ✕ 都走这里）。</summary>
        private void ClearImage()
        {
            if (_imageBytes == null) return;
            _imageBytes = null;
            _imageName = "";
            ImageThumb.Source = null;
            ImageStrip.Visibility = Visibility.Collapsed;
            if (_inputOpen) PositionAll();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasImageFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!HasImageFile(e.Data)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) LoadImageFile(files[0], false);
        }

        private static bool HasImageFile(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return false;
            string ext = Path.GetExtension(files[0] ?? "").ToLowerInvariant();
            foreach (var x in ImageExts) if (x == ext) return true;
            return false;
        }

        // ============================ 按钮 ============================
        private void PasteBtn_Click(object sender, RoutedEventArgs e)
        {
            // 剪贴板里是图片（截图）就直接翻图片
            if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
            {
                ClearImage();
                if (LoadImageFromClipboard(true)) return;
            }
            string txt = GetClipboardText();
            if (string.IsNullOrWhiteSpace(txt)) { Toast("剪贴板里没有文本"); return; }
            ClearImage();               // 换成文本翻译，图片就不挂了
            _suppressAuto = true;
            InputBox.Text = txt;
            InputBox.CaretIndex = txt.Length;
            _suppressAuto = false;
            ShowResultCard();
            _ = TranslateAsync();
        }

        private void TranslateBtn_Click(object sender, RoutedEventArgs e) { _ = TranslateAsync(); }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && _busy) _cts.Cancel();
            StopIrodori();
            CleanupIrodoriAudio();   // 清空之后这段译文就没了，语音文件也跟着删
            _suppressAuto = true;
            InputBox.Clear();
            _suppressAuto = false;
            ClearImage();
            ResultText.Text = "";
            ResultMeta.Text = "";
            HideEl(ResultCard, 160);
            InputPlaceholder.Visibility = Visibility.Visible;
            if (_inputOpen) PositionAll();
            InputBox.Focus();
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e) { CopyResult(true); }

        private void SpeakBtn_Click(object sender, RoutedEventArgs e) { SpeakResult(true); }

        private void CopyResult(bool notify)
        {
            string t = ResultText.Text;
            if (string.IsNullOrWhiteSpace(t)) { if (notify) Toast("还没有译文"); return; }
            SetClipboardText(t);
            if (notify) Toast("已复制译文");
        }

        private void SpeakResult(bool notify)
        {
            string t = ResultText.Text;
            if (string.IsNullOrWhiteSpace(t)) { if (notify) Toast("还没有译文"); return; }
            SpeakText(t, _cfg.target, notify);
        }

        /// <summary>按设置分发：系统语音（离线）或 Edge 在线语音。在线模式下再点一次 = 停止。</summary>
        private void SpeakText(string text, string lang, bool toggle)
        {
            if (IsEdgeSpeak())
            {
                if (_edge.Busy)
                {
                    if (toggle) { _edge.Stop(); Toast("已停止"); }
                    return;
                }
                string voice = EdgeVoiceFor(lang);
                Toast("在线朗读合成中…");
                _ = _edge.SpeakAsync(text, voice, _cfg.speak_rate,
                        msg => Dispatcher.InvokeAsync(() => Toast(msg, true)));
                return;
            }
            _speaker.Speak(text, lang, _cfg.speak_rate, msg => Dispatcher.InvokeAsync(() => Toast(msg, true)));
        }

        private bool IsEdgeSpeak()
        {
            return string.Equals(_cfg.speak_engine, "edge", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>点词时念一下这个词：先掐掉上一句，再念辞书形（日语）。失败只记日志，不弹提示。</summary>
        internal void SpeakWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return;
            try
            {
                App.Log("点词发音: " + word + "（" + (IsEdgeSpeak() ? "edge" : "system") + "）");
                // 只是顺手念一下：失败记日志就行，不要用弹窗挡在查词前面
                Action<string> quiet = msg => App.Log("点词发音失败：" + msg);
                if (IsEdgeSpeak())
                {
                    if (_edge.Busy) _edge.Stop();
                    _ = _edge.SpeakAsync(word, EdgeVoiceFor("ja"), _cfg.speak_rate, quiet);
                }
                else
                {
                    _speaker.Stop();
                    _speaker.Speak(word, "ja", _cfg.speak_rate, quiet);
                }
            }
            catch (Exception ex) { App.Log("点词发音失败：" + ex.Message); }
        }

        private string EdgeVoiceFor(string lang)
        {
            string p = (lang ?? "").Split('-')[0].ToLowerInvariant();
            string v = p == "ja" ? _cfg.edge_voice_ja
                     : p == "en" ? _cfg.edge_voice_en
                     : _cfg.edge_voice_zh;
            if (string.IsNullOrWhiteSpace(v)) v = EdgeTts.DefaultVoice(p);
            return v.Trim();
        }

        // ---------- 第三方语音：本地 Irodori-TTS（结果卡上的「あ」按钮，只在目标语言=日语时出现） ----------

        /// <summary>「あ」按钮只在目标语言是日语时显示；它和系统朗读按钮（🔊）并存，不覆盖。</summary>
        private void UpdateIrodoriBtn()
        {
            bool ja = string.Equals(_cfg.target, "ja", StringComparison.OrdinalIgnoreCase);
            if (ja)
            {
                if (IrodoriBtn.Visibility != Visibility.Visible)
                {
                    IrodoriBtn.Visibility = Visibility.Visible;
                    AnimOpacity(IrodoriBtn, 1, 200, true);
                }
            }
            else if (IrodoriBtn.Visibility == Visibility.Visible)
            {
                IrodoriBtn.Visibility = Visibility.Collapsed;
            }
            UpdateReplayBtn();
            if (!_audio.IsPlaying) SetIrodoriState(false, false);
        }

        /// <summary>
        /// 「重播」按钮：上一轮生成的语音文件还在、且目标语言是日语时才出现。
        /// 它只重放音频，不再调用服务（合成一次 3 秒起，重播是即时的）。
        /// </summary>
        private void UpdateReplayBtn()
        {
            bool ja = string.Equals(_cfg.target, "ja", StringComparison.OrdinalIgnoreCase);
            bool has = false;
            if (ja)
                foreach (string p in _lastSpeakWavs)
                    if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) { has = true; break; }
            if (ReplayBtn == null) return;
            ReplayBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>上一轮 Irodori 朗读生成的那几段 wav（重播用；文件被清理后自动失效）。</summary>
        private readonly List<string> _lastSpeakWavs = new List<string>();

        private void ReplayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_irodoriBusy || _audio.IsPlaying)
            {
                StopIrodori();
                Toast("已停止");
                return;
            }
            _ = ReplayIrodoriAsync();
        }

        /// <summary>重播上一轮生成的语音（多段就按顺序再放一遍）。</summary>
        private async Task ReplayIrodoriAsync()
        {
            var wavs = new List<string>();
            foreach (string p in _lastSpeakWavs)
                if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) wavs.Add(p);
            if (wavs.Count == 0) { Toast("还没有可重播的语音"); UpdateReplayBtn(); return; }
            if (_irodoriBusy) return;

            _irodoriBusy = true;
            _irodoriCts = new CancellationTokenSource();
            var swAll = Stopwatch.StartNew();
            _irodoriSw = swAll;
            int played = 0;
            try
            {
                App.Log("Irodori 重播: 共 " + wavs.Count + " 段");
                // 重播全程走连续播放：所有段一次性接进同一个音频设备，段间没有重新开播的静音
                bool streamOk = true;
                _audio.BeginStream();
                for (int i = 0; i < wavs.Count; i++)
                {
                    if (_irodoriCts.IsCancellationRequested) break;
                    if (streamOk)
                    {
                        string aerr;
                        if (_audio.AppendWav(wavs[i], out aerr))
                        {
                            SetIrodoriState(true, false);
                            Toast(wavs.Count > 1 ? ("Irodori-TTS 重播中 " + (i + 1) + "/" + wavs.Count) : "Irodori-TTS 重播中");
                            App.Log("Irodori 重播 第 " + (i + 1) + "/" + wavs.Count + " 段 接入连续播放（" + System.IO.Path.GetFileName(wavs[i]) + "）");
                            played++;
                            continue;
                        }
                        App.Log("Irodori 连续播放不可用: " + aerr + "（退回逐段重播）");
                        streamOk = false;
                    }
                    try { await PlayOneAsync(wavs[i], i + 1, wavs.Count); }
                    catch (Exception pex)
                    {
                        App.Log("Irodori 重播失败: " + pex.Message);
                        Toast("重播失败：" + pex.Message, true);
                        break;
                    }
                    played++;
                }
                if (streamOk)
                {
                    _audio.EndStream();
                    await _audio.WaitDrainAsync(_irodoriCts.Token);
                }
                swAll.Stop();
                App.Log("Irodori 重播结束，共 " + played + " 段，总耗时 " + (swAll.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s");
            }
            catch (Exception ex)
            {
                App.Log("Irodori 重播异常: " + ex.Message);
                if (!(ex is OperationCanceledException)) Toast("重播失败：" + ex.Message, true);
            }
            finally
            {
                _playTcs = null;
                _irodoriBusy = false;
                if (_irodoriCts != null) { _irodoriCts.Dispose(); _irodoriCts = null; }
                SetIrodoriState(false, false);
                HideBusyToast();
                _irodoriSw = null;
                UpdateReplayBtn();
            }
        }

        /// <summary>
        /// 播一段并等它播完。时长按**这一段的 wav 头**算（以前用「上一段打开时记下的时长」，走 winmm 兜底时不会更新，
        /// 于是长段被短段掐断）。到点之后**只要还在响就继续等**——本机 MediaPlayer 起播要 1~4 秒
        /// （日志里 3.2 秒的音频从 Play() 到 Ended 用了 6.8 秒），按「时长 + 2 秒」硬判会把还在响的那一段切掉，
        /// 下一段的 Play() 一进来就 Stop 掉它 —— 听起来就是「后半句被跳过、直接接下一段」。
        /// </summary>
        private async Task PlayOneAsync(string wav, int index, int total)
        {
            _playTcs = new TaskCompletionSource<bool>();
            SetIrodoriState(true, false);
            Toast(total > 1 ? ("Irodori-TTS 播放中 " + index + "/" + total) : "Irodori-TTS 播放中");
            App.Log("Irodori 第 " + index + "/" + total + " 段 开始播放 +" + Pip() + "ms（" + System.IO.Path.GetFileName(wav) + "）");
            _audio.Play(wav);
            TimeSpan dur = AudioPlayer.WavDuration(wav);
            if (dur <= TimeSpan.Zero) dur = _lastAudioLen;
            TimeSpan baseWait = (dur > TimeSpan.Zero ? dur : TimeSpan.FromSeconds(20)) + TimeSpan.FromSeconds(2);
            var tcs = _playTcs;
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(baseWait));
            bool byWatchdog = !ReferenceEquals(winner, tcs.Task);
            int grace = 0;
            // 还在响：一段宽限（每 1.5 秒看一次，最多再等 30 秒）——宁可多等，也不要把还在响的音频切掉
            while (byWatchdog && _audio.IsPlaying && grace < 20)
            {
                grace++;
                winner = await Task.WhenAny(tcs.Task, Task.Delay(1500));
                if (ReferenceEquals(winner, tcs.Task)) { byWatchdog = false; break; }
            }
            string tail = "";
            if (byWatchdog)
            {
                tail = "（看门狗：本段 wav 时长 " + dur.TotalSeconds.ToString("0.0") + "s"
                     + (grace > 0 ? "，又宽限了 " + (grace * 1.5).ToString("0.0") + "s" : "")
                     + (_audio.IsPlaying ? "，仍在播放 → 强制进入下一段" : "，播放器已停但没有结束回调") + "）";
            }
            App.Log("Irodori 第 " + index + "/" + total + " 段 播放结束 +" + Pip() + "ms" + tail);
            _playTcs = null;
        }

        /// <summary>
        /// 按设置造一个 Irodori 客户端。<paramref name="chunkCount"/> &gt; 1（多段朗读）时，
        /// 会把 Caption / Seed 在这一份实例上「钉死」：整段长文的每一段都用同一份参数发请求。
        /// 为什么需要：服务端 seed_raw 留空时是「每个请求各抽一次随机种子」（Run Log 里的
        /// seed_used 每段都不一样，日志里能查到），于是段与段之间音色 / 情绪会跳变，
        /// 听感像换了个人在念。Caption 同理——它也是整轮朗读的一份快照。
        /// 注意：这个实例被 SpeakIrodoriAsync 整轮复用（见那里的注释），
        /// 所以参数是「透传」而不是每段重读配置——播放中途改设置不会让后半段换声音。
        /// </summary>
        private IrodoriTts BuildIrodori(int chunkCount = 1)
        {
            // FromConfig 把 caption / 采样参数 / 参考音频从配置里各复制一份到实例上
            var tts = IrodoriTts.FromConfig(_cfg, ResolveSeed());
            // 用户选了「无」/「随机」但清单为空时 Seed 是空串：多段朗读就在本地钉一个固定值，
            // 只影响这一轮；单段朗读保持原样（不改变「每次随机」的听感）。
            if (chunkCount > 1 && string.IsNullOrEmpty(tts.Seed))
            {
                tts.Seed = _seedRnd.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture);
                App.Log("Irodori 多段朗读: 本轮统一 seed=" + tts.Seed + "（" + chunkCount + " 段共用）");
            }
            return tts;
        }

        private readonly Random _seedRnd = new Random();

        /// <summary>
        /// 把设置里的「随机种子」选择换算成发给服务端的字符串：
        /// 「无」→ 空字符串（服务端每次抽随机种子）；「随机」→ 从自定义种子里抽一个；
        /// 「种子A」等 → 用该种子的值；找不到或清单为空时回退成空（随机）。
        /// </summary>
        private string ResolveSeed()
        {
            string choice = (_cfg.irodori_seed_choice ?? "").Trim();
            if (choice.Length == 0 || choice == "无") return "";
            var list = _cfg.irodori_seeds ?? new List<AppConfig.IrodoriSeed>();
            if (choice == "随机")
            {
                if (list.Count == 0) return "";
                var pick = list[_seedRnd.Next(list.Count)];
                App.Log("随机种子: 从 " + list.Count + " 个自定义种子里抽到「" + pick.name + "」= " + pick.value);
                return pick.value;
            }
            foreach (var s in list) if (s != null && s.name == choice) return s.value;
            App.Log("随机种子: 找不到自定义种子「" + choice + "」，改用随机");
            return "";
        }

        private void SetIrodoriState(bool playing, bool busy)
        {
            IrodoriIcon.Text = busy ? "…" : (playing ? "■" : "あ");
            IrodoriBtn.ToolTip = busy ? "Irodori-TTS 合成中…"
                                      : (playing ? "停止播放" : "用本地 Irodori-TTS 朗读（日语）");
        }

        /// <summary>当前这一段音频的时长（用来给「等播完」加看门狗）。</summary>
        private TimeSpan _lastAudioLen;

        /// <summary>分段朗读时，用它在「这一段放完了」和播放循环之间传递信号。</summary>
        private TaskCompletionSource<bool> _playTcs;

        private void AudioEnded()
        {
            // 分段流水线在等这一段播完：把等待放行（收尾由 SpeakIrodoriAsync 统一做），否则就直接复位按钮
            var t = _playTcs;
            if (t != null) { _playTcs = null; t.TrySetResult(true); }
            else SetIrodoriState(false, false);
        }

        /// <summary>停止 Irodori 播放，并把还在飞的合成请求 / 等待中的播放循环一起放掉。</summary>
        private void StopIrodori()
        {
            try { _audio.Stop(); } catch (Exception) { }
            if (_irodoriCts != null && !_irodoriCts.IsCancellationRequested)
            {
                try { _irodoriCts.Cancel(); } catch (Exception) { }
            }
            // 播放被 Stop 掉时 Ended 不会触发（AudioPlayer 会作废在飞回调），必须自己释放等待
            var t = _playTcs;
            if (t != null) { _playTcs = null; t.TrySetResult(false); }
            SetIrodoriState(false, false);
        }

        /// <summary>
        /// 清理已经生成的 Irodori 语音文件（服务端写在 %TEMP%\gradio 里的临时 wav 不会自己消失）。
        /// sweepLeftovers=true 时额外扫一遍临时目录，把以前没删干净的 sample_* 也删掉。
        /// </summary>
        private void CleanupIrodoriAudio(bool sweepLeftovers = false, bool notify = false)
        {
            try
            {
                var r = IrodoriTts.CleanupCache(sweepLeftovers);
                _lastSpeakWavs.Clear();
                UpdateReplayBtn();
                if (r.Count > 0)
                    App.Log("Irodori 语音缓存清理: 删除 " + r.Count + " 个文件 (" + (r.Bytes / 1048576.0).ToString("0.0") + " MB)");
                if (notify)
                {
                    if (r.Count == 0) Toast("没有需要清理的语音缓存");
                    else Toast("已清理 " + r.Count + " 个语音缓存（" + (r.Bytes / 1048576.0).ToString("0.0") + " MB）");
                }
            }
            catch (Exception ex) { App.Log("Irodori 语音缓存清理失败: " + ex.Message); }
        }

        private void IrodoriBtn_Click(object sender, RoutedEventArgs e)
        {
            // 合成中或播放中，单击都是「停」
            if (_irodoriBusy || _audio.IsPlaying)
            {
                StopIrodori();
                Toast("已停止");
                return;
            }
            _ = SpeakIrodoriAsync();
        }

        /// <summary>
        /// 朗读译文。长文先按句子切成若干段（每段不超过 irodori_chunk_chars 字），
        /// 再「边放已经生成好的、边合成下一段」：第一段合成完就能听到，不必等全文合成完。
        /// </summary>
        private async Task SpeakIrodoriAsync()
        {
            if (_irodoriBusy) return;
            string full = (ResultText.Text ?? "").Trim();
            if (full.Length == 0) { Toast("还没有译文"); return; }

            // 合成新的之前，先把上一段生成的音频删掉
            StopIrodori();
            CleanupIrodoriAudio();

            var chunks = SplitForSpeech(full);
            _irodoriBusy = true;
            _irodoriCts = new CancellationTokenSource();
            SetIrodoriState(false, true);
            Toast(chunks.Count > 1 ? ("Irodori-TTS 合成中…（共 " + chunks.Count + " 段）") : "Irodori-TTS 合成中…");
            if (chunks.Count > 1)
            {
                var lens = new List<string>();
                foreach (string c in chunks) lens.Add(c.Length.ToString());
                App.Log("Irodori 长文分段: " + full.Length + " 字 → " + chunks.Count + " 段（上限 "
                    + _cfg.irodori_chunk_chars + " 字）段长=[" + string.Join(",", lens.ToArray()) + "]");
            }

            // 上下文一致性：整轮朗读共用一个 IrodoriTts（caption / seed / 参考音频都是同一份），
            // 多段时把 seed 钉死，否则服务端每个请求各抽一次种子，段间音色会跳（见 BuildIrodori 的注释）。
            // 这个实例在下面所有 SynthChunkAsync 里复用，改设置只影响下一轮。
            var tts = BuildIrodori(chunks.Count);
            // 切分前缀：这一轮开头拍一份快照，播放中途改设置不影响还没合成的段（和 caption / seed 一个道理）。
            // 只有多段时才有意义（B、C… 段才会拼上）；单段朗读原样发。
            string chunkPrefix = _cfg.irodori_chunk_prefix ?? "";
            if (chunkPrefix.Length > 0)
                App.Log("Irodori 切分前缀: \"" + chunkPrefix + "\"（" + chunkPrefix.Length + " 字，"
                    + (chunks.Count > 1 ? "第 2 段起生效" : "本轮只有 1 段，不生效") + "）");
            var swAll = Stopwatch.StartNew();
            _irodoriSw = swAll;
            int played = 0;
            var madeWavs = new List<string>();   // 这一轮生成的 wav，留着给「重播」用
            try
            {
                // 预合成窗口：本地服务是单卡的，同时发多个请求只会互相抢 GPU
                //（实测 3 个并发时第一段从 3.5 秒拖到 12 秒，完成顺序还会乱），
                // 所以只用「一段在放、下一段在合成」这种一段的提前量；IrodoriTts 里还有闸门兜底。
                const int look = 1;
                var pending = new Dictionary<int, Task<string>>();
                for (int i = 0; i < chunks.Count && i < look; i++)
                    pending[i] = SynthChunkAsync(tts, chunks[i], i + 1, chunks.Count, chunkPrefix, _irodoriCts.Token);

                // 连续播放：每段合成完就立刻追加进同一个音频设备（WaveOutStream），
                // 段与段之间没有「关掉播放器再重开」的 0.4~0.7 秒静音。
                // 设备打不开 / 音频不是 16 位 PCM 时，退回原来的「逐段 Play + 等播完」（streamOk=false）。
                bool streamOk = true;
                _audio.BeginStream();

                for (int i = 0; i < chunks.Count; i++)
                {
                    if (_irodoriCts.IsCancellationRequested) break;
                    string wav = await pending[i];
                    if (_irodoriCts.IsCancellationRequested) break;

                    // 立刻把后面还没排的段补进队列 —— 这就是「边放边合成」的来源
                    if (i + look < chunks.Count)
                        pending[i + look] = SynthChunkAsync(tts, chunks[i + look], i + look + 1, chunks.Count, chunkPrefix, _irodoriCts.Token);

                    if (streamOk)
                    {
                        string aerr;
                        if (_audio.AppendWav(wav, out aerr))
                        {
                            // 不等待：这一段已经在设备缓冲里排队了，循环会马上去取下一段
                            SetIrodoriState(true, false);
                            Toast(chunks.Count > 1 ? ("Irodori-TTS 播放中 " + (i + 1) + "/" + chunks.Count) : "Irodori-TTS 播放中");
                            App.Log("Irodori 第 " + (i + 1) + "/" + chunks.Count + " 段 接入连续播放 +" + Pip() + "ms（" + System.IO.Path.GetFileName(wav) + "）");
                            played++;
                            madeWavs.Add(wav);
                            continue;
                        }
                        App.Log("Irodori 连续播放不可用: " + aerr + "（本段起退回逐段播放）");
                        streamOk = false;
                    }

                    try
                    {
                        await PlayOneAsync(wav, i + 1, chunks.Count);
                    }
                    catch (Exception pex)
                    {
                        // 合成成功、只是没法出声：不要把这种情况说成合成失败
                        App.Log("Irodori 播放失败: " + pex.Message);
                        SetIrodoriState(false, false);
                        Toast("已合成，但播放失败：" + pex.Message, true);
                        break;
                    }
                    played++;
                    madeWavs.Add(wav);
                }
                if (streamOk)
                {
                    _audio.EndStream();
                    // 这里才等「真正播完」：中途一直不阻塞，所以合成和播放是叠着走的
                    await _audio.WaitDrainAsync(_irodoriCts.Token);
                }
                swAll.Stop();
                if (played > 0)
                    App.Log("Irodori-TTS 播放结束，共 " + played + " 段，总耗时 " + (swAll.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s");
            }
            catch (Exception ex)
            {
                swAll.Stop();
                string msg = (ex is OperationCanceledException) ? "已取消" : ex.Message;
                App.Log("Irodori-TTS 失败: " + msg);
                if (!(ex is OperationCanceledException)) Toast("Irodori-TTS 失败：" + msg, true);
            }
            finally
            {
                _playTcs = null;
                _irodoriBusy = false;
                if (_irodoriCts != null) { _irodoriCts.Dispose(); _irodoriCts = null; }
                SetIrodoriState(false, false);
                // 这一轮的文件**留着**，好让「重播」按钮能再放一遍；
                // 下一次朗读 / 下一次翻译 / 关输入窗时才统一删（那些地方都会先 CleanupIrodoriAudio）。
                _lastSpeakWavs.Clear();
                if (played > 0) _lastSpeakWavs.AddRange(madeWavs);
                UpdateReplayBtn();
                HideBusyToast();           // 播完了就把「播放中」提示条收掉，别让它继续压着按钮
                _irodoriSw = null;
            }
        }

        /// <summary>相对本次朗读开始的毫秒数（日志里用，便于核对「播放和合成是否真的重叠」）。</summary>
        private long Pip()
        {
            return _irodoriSw == null ? 0 : _irodoriSw.ElapsedMilliseconds;
        }

        /// <summary>
        /// 合成一段。流水线里它可能在播放上一段的同时跑。
        /// <paramref name="prefix"/> 是设置里的「切分前缀」：只有第 2 段及以后才会拼在文本开头
        /// （第 1 段是开头，没什么可接的）。拼出来的文本只发给 API，分段规则和译文都不受影响。
        /// </summary>
        private async Task<string> SynthChunkAsync(IrodoriTts tts, string text, int index, int total, string prefix, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            // 前缀只加在 B、C… 段（index 从 1 开始）；prefix 里的 emoji 是普通字符串，原样拼进去
            string send = (index > 1 && !string.IsNullOrEmpty(prefix)) ? prefix + text : text;
            string pfxNote = send.Length > text.Length ? ("，含前缀 " + (send.Length - text.Length) + " 字") : "";
            App.Log("Irodori 第 " + index + "/" + total + " 段 开始合成 +" + Pip() + "ms（" + text.Length + " 字" + pfxNote + "）");
            string wav = await tts.SynthesizeAsync(send, ct);
            sw.Stop();
            App.Log("Irodori-TTS 第 " + index + "/" + total + " 段 合成完成 " + sw.ElapsedMilliseconds + "ms（"
                    + text.Length + " 字" + pfxNote + "）→ " + System.IO.Path.GetFileName(wav));
            LogChunk(tts, index, total);   // 在这里记 RunLog：预合成时后一段的日志会覆盖前一段的
            return wav;
        }

        private void LogChunk(IrodoriTts tts, int index, int total)
        {
            if (!string.IsNullOrEmpty(tts.LastRunLog))
                App.Log("Irodori RunLog[" + index + "/" + total + "]: " + tts.LastRunLog.Replace("\r", "").Replace("\n", " | "));
            if (!string.IsNullOrEmpty(tts.LastTiming))
                App.Log("Irodori Timing[" + index + "/" + total + "]: " + tts.LastTiming.Replace("\r", "").Replace("\n", " | "));
        }

        // 断句优先级用到的字符集（日文为主，兼顾中英）
        private const string SentenceMarks = "。！？!?…‥";
        private const string SoftMarks = "、，,；;：:";
        private const string CloseMarks = "」』）)】》〉〕｝}］]”\"'";
        // 这些字符不能当一段的开头（小假名 / 长音符 / 标点 / 右引号），否则听起来会断在半个词上，
        // 或者段首先念一个孤零零的右引号 / 句末标点，让 TTS 在开口处停一下（用户反馈的发音怪异多来自这里）
        private const string NeverStart = "、。！？!?…‥，,；;：:」』）)】》〉〕｝}］]”\"'ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮー〜・";
        // 这些字符不能当一段的结尾（左引号 / 左括号）：引号必须和它引出的内容待在同一段，
        // 否则上一段以「『（ 收尾，TTS 会把它读成一个悬空的开引号（长停顿 / 怪音）
        private const string NeverEnd = "「『（(【《〈〔｛{［[“";
        // 助词不能和后一个词分开（切在助词前面最刺耳）
        private const string Particles = "はがをにでとへもやのねよなぞぜわさか";
        // 格助词：切在它们后面通常很自然（用于「前后都是假名」时判断能不能切）
        private const string CaseParticles = "はがをにでへものや";
        // 数字和这些单位不能拆开
        private const string Units = "円個回人年月中日時分秒％%匹台冊枚本度番歳つ杯軒件通階羽頭文字語字倍割分";

        /// <summary>
        /// 按「段落 → 句末标点 → 读点(、) → 词边界」的优先级把长文切成若干段。
        /// limit&lt;=0 或本来就不超长则不切；需要切时首段 25~35 字（见 firstLimit 处的说明）。
        /// 规则要点：不强按字数切——先断句，句子太长才在「、」处断，最后才按长度硬切，
        /// 且硬切也会避开小假名/长音符、助词前、数字与单位之间、英文单词与数字内部。
        /// 切完还有两遍 O(段数) 的收尾：「短句智能合并」（减少请求次数）与「标点/引号归属修正」（防怪音）。
        /// </summary>
        private List<string> SplitForSpeech(string text)
        {
            var result = new List<string>();
            int limit = _cfg.irodori_chunk_chars;
            string t = (text ?? "").Replace("\r", "").Trim();
            if (t.Length == 0) return result;
            if (limit <= 0 || t.Length <= limit) { result.Add(t); return result; }

            // 首段上限：25~35 字（原先是 8~20 字）。
            //   首段决定「多久能听到第一声」，但也决定「第一声能撑多久」：8~20 字大约 1~2 秒就放完，
            //   而下一段的合成要 4~5 秒（本地单卡每个请求都有 7~9 秒固定开销），
            //   播放结束到下一段出声之间就是用户听到的真空卡顿。
            //   抬到 25~35 字后首段大约 4~6 秒，正好覆盖下一段的合成时间。
            //   limit 本身更小时仍以 limit 为准（外层 min 兜住）。
            int firstLimit = Math.Min(limit, Math.Max(25, Math.Min(35, limit)));

            var pieces = SplitPieces(t, limit);
            var cur = new StringBuilder();
            foreach (string p in pieces)
            {
                int cap = (result.Count == 0) ? firstLimit : limit;
                // 单句本身就超过上限时不能再靠合并控制长度，直接成段
                if (cur.Length > 0 && cur.Length + p.Length > cap)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                cur.Append(p);
            }
            if (cur.Length > 0) result.Add(cur.ToString());
            if (result.Count == 0) result.Add(t);

            // 收尾 ①：短句合并（对话体的「そうか。」「わかった。」不再各发一次请求）
            result = MergeShortSegments(result, limit);
            // 收尾 ②：标点 / 引号归属（右引号、句末标点别跑到段首；左引号别留在段尾）
            result = FixOwnership(result);
            // 归属修正可能让某段只剩一两个字（比如单独一个 」，再并一次，避免又出现碎片段）
            result = MergeShortSegments(result, limit);
            return result;
        }

        /// <summary>
        /// 短句智能合并（减少 TTS 请求次数）：对话体一行一句会被拆成好几个极短的碎片，
        /// 每段都要吃一次 7~9 秒的固定开销，延迟和段间静默都被放大。
        /// 规则：相邻两段合并后不超过 limit 就并（原文本来就是连续的，并起来不改变读法）；
        /// 唯一例外是首段——首段是刻意切短用来「尽快出声」的，只有它短到听不出一句（&lt;12 字）时才向后并。
        /// 单遍遍历 + 字符串拼接，没有正则。
        /// </summary>
        private static List<string> MergeShortSegments(List<string> segs, int limit)
        {
            if (segs.Count < 2) return segs;
            const int MinAudible = 12;   // 短于这个字数听着像「一个字就停了」，宁可并进下一段
            var outp = new List<string>(segs.Count);
            foreach (string s in segs)
            {
                if (outp.Count > 0)
                {
                    int i = outp.Count - 1;
                    string prev = outp[i];
                    bool first = (i == 0);
                    // 首段：只有短到听不出一句才并；其余段：只要合起来不超 limit 就并
                    if ((!first || prev.Length < MinAudible) && prev.Length + s.Length <= limit)
                    {
                        outp[i] = prev + s;
                        continue;
                    }
                }
                outp.Add(s);
            }
            return outp;
        }

        /// <summary>
        /// 标点 / 引号的「归属权」修正——引号、括号、句末标点必须跟着它修饰的那一句走：
        ///   · 右引号 」』）、句末标点 。！？… 不能出现在段首（否则 TTS 一开口先念个孤立标点，怪音/长停顿）；
        ///   · 左引号 「『（ 不能留在段尾（悬空的开引号同样会让它在段尾拖一下）。
        /// 只做「相邻两段之间各挪几个字符」，不重新切分、不动顺序，所以不会改变读出来的文字，
        /// 也不改变总段数；段长最多因此超出上限两三个字（比切坏引号划算）。
        /// 单遍遍历，O(段数)，不引入正则。
        /// </summary>
        private static List<string> FixOwnership(List<string> segs)
        {
            if (segs.Count < 2) return segs;
            for (int i = 0; i < segs.Count - 1; i++)
            {
                // ① 下一段开头是右引号/右括号/句末标点/软标点/小假名 → 这些属于上一段，挪回去
                string next = segs[i + 1];
                int k = 0;
                while (k < next.Length - 1 && NeverStart.IndexOf(next[k]) >= 0) k++;
                if (k > 0)
                {
                    segs[i] += next.Substring(0, k);
                    segs[i + 1] = next.Substring(k);
                }

                // ② 上一段结尾是左引号/左括号 → 它引出的内容在下一段，把它推过去
                string prev = segs[i];
                int m = prev.Length;
                while (m > 1 && NeverEnd.IndexOf(prev[m - 1]) >= 0) m--;
                if (m < prev.Length)
                {
                    segs[i] = prev.Substring(0, m);
                    segs[i + 1] = prev.Substring(m) + segs[i + 1];
                }
            }
            return segs;
        }

        /// <summary>先切成「足够小」的片段：段落/句末标点断句，超长句再在「、」处断，最后才硬切。</summary>
        private static List<string> SplitPieces(string t, int limit)
        {
            var pieces = new List<string>();
            var sb = new StringBuilder();
            foreach (char ch in t)
            {
                if (ch == '\n' || ch == '\r')
                {
                    if (sb.Length > 0) { pieces.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(ch);
                if (SentenceMarks.IndexOf(ch) >= 0) { pieces.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) pieces.Add(sb.ToString());

            var fine = new List<string>();
            foreach (string raw in pieces)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                if (p.Length <= limit) { fine.Add(p); continue; }
                // 句子超长：退一级在「、」这类读点处切
                foreach (string s in SplitAt(p, SoftMarks))
                {
                    string q = s.Trim();
                    if (q.Length == 0) continue;
                    if (q.Length <= limit) fine.Add(q);
                    else fine.AddRange(HardSplit(q, limit));   // 最后手段：按长度硬切（下面会避开危险位置）
                }
            }
            return fine;
        }

        /// <summary>按给定标记切（标记留在前一片的末尾）。</summary>
        private static IEnumerable<string> SplitAt(string s, string marks)
        {
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                sb.Append(ch);
                if (marks.IndexOf(ch) >= 0) { yield return sb.ToString(); sb.Clear(); }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        /// <summary>单句仍然超长时按长度硬切：在允许的窗口里挑「最自然」的切点（见 CutTier），找不到就按长度切。</summary>
        private static IEnumerable<string> HardSplit(string s, int limit)
        {
            int start = 0;
            while (start < s.Length)
            {
                int remain = s.Length - start;
                if (remain <= limit) { yield return s.Substring(start); yield break; }

                int len = limit;
                int floor = Math.Max(1, limit / 2);
                int best = -1, bestTier = 99;
                for (int i = start + limit; i > start + floor; i--)
                {
                    int tier = CutTier(s, i);
                    if (tier < bestTier) { bestTier = tier; best = i; if (tier == 1) break; }
                }
                if (best > start) len = best - start;
                else
                {
                    // 窗口里全是「不能切」的位置（一长串英文/数字、连续小假名、连着的助词…）：
                    // 向后找第一个能切的地方。宁可这一段略超字数，也绝不从单词 / 数字中间断开。
                    int j = start + limit;
                    while (j < s.Length && CutTier(s, j) >= 99) j++;
                    if (j > start) len = j - start;
                }
                yield return s.Substring(start, len);
                start += len;
            }
        }

        /// <summary>
        /// 切点优先级：1 = 格助词后面（最自然）；2 = 汉字/片假名/数字开头（多半是词首）；
        /// 3 = 汉字接假名（可能是送り仮名，勉强能切）；99 = 不能切。
        /// </summary>
        private static int CutTier(string s, int i)
        {
            if (i <= 0 || i >= s.Length) return 99;
            char prev = s[i - 1], next = s[i];
            if (NeverStart.IndexOf(next) >= 0) return 99;                    // 小假名/长音符/标点/右引号不能当开头
            if (NeverEnd.IndexOf(prev) >= 0) return 99;                      // 左引号/左括号不能收尾（归属权保护）
            if (Particles.IndexOf(next) >= 0) return 99;                     // 别把助词和它前面的词拆开
            if (char.IsDigit(prev) && Units.IndexOf(next) >= 0) return 99;   // 数字和单位不拆
            if (IsWordChar(prev) && IsWordChar(next)) return 99;             // 英文单词 / 数字内部不拆（one → o|ne、12 → 1|2）
            if (prev == 'て' || prev == 'で') return 99;                     // 〜ている / 〜てしまう 之类，别切在て形后面
            if (CaseParticles.IndexOf(prev) >= 0) return 1;                  // 「…を|超えた」这种，切在格助词后
            if (IsKana(prev) && IsKana(next)) return 99;                     // 假名连着假名，多半在词中间（ところ 等）
            if (!IsKana(prev) && IsKana(next)) return 3;                     // 汉字 → 假名，可能是送り仮名
            return 2;                                                        // 假名 → 汉字/片假名/数字，多半是词首
        }

        /// <summary>
        /// 英文字母 / 阿拉伯数字（含全角）：硬切点不能落在它们中间，
        /// 否则 one 会被念成 o + ne、12 会被念成 1 + 2（TTS 会当成两个不相干的音节）。
        /// </summary>
        private static bool IsWordChar(char c)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) return true;
            // 全角字母数字（Ａ-Ｚ / ａ-ｚ / ０-９）同理
            if (c >= '\uFF21' && c <= '\uFF3A') return true;
            if (c >= '\uFF41' && c <= '\uFF5A') return true;
            if (c >= '\uFF10' && c <= '\uFF19') return true;
            return false;
        }

        /// <summary>是不是假名（平假名 / 片假名）。</summary>
        private static bool IsKana(char c)
        {
            return (c >= '\u3041' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF');
        }

        // ============================ 结果窗 ============================
        private void ShowResultCard()
        {
            UpdateIrodoriBtn();
            if (ResultCard.Visibility != Visibility.Visible)
            {
                ResultCard.Visibility = Visibility.Visible;
                ShowEl(ResultCard, 240);
                AnimScale(ResultScale, 1, 240);
                AnimY(ResultShift, 0, 240);
            }
            // 文本变了高度就变了：必须重新量一次再定位，否则结果卡会压住输入卡
            PositionAll();
        }

        private void SetResult(string text)
        {
            ResultText.Text = text;
            ResultScroll.ScrollToEnd();
        }

        // ============================ 翻译 ============================
        private async Task TranslateAsync()
        {
            if (_busy) return;
            string text = InputBox.Text;
            bool hasImage = _imageBytes != null;
            if (string.IsNullOrWhiteSpace(text) && !hasImage) { Toast("没有可翻译的内容"); return; }
            if (_cfg.source != "auto" && _cfg.source == _cfg.target)
            {
                Toast("源语言与目标语言相同");
                return;
            }

            _busy = true;
            TranslateBtn.IsEnabled = false;
            _cts = new CancellationTokenSource();
            _stream.Clear();
            // 开始翻下一句：上一句的语音文件随之作废
            StopIrodori();
            if (_edge != null) _edge.Stop();
            CleanupIrodoriAudio();
            SetResult("");
            ShowResultCard();
            ResultMeta.Text = "翻译中…";
            var sw = Stopwatch.StartNew();

            string src = _cfg.source, tgt = _cfg.target;
            string glossary = _cfg.glossary_on ? _cfg.glossary : "";
            try
            {
                Action<string> onDelta = delta =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        _stream.Append(delta);
                        ResultText.Text = _stream.ToString();
                        ResultScroll.ScrollToEnd();
                    }, DispatcherPriority.Background);
                };

                string final;
                int chunkCount = 1;
                bool imageMode = _imageBytes != null;
                App.Log("翻译请求: " + (imageMode ? "图片" : src + "→" + tgt) + " · 源 " + text.Length
                        + " 字 · 约束 " + Prompt.ConstraintSummary(_cfg.instruction, _cfg.glossary, _cfg.glossary_on, _cfg.genre));
                if (imageMode)
                {
                    // 图片翻译：整张图一次请求（图片没法切段），输入框里的文字当「参考文本/关注内容」
                    string prompt = Prompt.BuildImage(text, tgt, src, _cfg.instruction, glossary);
                    var bytes = _imageBytes;
                    string mime = _imageMime;
                    App.Log("图片翻译: 请求 " + _imageName + "（" + (bytes.Length / 1024) + " KB, " + mime + "）");
                    final = (await _client.TranslateImageAsync(prompt, bytes, mime, onDelta, _cts.Token)).Trim();
                }
                else
                {
                    var chunks = Prompt.SplitChunks(text, _cfg.chunk_limit);
                    chunkCount = chunks.Count;
                    var parts = new List<string>();
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        string prompt = Prompt.Build(chunks[i], tgt, src, _cfg.instruction, glossary, _cfg.genre);
                        parts.Add(await _client.TranslateAsync(prompt, onDelta, _cts.Token));
                    }
                    final = string.Join("", parts).Trim();
                }
                SetResult(final);
                sw.Stop();
                double sec = sw.Elapsed.TotalSeconds;
                string meta = sec.ToString("0.0", CultureInfo.InvariantCulture) + "s · " + AppConfig.LangName(tgt);
                if (imageMode) meta += " · 图片";
                else if (chunkCount > 1) meta += " · " + chunkCount + " 段";
                meta += " · " + final.Length + " 字";
                // 降级过就标出来，免得用户以为是模型变差了
                if (!string.IsNullOrEmpty(_client.LastModel) &&
                    !string.Equals(_client.LastModel, _cfg.model, StringComparison.OrdinalIgnoreCase))
                    meta += " · 降级 " + AppConfig.ModelShort(_client.LastModel);
                ResultMeta.Text = meta;

                App.History.Add(imageMode ? ("[图片] " + _imageName + (string.IsNullOrWhiteSpace(text) ? "" : " · " + text)) : text,
                                final, src == "auto" ? Prompt.Detect(text) : src, tgt);

                if (_cfg.auto_copy) SetClipboardText(final);
                if (_cfg.auto_speak) SpeakText(final, tgt, false);
            }
            catch (OperationCanceledException)
            {
                ResultMeta.Text = "已取消";
            }
            catch (Exception ex)
            {
                ResultMeta.Text = "";
                if (ResultCard.Visibility == Visibility.Visible && ResultText.Text.Length == 0)
                    HideEl(ResultCard, 140);
                Toast(ex.Message, true);
                App.Log("翻译失败: " + ex.Message);
            }
            finally
            {
                _busy = false;
                TranslateBtn.IsEnabled = true;
                PositionAll();
            }
        }

        // ============================ 热键 ============================
        // 呼出并翻译 / 划词翻译现在是同一个动作（都是「先取选中文本，取不到用剪贴板」），
        // 两颗热键只是习惯不同，任意一个留空就不注册那个键。
        private void SetupHotkeys()
        {
            if (!_cfg.hotkey_enabled) { Toast("全局热键已关闭"); return; }
            try
            {
                var src = (HwndSource)PresentationSource.FromVisual(this);
                if (src == null) return;
                _hotkeys = new HotkeyManager(src.Handle);
                _hotkeys.Pressed += HotkeyPressed;
                var used = new List<string>();
                string t = _hotkeys.Register(1, _cfg.hotkey_toggle, HotkeyManager.ToggleFallbacks, used);
                string s = _cfg.hotkey_select;
                bool dup = !string.IsNullOrWhiteSpace(s) && string.Equals(s.Trim(), t, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(s) && !dup)
                    s = _hotkeys.Register(2, s, HotkeyManager.SelectFallbacks, used);
                _cfg.hotkey_toggle = t;
                _cfg.hotkey_select = s;
                App.Log("热键注册: 呼出=" + (string.IsNullOrEmpty(t) ? "(未启用)" : t)
                        + " 划词=" + (string.IsNullOrEmpty(s) ? "(未启用)" : s)
                        + (dup ? "（与呼出相同，只注册一个）" : ""));
                if (!string.IsNullOrEmpty(_hotkeys.Warnings))
                    Toast(_hotkeys.Warnings, false);
            }
            catch (Exception ex) { App.Log("热键注册失败: " + ex.Message); }
        }

        private void HotkeyPressed(int id)
        {
            // 呼出并翻译 / 划词翻译合并成同一个动作（id 只是哪颗热键按下来的区别）
            _ = HotkeyTranslateAsync();
        }

        /// <summary>本窗口的 HWND（判断前台焦点是不是自己，用完缓存）。</summary>
        private IntPtr _selfHwnd = IntPtr.Zero;
        private IntPtr WindowHandle()
        {
            if (_selfHwnd == IntPtr.Zero)
            {
                try { _selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; }
                catch { }
            }
            return _selfHwnd;
        }

        /// <summary>
        /// 热键统一入口：①先取「选中文本」——必须在打开卡片之前，因为 OpenInput() 会 Activate() + 把焦点给输入框，
        /// 焦点一旦落到自己身上，模拟的 Ctrl+C 就抄不到别人选中的字（这就是「展开后提示没取到文本」的原因）；
        /// ②没选中就用剪贴板里的文字；③两样都没有才开卡片并提示；④有文本就填进输入框并翻译。
        /// </summary>
        private async Task HotkeyTranslateAsync()
        {
            IntPtr self = WindowHandle();
            bool selfForeground = self != IntPtr.Zero && SelectionGrab.ForegroundWindow() == self;
            bool wasOpen = _inputOpen;
            string sel = await GrabSelectionAsync(selfForeground);
            string src = "选中文本";
            if (string.IsNullOrWhiteSpace(sel))
            {
                sel = GetClipboardText();
                src = "剪贴板";
            }
            App.Log("热键取词: 选中文本=" + (string.IsNullOrWhiteSpace(sel) ? "(空)" : sel.Length + " 字")
                    + " 来源=" + src + " 取词时卡片已开=" + wasOpen + " 取词时焦点在自己身上=" + selfForeground);

            if (string.IsNullOrWhiteSpace(sel))
            {
                if (!_inputOpen) OpenInput();
                Toast(selfForeground
                    ? "没取到文本：焦点在悬浮翻译器上。请先在别的程序里选中文字（或 Ctrl+C 复制）再按快捷键"
                    : "没取到文本：先在别的程序里选中文字，或把内容复制到剪贴板");
                return;
            }
            if (!_inputOpen) OpenInput();
            _suppressAuto = true;
            InputBox.Text = sel;
            InputBox.CaretIndex = InputBox.Text.Length;
            _suppressAuto = false;
            Toast(src == "剪贴板" ? "没取到选中文本，改用剪贴板内容" : "已获取选中文本");
            await TranslateAsync();
        }

        /// <summary>
        /// 划词取词：保存剪贴板 → 发送 Ctrl+C → 读回选中文本 → 还原剪贴板。
        /// skipSend=true（焦点本来就在自己窗口上）时不发 Ctrl+C，直接返回空，交给剪贴板回退。
        /// </summary>
        private async Task<string> GrabSelectionAsync(bool skipSend = false)
        {
            string before = GetClipboardText();
            if (skipSend) return "";
            uint seq0 = SelectionGrab.ClipboardSequence();
            await Task.Delay(60);
            SelectionGrab.SendCtrlC();
            string sel = "";
            bool changed = false;
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(50);
                if (SelectionGrab.ClipboardSequence() != seq0) { sel = GetClipboardText(); changed = true; break; }
            }
            if (changed && !string.IsNullOrWhiteSpace(before)) SetClipboardText(before);
            return changed ? (sel ?? "") : "";
        }

        // ============================ 提示 / 剪贴板 ============================
        public void Toast(string msg, bool error = false)
        {
            ToastText.Text = msg;
            ToastText.Foreground = (Brush)FindResource(error ? "ErrBrush" : "TxtBrush");
            ShowEl(ToastBox, 200);
            ToastBox.UpdateLayout();
            PositionToast();
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(error ? 4200 : 2400);
            _toastTimer.Start();
        }

        /// <summary>立刻收起还停在「播放中」的提示条（播放结束后不该继续占着屏幕）。</summary>
        private void HideBusyToast()
        {
            if (ToastText.Text != null && ToastText.Text.StartsWith("Irodori-TTS 播放中"))
            {
                _toastTimer.Stop();
                HideEl(ToastBox, 150, 0);
            }
        }

        public static string GetClipboardText()
        {
            try
            {
                if (Clipboard.ContainsText()) return Clipboard.GetText();
            }
            catch { }
            return "";
        }

        public static void SetClipboardText(string s)
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    if (string.IsNullOrEmpty(s)) Clipboard.Clear();
                    else Clipboard.SetText(s);
                    return;
                }
                catch { Thread.Sleep(40); }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _cfg.orb_x = _orbX;
            _cfg.orb_y = _orbY;
            _cfg.Save();
            App.History.Save();
            if (_hotkeys != null) _hotkeys.Dispose();
            if (_speaker != null) _speaker.Stop();
            if (_edge != null) _edge.Dispose();
            StopIrodori();
            // 退出前把这次生成的语音文件删掉（顺带扫一遍以前遗留的）
            CleanupIrodoriAudio(true);
            if (_audio != null) _audio.Dispose();
            base.OnClosing(e);
        }
    }
}
