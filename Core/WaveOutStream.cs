using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FloatingTranslator
{
    /// <summary>
    /// 段与段之间没有空隙的 WAV 播放器（winmm waveOut，16 位 PCM 专用）。
    ///
    /// 为什么不用 WPF 的 MediaPlayer：它每开一个文件都要重新走一遍媒体管道。日志里实测
    /// 「第 N 段 开始播放」到「开始播放，时长 Xs」（MediaOpened）稳定要 0.4~0.7 秒，
    /// 媒体管道不健康时更久（旧代码注释里记过 3.2 秒音频从 Play() 到 Ended 花了 6.8 秒）。
    /// 朗读是「一段接一段」的几秒短音频，这点开播开销在听感上就是段与段之间的一段静音。
    ///
    /// 这里的做法：设备只打开一次，每段 wav 的 PCM 直接当成一个 buffer 追加到同一个设备上，
    /// 设备按采样点连续消费——段与段之间是真正的 0 间隙，而不是「放完再开下一个」。
    /// 数据没跟上（下一段还没合成出来）时设备自然静音，数据一到立刻接着播。
    ///
    /// 事件都在后台线程发出，调用方自己切回 UI 线程。
    /// </summary>
    internal sealed class WaveOutStream : IDisposable
    {
        private const uint WAVE_MAPPER = 0xFFFFFFFFu;
        private const int WHDR_DONE = 0x00000001;
        private const int WAVEHDR_SIZE = 48;      // x64 下 sizeof(WAVEHDR)
        private const int MAX_IN_FLIGHT = 3;      // 同时挂在设备上的 buffer 上限（够连续，也不囤太多）

        /// <summary>16 位 PCM 的 WAVEFORMATEX（waveOutOpen 就吃这个结构）。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct WavFormat
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;

            public bool SameAs(WavFormat o)
            {
                return wFormatTag == o.wFormatTag && nChannels == o.nChannels
                    && nSamplesPerSec == o.nSamplesPerSec && wBitsPerSample == o.wBitsPerSample;
            }

            public string Describe()
            {
                return nSamplesPerSec + "Hz/" + nChannels + "ch/" + wBitsPerSample + "bit";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WavFormat pwfx,
                                              IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")]
        private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);

        private sealed class Slot
        {
            public IntPtr Header;
            public GCHandle Pin;
            public int Bytes;
            public bool InFlight;
        }

        private readonly object _lock = new object();
        private readonly Queue<byte[]> _pending = new Queue<byte[]>();
        private readonly List<Slot> _slots = new List<Slot>();   // 挂在设备上的 buffer
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        private IntPtr _hwo = IntPtr.Zero;
        private WavFormat _fmt;
        private Thread _worker;
        private volatile bool _quit;
        private bool _eos;              // 调用方声明「没有更多了」
        private bool _drained;          // 本次已经报过播完
        private bool _starved;          // 正在「没数据可播、等下一段」
        private long _starvedSince;
        private long _bytesIn, _bytesOut;

        /// <summary>所有数据都播完了（只在 EndOfStream 之后发生，且只报一次）。</summary>
        public event Action Drained;
        /// <summary>没数据可播了：true 开始等下一段，false 恢复出声。参数是这次静止的毫秒数。</summary>
        public event Action<bool, long> Starved;
        /// <summary>播放过程中的异常（不致命，调用方记日志即可）。</summary>
        public event Action<string> Error;

        public WavFormat Format { get { return _fmt; } }
        public bool IsOpened { get { return _hwo != IntPtr.Zero; } }

        // ==================== WAV 读取 ====================

        /// <summary>
        /// 读出一个 wav 的 fmt 描述和 PCM 数据。只接受 16 位 PCM（waveOut 最稳的格式）。
        /// 失败返回 false 并给出可直接进日志的原因。
        /// </summary>
        public static bool TryReadPcm16(string path, out byte[] pcm, out WavFormat fmt, out string error)
        {
            pcm = null;
            fmt = default(WavFormat);
            error = "";
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (new string(br.ReadChars(4)) != "RIFF") { error = "不是 RIFF 文件"; return false; }
                    br.ReadInt32();
                    if (new string(br.ReadChars(4)) != "WAVE") { error = "不是 WAVE 文件"; return false; }

                    bool gotFmt = false;
                    byte[] data = null;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = new string(br.ReadChars(4));
                        int size = br.ReadInt32();
                        if (id == "fmt ")
                        {
                            fmt.wFormatTag = br.ReadInt16();
                            fmt.nChannels = br.ReadInt16();
                            fmt.nSamplesPerSec = br.ReadInt32();
                            fmt.nAvgBytesPerSec = br.ReadInt32();
                            fmt.nBlockAlign = br.ReadInt16();
                            fmt.wBitsPerSample = br.ReadInt16();
                            fmt.cbSize = 0;
                            if (size > 16) br.ReadBytes(size - 16);
                            else if (size < 16) br.ReadBytes(Math.Max(0, size));
                            gotFmt = true;
                        }
                        else if (id == "data")
                        {
                            long avail = fs.Length - fs.Position;
                            // 有些编码器把 data 长度写成 0 / -1（边写边读的流），按剩下全读
                            long n = (size <= 0 || size > avail) ? avail : size;
                            data = br.ReadBytes((int)n);
                        }
                        else
                        {
                            if (size < 0 || size > fs.Length - fs.Position) break;
                            br.ReadBytes(size);
                        }
                        // RIFF 块按偶数字节对齐
                        if ((size & 1) == 1 && fs.Position < fs.Length) br.ReadByte();
                    }

                    if (!gotFmt) { error = "缺少 fmt 块"; return false; }
                    if (data == null || data.Length == 0) { error = "缺少 data 块"; return false; }
                    if (fmt.wFormatTag != 1) { error = "不是 PCM（wFormatTag=" + fmt.wFormatTag + "）"; return false; }
                    if (fmt.wBitsPerSample != 16) { error = "不是 16 位（" + fmt.wBitsPerSample + " 位）"; return false; }
                    if (fmt.nChannels < 1 || fmt.nChannels > 2) { error = "声道数不支持（" + fmt.nChannels + "）"; return false; }
                    if (fmt.nSamplesPerSec < 8000 || fmt.nSamplesPerSec > 192000) { error = "采样率不支持（" + fmt.nSamplesPerSec + "）"; return false; }
                    if (fmt.nBlockAlign <= 0) fmt.nBlockAlign = (short)(fmt.nChannels * 2);
                    if (fmt.nAvgBytesPerSec <= 0) fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;

                    int usable = data.Length - data.Length % fmt.nBlockAlign;
                    if (usable <= 0) { error = "PCM 数据不足一帧"; return false; }
                    if (usable != data.Length) Array.Resize(ref data, usable);
                    pcm = data;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "读取 wav 失败：" + ex.Message;
                return false;
            }
        }

        // ==================== 播放 ====================

        public bool Start(out string error)
        {
            error = "";
            lock (_lock)
            {
                _quit = false;
                _eos = false;
                _drained = false;
                _starved = false;
                _pending.Clear();
            }
            if (_worker == null)
            {
                _worker = new Thread(Loop) { IsBackground = true, Name = "WaveOutStream" };
                _worker.Start();
            }
            return true;
        }

        /// <summary>追加一段 PCM。设备尚未打开时按这段的格式打开。</summary>
        public bool Append(byte[] pcm, WavFormat fmt, out string error)
        {
            error = "";
            if (pcm == null || pcm.Length == 0) { error = "没有可播的数据"; return false; }

            if (_hwo == IntPtr.Zero)
            {
                var f = fmt;
                int rc = waveOutOpen(out _hwo, WAVE_MAPPER, ref f, IntPtr.Zero, IntPtr.Zero, 0 /*CALLBACK_NULL*/);
                if (rc != 0 || _hwo == IntPtr.Zero)
                {
                    _hwo = IntPtr.Zero;
                    error = "waveOutOpen 失败 rc=" + rc;
                    return false;
                }
                _fmt = fmt;
                ApplyVolume();
                App.Log("连续播放：已打开音频设备 " + fmt.Describe() + "（t=" + _sw.ElapsedMilliseconds + "ms）");
            }
            else if (!_fmt.SameAs(fmt))
            {
                error = "本段格式（" + fmt.Describe() + "）与上一段（" + _fmt.Describe() + "）不同";
                return false;
            }

            lock (_lock)
            {
                if (_drained) { _drained = false; _eos = false; }
                _pending.Enqueue(pcm);
                _bytesIn += pcm.Length;
            }
            return true;
        }

        /// <summary>声明不会再有数据了：等缓冲区播空就报 Drained。</summary>
        public void EndOfStream()
        {
            lock (_lock) _eos = true;
        }

        public void SetVolume(double v)
        {
            _volume = v < 0 ? 0 : (v > 1 ? 1 : v);
            ApplyVolume();
        }
        private double _volume = 1.0;

        private void ApplyVolume()
        {
            if (_hwo == IntPtr.Zero) return;
            try
            {
                uint v = (uint)(_volume * 0xFFFF);
                waveOutSetVolume(_hwo, (v << 16) | v);      // 左右声道同一个值
            }
            catch (Exception) { }
        }

        private bool HasDataLocked()
        {
            return _pending.Count > 0;
        }

        private void Loop()
        {
            while (!_quit)
            {
                // 1) 回收已经播完的 buffer
                lock (_lock)
                {
                    for (int i = _slots.Count - 1; i >= 0; i--)
                    {
                        var s = _slots[i];
                        if (!s.InFlight) continue;
                        if ((Marshal.ReadInt32(s.Header, 24) & WHDR_DONE) == 0) continue;
                        if (_hwo != IntPtr.Zero) waveOutUnprepareHeader(_hwo, s.Header, WAVEHDR_SIZE);
                        Release(s);
                        _slots.RemoveAt(i);
                    }
                }

                // 2) 把排队的数据写进设备（一个追加块就是一个 buffer，顺序与原样一致）
                WritePending();

                // 3) 判断「播空了」还是「在等下一段」
                bool idle, eos, hasData;
                lock (_lock)
                {
                    idle = _slots.Count == 0;
                    eos = _eos;
                    hasData = HasDataLocked();
                }
                if (idle && !hasData)
                {
                    if (eos) MarkDrained();
                    else MarkStarved(true);
                }
                else MarkStarved(false);

                Thread.Sleep(4);
            }
        }

        private int _writeFails;

        private void WritePending()
        {
            while (!_quit)
            {
                byte[] chunk = null;
                lock (_lock)
                {
                    if (_pending.Count == 0) return;
                    if (_slots.Count >= MAX_IN_FLIGHT) return;
                    chunk = _pending.Dequeue();
                }
                if (chunk == null || chunk.Length == 0) continue;
                if (!WriteChunk(chunk))
                {
                    // 写失败就放回队列下一圈再试；连续失败太多次说明设备已经不可用，
                    // 丢掉这段（继续放回去只会每 4ms 重试一次、永远卡住）
                    _writeFails++;
                    if (_writeFails >= 20)
                    {
                        _writeFails = 0;
                        RaiseError("waveOut 连续写入失败，丢弃 " + chunk.Length + " 字节（设备可能被独占或已失效）");
                        continue;
                    }
                    lock (_lock) _pending.Enqueue(chunk);
                    return;
                }
                _writeFails = 0;
            }
        }

        /// <summary>报一个不致命的错误：自己记日志，同时通知订阅者（两类都记，方便查设备问题）。</summary>
        private void RaiseError(string msg)
        {
            App.Log("连续播放：" + msg);
            var h = Error;
            if (h != null) { try { h(msg); } catch (Exception) { } }
        }

        private bool WriteChunk(byte[] chunk)
        {
            var s = new Slot { Bytes = chunk.Length };
            IntPtr header = IntPtr.Zero;
            try
            {
                s.Pin = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                header = Marshal.AllocHGlobal(WAVEHDR_SIZE);
                for (int i = 0; i < WAVEHDR_SIZE; i += 4) Marshal.WriteInt32(header, i, 0);
                Marshal.WriteIntPtr(header, 0, s.Pin.AddrOfPinnedObject());
                Marshal.WriteInt32(header, 8, s.Bytes);

                int rc = waveOutPrepareHeader(_hwo, header, WAVEHDR_SIZE);
                if (rc == 0) rc = waveOutWrite(_hwo, header, WAVEHDR_SIZE);
                if (rc != 0)
                {
                    RaiseError("waveOut 写入失败 rc=" + rc + "（" + _fmt.Describe() + "，" + s.Bytes + " 字节）");
                    if (s.Pin.IsAllocated) s.Pin.Free();
                    Marshal.FreeHGlobal(header);
                    return false;
                }

                s.Header = header;
                s.InFlight = true;
                lock (_lock)
                {
                    _slots.Add(s);
                    _bytesOut += chunk.Length;
                }
                return true;
            }
            catch (Exception ex)
            {
                RaiseError("写入异常 " + ex.Message);
                try { if (s.Pin.IsAllocated) s.Pin.Free(); } catch (Exception) { }
                try { if (header != IntPtr.Zero) Marshal.FreeHGlobal(header); } catch (Exception) { }
                return false;
            }
        }

        private static void Release(Slot s)
        {
            try { if (s.Pin.IsAllocated) s.Pin.Free(); } catch (Exception) { }
            try { if (s.Header != IntPtr.Zero) Marshal.FreeHGlobal(s.Header); } catch (Exception) { }
            s.InFlight = false;
        }

        private void MarkDrained()
        {
            lock (_lock)
            {
                if (_drained) return;
                _drained = true;
                _eos = false;
            }
            App.Log("连续播放：全部播完（送入 " + (_bytesIn / 1024) + " KB）");
            var h = Drained;
            if (h != null) { try { h(); } catch (Exception) { } }
        }

        private void MarkStarved(bool on)
        {
            bool changed = false;
            long idle = 0;
            lock (_lock)
            {
                if (on && !_starved) { _starved = true; _starvedSince = _sw.ElapsedMilliseconds; changed = true; }
                else if (!on && _starved) { _starved = false; idle = _sw.ElapsedMilliseconds - _starvedSince; changed = true; }
            }
            if (!changed) return;
            if (on) App.Log("连续播放：暂时没有数据，等下一段合成…（t=" + _sw.ElapsedMilliseconds + "ms）");
            else App.Log("连续播放：数据接上了，静音 " + idle + "ms（t=" + _sw.ElapsedMilliseconds + "ms）");
            var h = Starved;
            if (h != null) { try { h(on, idle); } catch (Exception) { } }
        }

        public void Stop()
        {
            _quit = true;
            var t = _worker;
            _worker = null;
            if (t != null && t.IsAlive)
            {
                try { t.Join(500); } catch (Exception) { }
            }
            lock (_lock)
            {
                _pending.Clear();
                if (_hwo != IntPtr.Zero)
                {
                    try { waveOutReset(_hwo); } catch (Exception) { }
                    foreach (var s in _slots)
                    {
                        try { waveOutUnprepareHeader(_hwo, s.Header, WAVEHDR_SIZE); } catch (Exception) { }
                        Release(s);
                    }
                    _slots.Clear();
                    try { waveOutClose(_hwo); } catch (Exception) { }
                    _hwo = IntPtr.Zero;
                }
                _eos = false;
                _drained = false;
                _starved = false;
            }
        }

        public void Dispose() { Stop(); }
    }
}
