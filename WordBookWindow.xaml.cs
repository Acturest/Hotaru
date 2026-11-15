using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatingTranslator
{
    /// <summary>生词本窗口：搜索 / 选中 / 朗读 / 删除 / 导出 CSV / 清空。</summary>
    public partial class WordBookWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<Border> _rows = new List<Border>();
        private WordItem _selected;

        private static readonly FontFamily JpFont = new FontFamily("Yu Gothic UI, Meiryo, Microsoft YaHei UI");

        public WordBookWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();

            Header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };
            CloseBtn.Click += (s, e) => Close();
            SpeakBtn.Click += (s, e) => SpeakSelected();
            DelBtn.Click += (s, e) => DeleteSelected();
            ExportBtn.Click += (s, e) => ExportCsv();
            ClearBtn.Click += (s, e) => ClearAll();
            SearchBox.TextChanged += (s, e) => Refresh();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { SpeakSelected(); e.Handled = true; }
                else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
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
            List<WordItem> all = App.Words != null ? App.Words.Items : new List<WordItem>();
            List<WordItem> shown = string.IsNullOrEmpty(q)
                ? all
                : all.Where(w => Contains(w.term, q) || Contains(w.reading, q)
                              || Contains(w.brief, q) || Contains(w.sentence, q)).ToList();

            foreach (WordItem w in shown) ListHost.Children.Add(MakeRow(w));

            EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = all.Count == 0 ? "还没有收藏的词（查词浮层上点 ☆）" : "没有匹配的词";
            CountText.Text = string.IsNullOrEmpty(q) ? (all.Count + " 个") : (shown.Count + " / " + all.Count + " 个");
        }

        private static bool Contains(string s, string q)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Border MakeRow(WordItem w)
        {
            var panel = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = w.term,
                FontFamily = JpFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TxtBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrWhiteSpace(w.reading))
            {
                head.Children.Add(new TextBlock
                {
                    Text = "［" + w.reading + "］",
                    FontFamily = JpFont,
                    FontSize = 11,
                    Margin = new Thickness(7, 0, 0, 0),
                    Foreground = (Brush)FindResource("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (!string.IsNullOrWhiteSpace(w.pos))
            {
                head.Children.Add(new TextBlock
                {
                    Text = w.pos,
                    FontSize = 10,
                    Margin = new Thickness(7, 0, 0, 0),
                    Foreground = (Brush)FindResource("DimBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150,
                });
            }
            panel.Children.Add(head);

            if (!string.IsNullOrWhiteSpace(w.brief))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = OneLine(w.brief, 70),
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            if (!string.IsNullOrWhiteSpace(w.sentence))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = OneLine(w.sentence, 60),
                    FontFamily = JpFont,
                    FontSize = 10.5,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = (Brush)FindResource("DimBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
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
                Tag = w,
                ToolTip = string.IsNullOrWhiteSpace(w.sentence) ? w.brief : w.brief + "\n\n" + w.sentence,
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (s, e) => { if (_selected != w) row.Background = (Brush)FindResource("BtnHoverBrush"); };
            row.MouseLeave += (s, e) => { if (_selected != w) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount >= 2) { _selected = w; MarkSelection(); SpeakSelected(); }
                else { _selected = w; MarkSelection(); }
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

        private static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        // ---------------------------------------------------------------- 操作
        private void SpeakSelected()
        {
            if (_selected == null) { _owner.Toast("请先选一个词"); return; }
            _owner.SpeakWord(_selected.term);
        }

        private void DeleteSelected()
        {
            if (_selected == null) { _owner.Toast("请先选一个词"); return; }
            App.Words.Remove(_selected);
            Refresh();
        }

        private void ClearAll()
        {
            if (App.Words == null || App.Words.Items.Count == 0) return;
            if (MessageBox.Show(this, "确定要清空整个生词本吗？", "生词本",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            App.Words.Clear();
            Refresh();
        }

        private void ExportCsv()
        {
            if (App.Words == null || App.Words.Items.Count == 0) { _owner.Toast("生词本是空的"); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出生词本",
                FileName = "生词本_" + DateTime.Now.ToString("yyyyMMdd") + ".csv",
                Filter = "CSV 文件|*.csv|所有文件|*.*",
                DefaultExt = ".csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                // 带 BOM，Excel 打开中文 / 日文才不会乱码
                File.WriteAllText(dlg.FileName, App.Words.ExportCsv(), new UTF8Encoding(true));
                _owner.Toast("已导出 " + App.Words.Items.Count + " 个词 → " + Path.GetFileName(dlg.FileName));
                App.Log("生词本：导出 " + App.Words.Items.Count + " 个 → " + dlg.FileName);
            }
            catch (Exception ex)
            {
                _owner.Toast("导出失败：" + ex.Message, true);
                App.Log("生词本导出失败: " + ex);
            }
        }

        // ---------------------------------------------------------------- 定位
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
