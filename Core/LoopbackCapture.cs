using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FloatingTranslator
{
    /// <summary>
    /// 用 WASAPI loopback 捕获「电脑正在播放的声音」（默认播放设备）。
    /// 输出单声道 float 采样，采样率跟随设备混音格式（通常是 48 kHz）。
    /// 不需要麦克风权限，也不受麦克风静音影响。
    /// </summary>
    public sealed class LoopbackCapture : IDisposable
    {
        public event Action<float[], int> Data;
        public event Action<string> Failed;

        public bool IsCapturing { get; private set; }
        public int SampleRate { get; private set; }

        private Thread _thread;
        private volatile bool _stop;
        private ManualResetEventSlim _ready;
        private Exception _startError;
        private int _channels;
        private int _bits;
        private bool _isFloat;
        private bool _isPcm;
        private IntPtr _eventHandle;

        private static readonly Guid EnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        private static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00AA00389B71");

        public void Start()
        {
            if (IsCapturing) return;
            _stop = false;
            _startError = null;
            try { if (_ready != null) _ready.Dispose(); } catch { }
            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(CaptureThread) { IsBackground = true, Name = "LoopbackCapture" };
            _thread.Start();
            if (!_ready.Wait(4000))
            {
                _stop = true;
                try { Stop(); } catch { }
                throw new InvalidOperationException("启动系统声音捕获超时，请确认正在使用默认播放设备。");
            }
            if (_startError != null)
            {
                try { Stop(); } catch { }
                throw new InvalidOperationException("无法捕获系统播放的音频：" + _startError.Message, _startError);
            }
            IsCapturing = true;
        }

        public void Stop()
        {
            _stop = true;
            Thread t = _thread;
            if (t != null && t.IsAlive) t.Join(1500);
            _thread = null;
            IsCapturing = false;
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { if (_ready != null) _ready.Dispose(); } catch { }
            _ready = null;
        }

        private void CaptureThread()
        {
            CoInitializeEx(IntPtr.Zero, 0);
            IntPtr pFormat = IntPtr.Zero;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IMMDevice device = null;
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(EnumeratorClsid));
                int hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device);
                if (hr != 0) throw new COMException("找不到默认播放设备", hr);

                Guid iidClient = typeof(IAudioClient).GUID;
                object obj;
                hr = device.Activate(ref iidClient, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out obj);
                if (hr != 0) throw new COMException("无法打开默认播放设备", hr);
                client = (IAudioClient)obj;

                hr = client.GetMixFormat(out pFormat);
                if (hr != 0) throw new COMException("无法读取播放设备的音频格式", hr);
                ParseFormat(pFormat);

                _eventHandle = CreateEvent(IntPtr.Zero, false, false, null);
                if (_eventHandle == IntPtr.Zero) throw new InvalidOperationException("创建音频事件失败。");

                // shared mode + loopback + event callback，200ms 缓冲
                hr = client.Initialize(0, 0x00060000, 2000000, 0, pFormat, IntPtr.Zero);
                if (hr != 0) throw new COMException("初始化系统声音捕获失败", hr);
                hr = client.SetEventHandle(_eventHandle);
                if (hr != 0) throw new COMException("设置音频事件失败", hr);

                Guid iidCapture = typeof(IAudioCaptureClient).GUID;
                IntPtr pCapture;
                hr = client.GetService(ref iidCapture, out pCapture);
                if (hr != 0) throw new COMException("获取音频捕获接口失败", hr);
                capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(pCapture);
                Marshal.Release(pCapture);

                hr = client.Start();
                if (hr != 0) throw new COMException("启动系统声音捕获失败", hr);

                _ready.Set();
                while (!_stop)
                {
                    WaitForSingleObject(_eventHandle, 100);
                    Drain(capture);
                }
                try { client.Stop(); } catch { }
            }
            catch (Exception ex)
            {
                _startError = ex;
                try { _ready.Set(); } catch { }
                if (IsCapturing)
                {
                    try { Failed?.Invoke(ex.Message); } catch { }
                }
            }
            finally
            {
                if (capture != null) { try { Marshal.ReleaseComObject(capture); } catch { } }
                if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } }
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
                if (pFormat != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(pFormat); } catch { } }
                if (_eventHandle != IntPtr.Zero) { try { CloseHandle(_eventHandle); } catch { } _eventHandle = IntPtr.Zero; }
                CoUninitialize();
                IsCapturing = false;
            }
        }

        private void Drain(IAudioCaptureClient capture)
        {
            while (!_stop)
            {
                int hr = capture.GetNextPacketSize(out int packet);
                if (hr != 0 || packet <= 0) return;

                hr = capture.GetBuffer(out IntPtr data, out int frames, out int flags,
                    out long devicePosition, out long qpcPosition);
                if (hr != 0) return;
                try
                {
                    if (frames > 0 && data != IntPtr.Zero)
                    {
                        bool silent = (flags & 0x2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                        float[] mono = silent ? new float[frames] : ConvertFrames(data, frames);
                        if (mono.Length > 0)
                        {
                            try { Data?.Invoke(mono, SampleRate); } catch { }
                        }
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }

        private float[] ConvertFrames(IntPtr data, int frames)
        {
            int ch = Math.Max(1, _channels);
            int count = frames * ch;
            float[] mono = new float[frames];

            if (_isFloat && _bits == 32)
            {
                float[] buf = new float[count];
                Marshal.Copy(data, buf, 0, count);
                for (int i = 0; i < frames; i++)
                {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                    mono[i] = s / ch;
                }
                return mono;
            }

            if (_isPcm && _bits == 16)
            {
                short[] buf = new short[count];
                Marshal.Copy(data, buf, 0, count);
                for (int i = 0; i < frames; i++)
                {
                    int s = 0;
                    for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                    mono[i] = s / (float)ch / 32768f;
                }
                return mono;
            }

            if (_isPcm && _bits == 32)
            {
                int[] buf = new int[count];
                Marshal.Copy(data, buf, 0, count);
                for (int i = 0; i < frames; i++)
                {
                    double s = 0;
                    for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                    mono[i] = (float)(s / ch / 2147483648.0);
                }
                return mono;
            }

            if (_isPcm && _bits == 24)
            {
                byte[] buf = new byte[count * 3];
                Marshal.Copy(data, buf, 0, buf.Length);
                for (int i = 0; i < frames; i++)
                {
                    double s = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int k = (i * ch + c) * 3;
                        int v = buf[k] | (buf[k + 1] << 8) | (buf[k + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        s += v;
                    }
                    mono[i] = (float)(s / ch / 8388608.0);
                }
                return mono;
            }

            return new float[frames];
        }

        private void ParseFormat(IntPtr pFormat)
        {
            WAVEFORMATEX wf = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
            _channels = wf.nChannels;
            SampleRate = (int)wf.nSamplesPerSec;
            _bits = wf.wBitsPerSample;
            _isFloat = wf.wFormatTag == 3;
            _isPcm = wf.wFormatTag == 1;

            if (wf.wFormatTag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
            {
                WAVEFORMATEXTENSIBLE ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(pFormat);
                _bits = ext.Format.wBitsPerSample;
                _isFloat = ext.SubFormat == FloatSubFormat;
                _isPcm = ext.SubFormat == PcmSubFormat;
            }

            bool ok = (_isFloat && _bits == 32) || (_isPcm && (_bits == 16 || _bits == 24 || _bits == 32));
            if (!ok)
                throw new NotSupportedException("播放设备使用的音频格式不受支持（" + _bits + " 位）。");
            if (_channels <= 0 || SampleRate <= 0)
                throw new NotSupportedException("播放设备的音频格式无效。");
        }

        // ---------------- COM / Win32 ----------------

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
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

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out int numFrames, out int flags,
                out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(int numFrames);
            [PreserveSig] int GetNextPacketSize(out int numFrames);
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
