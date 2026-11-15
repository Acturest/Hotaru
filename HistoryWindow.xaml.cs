using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatingTranslator
{
    /// <summary>历史记录窗口：搜索 / 选中 / 使用 / 删除 / 清空</summary>
    public partial class HistoryWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<Border> _rows = new List<Border>();
        private HistoryItem _selected;

        public HistoryWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();

            Header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };
            CloseBtn.Click += (s, e) => Close();
            UseBtn.Click += (s, e) => UseSelected();
            DelBtn.Click += (s, e) => DeleteSelected();
            ClearBtn.Click += (s, e) => ClearAll();
            SearchBox.TextChanged += (s, e) => Refresh();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { UseSelected(); e.Handled = true; }
            };

            Refresh();
        }

        // ---------------------------------------------------------------- 数据
        public void Refresh()
        {
            ListHost.Children.Clear();
            _rows.Clear();
            _selected = null;

            string q = (SearchBox.Text ?? "").Trim();
            List<HistoryItem> all = App.History.Items ?? new List<HistoryItem>();
            List<HistoryItem> shown = string.IsNullOrEmpty(q)
                ? all
                : all.Where(h => Contains(h.input, q) || Contains(h.output, q)).ToList();

            foreach (HistoryItem h in shown) ListHost.Children.Add(MakeRow(h));

            EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = all.Count == 0 ? "暂无记录" : "没有匹配的记录";
            CountText.Text = string.IsNullOrEmpty(q) ? (all.Count + " 条") : (shown.Count + " / " + all.Count + " 条");
        }

        private static bool Contains(string s, string q)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Border MakeRow(HistoryItem h)
        {
            var panel = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = Short(h.t),
                FontSize = 10.5,
                Foreground = (Brush)FindResource("DimBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(new TextBlock
            {
                Text = LangPair(h),
                FontSize = 10.5,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock
            {
                Text = OneLine(h.input),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource("TxtBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(h.output))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = OneLine(h.output),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var row = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Child = panel,
                Tag = h,
                ToolTip = h.output,
                Cursor = Cursors.Hand
            };
            row.MouseEnter += (s, e) => { if (_selected != h) row.Background = (Brush)FindResource("BtnHoverBrush"); };
            row.MouseLeave += (s, e) => { if (_selected != h) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount >= 2) { _selected = h; MarkSelection(); UseSelected(); }
                else { _selected = h; MarkSelection(); }
            };
            _rows.Add(row);
            return row;
        }

        private void MarkSelection()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Border r = _rows[i];
                bool on = ReferenceEquals(r.Tag, _selected);
                r.Background = on ? (Brush)FindResource("FieldHoverBrush") : Brushes.Transparent;
                r.BorderBrush = on ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
            }
        }

        private static string Short(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            return t.Length >= 16 ? t.Substring(5, 11) : t;
        }

        private static string LangPair(HistoryItem h)
        {
            string s = string.IsNullOrEmpty(h.s) ? "auto" : h.s;
            string t = string.IsNullOrEmpty(h.g) ? "zh" : h.g;
            return AppConfig.LangName(s) + " → " + AppConfig.LangName(t);
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        // ---------------------------------------------------------------- 操作
        private void UseSelected()
        {
            if (_selected == null) { _owner.Toast("请先选择一条记录"); return; }
            _owner.UseHistory(_selected);
            Close();
        }

        private void DeleteSelected()
        {
            if (_selected == null) { _owner.Toast("请先选择一条记录"); return; }
            App.History.Remove(_selected);
            App.History.Save();
            Refresh();
        }

        private void ClearAll()
        {
            if (App.History.Items == null || App.History.Items.Count == 0) return;
            if (MessageBox.Show(this, "确定要清空全部历史记录吗？", "历史记录",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            App.History.Clear();
            App.History.Save();
            Refresh();
        }

        // ---------------------------------------------------------------- 定位
        /// <summary>摆到圆钮左边（放不下换右边），底边与圆钮底边对齐</summary>
        public void PlaceNear(MainWindow w, double orbX, double orbY)
        {
            Rect wa = SystemParameters.WorkArea;
            UpdateLayout();
            double h = ActualHeight > 0 ? ActualHeight : Height;
            double wd = ActualWidth > 0 ? ActualWidth : Width;

            double x = orbX - wd - 14;
            if (x < wa.Left + 8) x = orbX + 64 + 14;
            if (x + wd > wa.Right - 8) x = wa.Right - wd - 8;
            if (x < wa.Left + 8) x = wa.Left + 8;

            double y = orbY + 64 - h;
            if (y < wa.Top + 8) y = wa.Top + 8;
            if (y + h > wa.Bottom - 8) y = wa.Bottom - h - 8;

            Left = x;
            Top = y;
        }
    }
}
