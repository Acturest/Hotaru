using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FloatingTranslator
{
    /// <summary>全局热键：RegisterHotKey + 窗口消息钩子（WM_HOTKEY）</summary>
    public class HotkeyManager : IDisposable
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        private const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static readonly string[] ToggleFallbacks =
            { "Ctrl+Alt+Q", "Ctrl+Alt+S", "Ctrl+Alt+G", "Ctrl+Shift+Q", "Alt+Q", "Ctrl+Alt+B" };
        public static readonly string[] SelectFallbacks =
            { "Ctrl+Alt+W", "Ctrl+Alt+E", "Ctrl+Alt+V", "Ctrl+Shift+W", "Alt+W", "Ctrl+Alt+N" };

        private readonly IntPtr _hwnd;
        private readonly HwndSource _src;
        private readonly List<int> _ids = new List<int>();
        private readonly List<string> _used = new List<string>();

        public event Action<int> Pressed;
        public string Warnings = "";

        public HotkeyManager(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _src = HwndSource.FromHwnd(hwnd);
            if (_src != null) _src.AddHook(WndProc);
        }

        /// <summary>
        /// 依次尝试候选热键，返回实际生效的组合。
        /// preferred 留空 = 这个键不要了（直接返回 ""，不再去试 fallbacks，这样才能只留一个热键）。
        /// </summary>
        public string Register(int id, string preferred, string[] fallbacks, List<string> ignore)
        {
            if (string.IsNullOrWhiteSpace(preferred)) return "";
            var cands = new List<string>();
            cands.Add(preferred.Trim());
            if (fallbacks != null)
                foreach (var f in fallbacks)
                    if (!cands.Contains(f)) cands.Add(f);

            foreach (var cand in cands)
            {
                if (_used.Contains(cand)) continue;
                uint mods, vk;
                if (!Parse(cand, out mods, out vk)) continue;
                if (RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
                {
                    _ids.Add(id);
                    _used.Add(cand);
                    if (!string.Equals(cand, preferred, StringComparison.OrdinalIgnoreCase))
                        Warnings = "热键 " + preferred + " 被占用，已改用 " + cand;
                    return cand;
                }
            }
            Warnings = "热键注册失败（可能被其它软件占用）：" + preferred;
            return preferred;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                handled = true;
                var h = Pressed;
                if (h != null)
                {
                    int id = wParam.ToInt32();
                    try { h(id); } catch (Exception ex) { App.Log("热键处理异常: " + ex.Message); }
                }
            }
            return IntPtr.Zero;
        }

        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string up = text.ToUpperInvariant().Replace(" ", "");
            foreach (var part in up.Split('+'))
            {
                if (part.Length == 0) continue;
                switch (part)
                {
                    case "CTRL": case "CONTROL": mods |= MOD_CONTROL; break;
                    case "ALT": mods |= MOD_ALT; break;
                    case "SHIFT": mods |= MOD_SHIFT; break;
                    case "WIN": case "WINDOWS": mods |= MOD_WIN; break;
                    default:
                        {
                            string k = part;
                            if (k.StartsWith("F") && k.Length >= 2 && k.Length <= 3)
                            {
                                int n;
                                if (int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24)
                                {
                                    vk = (uint)(0x70 + n - 1);
                                    break;
                                }
                            }
                            if (k.Length == 1)
                            {
                                char c = k[0];
                                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = (uint)c; break; }
                            }
                            return false;
                        }
                }
            }
            if (mods == 0) return false;
            return vk != 0;
        }

        public void Dispose()
        {
            foreach (var id in _ids)
            {
                try { UnregisterHotKey(_hwnd, id); } catch { }
            }
            _ids.Clear();
            if (_src != null) _src.RemoveHook(WndProc);
        }
    }
}
