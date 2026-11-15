using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace FloatingTranslator
{
    /// <summary>
    /// 麦克风录音（winmm waveIn，16 kHz / 单声道 / 16 bit PCM）。
    /// 不引第三方库：ASR 模型固定吃 16k 单声道，后端自己重采样，所以这里只负责采集。
    /// </summary>
    public sealed class MicRecorder : IDisposable
    {
        public const int SampleRate = 16000;
        private const int BufferSamples = 1600;          // 0.1 秒一块
        private const int BufferCount = 8;

        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_FUNCTION = 0x00030000;
        private const int WIM_DATA = 0x3C0;
        private const int WAVE_FORMAT_PCM = 1;
        private const int WAVERR_STILLPLAYING = 33;
        private const int MMSYSERR_NOERROR = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll", SetLastError = false)]
        private static extern int waveInOpen(out IntPtr hwi, uint uDeviceID, ref WAVEFORMATEX pwfx,
                                             IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);
        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private readonly object _gate = new object();
        private readonly List<byte> _pcm = new List<byte>(SampleRate * 2 * 20);
        private readonly List<IntPtr> _headers = new List<IntPtr>();
        private readonly List<IntPtr> _buffers = new List<IntPtr>();
        private readonly WaveInProc _proc;      // 必须保住委托，否则 GC 掉后回调直接崩
        private IntPtr _hwi = IntPtr.Zero;
        private volatile bool _recording;
        private DateTime _startedAt;

        public MicRecorder()
        {
            _proc = OnData;
        }

        public bool IsRecording { get { return _recording; } }

        public TimeSpan Elapsed
        {
            get { return _recording ? DateTime.Now - _startedAt : TimeSpan.Zero; }
        }

        /// <summary>已经录到的采样数（只读，用于界面计时）。</summary>
        public int SampleCount
        {
            get { lock (_gate) return _pcm.Count / 2; }
        }

        public void Start()
        {
            if (_recording) return;
            lock (_gate) _pcm.Clear();

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
                cbSize = 0,
            };

            int hr = waveInOpen(out _hwi, WAVE_MAPPER, ref fmt,
                                Marshal.GetFunctionPointerForDelegate(_proc), IntPtr.Zero, CALLBACK_FUNCTION);
            if (hr != MMSYSERR_NOERROR)
            {
                _hwi = IntPtr.Zero;
                throw new InvalidOperationException("打不开麦克风（" + hr + "：" + ErrorText(hr)
                    + "）。请检查系统「隐私和安全性 → 麦克风」是否允许桌面应用访问。");
            }

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    IntPtr data = Marshal.AllocHGlobal(BufferSamples * 2);
                    _buffers.Add(data);
                    var hdr = new WAVEHDR
                    {
                        lpData = data,
                        dwBufferLength = (uint)(BufferSamples * 2),
                    };
                    IntPtr hp = Marshal.AllocHGlobal(headerSize);
                    Marshal.StructureToPtr(hdr, hp, false);
                    _headers.Add(hp);
                    hr = waveInPrepareHeader(_hwi, hp, (uint)headerSize);
                    if (hr != MMSYSERR_NOERROR) throw new InvalidOperationException("准备录音缓冲失败：" + ErrorText(hr));
                    hr = waveInAddBuffer(_hwi, hp, (uint)headerSize);
                    if (hr != MMSYSERR_NOERROR) throw new InvalidOperationException("添加录音缓冲失败：" + ErrorText(hr));
                }

                _recording = true;
                _startedAt = DateTime.Now;
                hr = waveInStart(_hwi);
                if (hr != MMSYSERR_NOERROR) throw new InvalidOperationException("启动录音失败：" + ErrorText(hr));
                App.Log("ASR：开始录音");
            }
            catch
            {
                _recording = false;
                CloseDevice();
                throw;
            }
        }

        /// <summary>停止录音并返回 16 kHz 单声道 float 采样（-1..1）。</summary>
        public float[] Stop()
        {
            _recording = false;
            if (_hwi != IntPtr.Zero)
            {
                try { waveInStop(_hwi); } catch { }
                try { waveInReset(_hwi); } catch { }
                // waveInReset 之后回调是异步回来的，等它把最后几块交回来再释放内存
                Thread.Sleep(80);
                CloseDevice();
            }

            byte[] bytes;
            lock (_gate)
            {
                bytes = _pcm.ToArray();
                _pcm.Clear();
            }
            int n = bytes.Length / 2;
            var samples = new float[n];
            for (int i = 0; i < n; i++)
            {
                short s = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = s / 32768f;
            }
            App.Log("ASR：停止录音，采样数=" + n);
            return samples;
        }

        private void CloseDevice()
        {
            if (_hwi == IntPtr.Zero) return;
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            // 还在播放的缓冲要等它回来才能 unprepare；最多等 1 秒，晚了就直接关设备
            for (int pass = 0; pass < 100; pass++)
            {
                bool busy = false;
                foreach (var hp in _headers)
                {
                    int r = waveInUnprepareHeader(_hwi, hp, (uint)headerSize);
                    if (r == WAVERR_STILLPLAYING) busy = true;
                }
                if (!busy) break;
                Thread.Sleep(10);
            }
            try { waveInClose(_hwi); } catch { }
            _hwi = IntPtr.Zero;

            foreach (var hp in _headers) Marshal.FreeHGlobal(hp);
            _headers.Clear();
            foreach (var bp in _buffers) Marshal.FreeHGlobal(bp);
            _buffers.Clear();
        }

        private void OnData(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (msg != WIM_DATA) return;
            try
            {
                var hdr = (WAVEHDR)Marshal.PtrToStructure(param1, typeof(WAVEHDR));
                if (hdr.dwBytesRecorded > 0 && hdr.lpData != IntPtr.Zero)
                {
                    var chunk = new byte[hdr.dwBytesRecorded];
                    Marshal.Copy(hdr.lpData, chunk, 0, chunk.Length);
                    lock (_gate) _pcm.AddRange(chunk);
                }
                if (_recording && _hwi != IntPtr.Zero)
                    waveInAddBuffer(hwi, param1, (uint)Marshal.SizeOf(typeof(WAVEHDR)));
            }
            catch (Exception ex)
            {
                App.Log("ASR：录音回调失败: " + ex.Message);
            }
        }

        private static string ErrorText(int code)
        {
            switch (code)
            {
                case 2: return "设备 ID 无效";
                case 4: return "设备已被占用";
                case 5: return "句柄无效";
                case 6: return "没有音频驱动";
                case 7: return "内存不足";
                case 8: return "不支持该操作";
                case 11: return "参数无效";
                case 12: return "设备忙";
                case 32: return "不支持的格式";
                case 33: return "缓冲还在播放";
                default: return "错误码 " + code;
            }
        }

        public void Dispose()
        {
            if (_recording) Stop();
            else if (_hwi != IntPtr.Zero) CloseDevice();
        }
    }
}
