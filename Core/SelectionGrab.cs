using System;
using System.Runtime.InteropServices;

namespace FloatingTranslator
{
    /// <summary>取词：模拟 Ctrl+C 并把选中的文本从剪贴板取回来</summary>
    public static class SelectionGrab
    {
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private const byte VK_CONTROL = 0x11;
        private const byte VK_C = 0x43;
        private const byte VK_MENU = 0x12;      // Alt
        private const byte VK_SHIFT = 0x10;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static uint ClipboardSequence()
        {
            try { return GetClipboardSequenceNumber(); }
            catch { return 0; }
        }

        /// <summary>当前前台窗口。取词前用它判断焦点是不是已经在自己身上（在自己身上就不该发 Ctrl+C）。</summary>
        public static IntPtr ForegroundWindow()
        {
            try { return GetForegroundWindow(); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>发送一次 Ctrl+C（先松开修饰键，避免 Alt/Ctrl 卡住）</summary>
        public static void SendCtrlC()
        {
            try
            {
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_C, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);
                keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex) { App.Log("模拟 Ctrl+C 失败: " + ex.Message); }
        }
    }
}
