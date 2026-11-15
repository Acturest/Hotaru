using System;
using System.Diagnostics;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace FloatingTranslator
{
    /// <summary>
    /// 播放本地音频（Irodori-TTS 返回的 wav）。主力是 WPF 的 MediaPlayer；
    /// 媒体管道不可用时（受限权限下常见 MediaFailed 0xC00D11D2）自动退回 winmm 的 SoundPlayer。
    /// 只能在 UI 线程创建/调用。
    /// </summary>
    public class AudioPlayer : IDisposable
    {
        private MediaPlayer _player;          // 每次 Play 重新建一个：事件里带着属于这一次的 token，避免旧事件误伤新播放
        private readonly Stopwatch _sw = Stopwatch.StartNew();   // 相对本播放器创建时刻的毫秒，用来量播放链路的延迟
        private SoundPlayer _sp;
        private string _path;
        private bool _playing;
        private int _token;
        private bool _mediaBroken;      // 本机媒体管道已知不通（失败一次后不再尝试 MediaPlayer）

        /// <summary>
        /// 直接读 wav 头算出时长。媒体管道播不出来时（拿不到 NaturalDuration），
        /// 用它来给「等播完」设一个贴合音频长度的看门狗，而不是干等 30 秒。
        /// 解析失败返回 TimeSpan.Zero。
        /// </summary>
        public static TimeSpan WavDuration(string path)
        {
            try
            {
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var br = new System.IO.BinaryReader(fs))
                {
                    if (new string(br.ReadChars(4)) != "RIFF") return TimeSpan.Zero;
                    br.ReadInt32();
                    if (new string(br.ReadChars(4)) != "WAVE") return TimeSpan.Zero;
                    int byteRate = 0;
                    long dataSize = 0;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = new string(br.ReadChars(4));
                        int size = br.ReadInt32();
                        if (id == "fmt ")
                        {
                            // fmt 块是 16 字节：格式(2) 声道(2) 采样率(4) 字节率(4) 块对齐(2) 位深(2)
                            // 以前这里只读了 12 字节就跳 size-16，位置差 4 字节 → 后面找不到 data 块 →
                            // WavDuration 恒为 0（看门狗于是退成「按 20 秒等」，长段会被多等很久）
                            br.ReadInt16();                  // wFormatTag
                            br.ReadInt16();                  // nChannels
                            br.ReadInt32();                  // nSamplesPerSec
                            byteRate = br.ReadInt32();       // nAvgBytesPerSec
                            if (size >= 16)
                            {
                                br.ReadInt16();              // nBlockAlign
                                br.ReadInt16();              // wBitsPerSample
                            }
                            if (size > 16) br.ReadBytes(size - 16);
                        }
                        else if (id == "data")
                        {
                            // 有些编码器把 data 长度写成 0 / -1（边写边读的流），按剩下全读
                            long avail = fs.Length - fs.Position;
                            dataSize = (size <= 0 || size > avail) ? avail : size;
                            if (dataSize > 0) br.ReadBytes((int)dataSize);
                        }
                        else
                        {
                            if (size < 0 || size > fs.Length - fs.Position) break;
                            br.ReadBytes(size);
                        }
                        if ((size & 1) == 1 && fs.Position < fs.Length) br.ReadByte();   // RIFF 块按偶数字节对齐
                    }
                    if (byteRate > 0 && dataSize > 0)
                        return TimeSpan.FromSeconds((double)dataSize / byteRate);
                }
            }
            catch (Exception) { }
            return TimeSpan.Zero;
        }

        /// <summary>播放自然结束、或彻底失败时回调（一律回 UI 线程）。</summary>
        public event Action Ended;

        /// <summary>音频真的打开了（拿到时长）时回调。</summary>
        public event Action<TimeSpan> Opened;

        /// <summary>两条通道都播不了时回调，消息可直接进日志/提示条。</summary>
        public event Action<string> Failed;

        public bool IsPlaying { get { return _playing; } }
        public double Volume { get; set; } = 1.0;

        public AudioPlayer()
        {
        }

        /// <summary>建一个只服务这一次播放的 MediaPlayer（事件在闭包里带上本次的 token）。</summary>
        private MediaPlayer NewPlayer(int token)
        {
            var p = new MediaPlayer();
            p.MediaOpened += (s, e) =>
            {
                if (token != _token) return;              // 这次播放已被作废
                var h = Opened;
                if (h != null)
                {
                    TimeSpan d = TimeSpan.Zero;
                    try { if (p.NaturalDuration.HasTimeSpan) d = p.NaturalDuration.TimeSpan; }
                    catch (Exception) { }
                    h(d);
                }
            };
            p.MediaEnded += (s, e) => Finish(token);
            p.MediaFailed += (s, e) =>
            {
                if (token != _token) return;
                string msg = (e != null && e.ErrorException != null) ? e.ErrorException.Message : "未知原因";
                _mediaBroken = true;                       // 这台机器上媒体管道不通，后面直接走 winmm，省掉每次的失败等待
                App.Log("Irodori 媒体管道报错(t=" + _sw.ElapsedMilliseconds + "ms): " + msg + "（改用 winmm 重试）");
                StartFallback(token);
            };
            return p;
        }

        public void Play(string path)
        {
            Stop();
            _path = path;
            _token++;
            _playing = true;
            App.Log("播放器(t=" + _sw.ElapsedMilliseconds + "ms): 第 " + _token + " 次播放 " + System.IO.Path.GetFileName(path));
            if (_mediaBroken)
            {
                // 媒体管道本机已知不可用：直接走 winmm，别每次都先等一次 MediaFailed
                StartFallback(_token);
                return;
            }
            try
            {
                var p = NewPlayer(_token);
                _player = p;
                p.Open(new Uri(path, UriKind.Absolute));
                p.Volume = Volume;
                p.Play();
            }
            catch (Exception ex)
            {
                _mediaBroken = true;
                App.Log("Irodori 媒体管道不可用(t=" + _sw.ElapsedMilliseconds + "ms): " + ex.Message + "（改用 winmm 重试）");
                StartFallback(_token);
            }
        }

        /// <summary>MediaPlayer 播不了时的兜底：winmm 的 SoundPlayer，在后台线程同步播完。</summary>
        private void StartFallback(int token)
        {
            if (!_playing || token != _token) return;
            string path = _path;
            SoundPlayer sp;
            try
            {
                sp = new SoundPlayer(path);
                sp.Load();                       // 先校验，确实能播再切过去
            }
            catch (Exception ex)
            {
                string msg = "系统播放器也打不开：" + ex.Message;
                App.Log("Irodori 播放失败（winmm 也打不开）: " + ex.Message);
                var hf0 = Failed; if (hf0 != null) hf0(msg);
                Finish(token);
                return;
            }

            _sp = sp;
            App.Log("Irodori 播放改用 winmm（SoundPlayer）(t=" + _sw.ElapsedMilliseconds + "ms)");
            Task.Run(() =>
            {
                string err = null;
                long t0 = _sw.ElapsedMilliseconds;
                try { sp.PlaySync(); }
                catch (Exception ex) { err = ex.Message; }
                long t1 = _sw.ElapsedMilliseconds;
                App.Log("播放器(t=" + t1 + "ms): winmm 播完，PlaySync 返回耗时 " + (t1 - t0) + "ms");
                // 提前返回 = 音频被别的东西打断（或被删了），这种会让「等播完」的循环误以为这一段结束了
                double exp = WavDuration(path).TotalMilliseconds;
                if (exp > 500 && (t1 - t0) < exp * 0.7)
                    App.Log("播放器: winmm 提前返回（实际 " + (t1 - t0) + "ms / 音频 " + (long)exp
                            + "ms）文件还在=" + System.IO.File.Exists(path));
                Action done = () =>
                {
                    if (err != null)
                    {
                        App.Log("Irodori 播放失败（winmm）: " + err);
                        var hf = Failed; if (hf != null) hf(err);
                    }
                    Finish(token);
                };
                var disp = Application.Current != null ? Application.Current.Dispatcher : null;
                if (disp != null) disp.BeginInvoke(done); else done();
            });
        }

        private void Finish(int token)
        {
            if (token != _token) return;         // 已经被新的播放/停止作废
            _playing = false;
            App.Log("播放器(t=" + _sw.ElapsedMilliseconds + "ms): 播放结束回调");
            var h = Ended;
            if (h != null) h();
        }

        public void Stop()
        {
            CloseStream();                       // 流式播放（若有）一起停掉
            _token++;                            // 作废在飞的回调
            _playing = false;
            long t0 = _sw.ElapsedMilliseconds;
            var p = _player;
            _player = null;
            if (p != null)
            {
                try { p.Stop(); p.Close(); } catch (Exception) { }
            }
            if (_sp != null)
            {
                try { _sp.Stop(); } catch (Exception) { }
                _sp = null;
            }
            long dt = _sw.ElapsedMilliseconds - t0;
            if (dt > 50) App.Log("播放器: Stop 用了 " + dt + "ms（在 UI 线程上，会卡界面）");
        }

        // ==================== 连续播放（流式） ====================
        // 一段接一段的朗读不再「播完一段再开下一段」：每段 PCM 直接追加到同一个已打开的设备上，
        // 段与段之间没有重新开播的 0.4~0.7 秒静音（见 WaveOutStream 的注释）。

        private WaveOutStream _stream;
        private bool _streamMode;              // 本轮走流式播放
        private bool _streamGotData;           // 至少成功追加过一段
        private bool _streamDone;              // 已经自然播完
        private double _streamVolume = 1.0;
        private TaskCompletionSource<bool> _streamWait;

        /// <summary>本轮是否在走连续播放（流式）。</summary>
        public bool IsStreaming { get { return _streamMode; } }

        /// <summary>开始一轮连续播放。真正打开设备发生在第一次 AppendWav。</summary>
        public void BeginStream()
        {
            Stop();
            _streamVolume = Volume;               // 与 Play 一样跟 Volume 属性走
            _streamMode = true;
            _streamGotData = false;
            _streamDone = false;
            _streamWait = null;
        }

        /// <summary>
        /// 追加一段 wav：立刻开始出声，或紧接在上一段后面（中间没有空隙）。
        /// 失败时返回 false，调用方应退回逐段播放（<paramref name="error"/> 可直接进日志）。
        /// </summary>
        public bool AppendWav(string path, out string error)
        {
            error = "";
            byte[] pcm;
            WaveOutStream.WavFormat fmt;
            if (!WaveOutStream.TryReadPcm16(path, out pcm, out fmt, out error)) return false;

            int headMs, tailMs;
            int cut = TrimSilence(ref pcm, fmt, out headMs, out tailMs);
            if (cut > 0)
                App.Log("连续播放：裁掉段首/段尾静音 " + headMs + "ms/" + tailMs + "ms（" + System.IO.Path.GetFileName(path) + "）");

            if (_stream == null)
            {
                _stream = new WaveOutStream();
                _stream.Drained += OnStreamDrained;
                _stream.SetVolume(_streamVolume);
                if (!_stream.Start(out error)) { CloseStream(); return false; }
            }
            if (!_stream.Append(pcm, fmt, out error)) return false;

            _streamGotData = true;
            _playing = true;
            return true;
        }

        /// <summary>本轮没有更多段了。</summary>
        public void EndStream()
        {
            if (_stream != null) _stream.EndOfStream();
        }

        /// <summary>等这一轮流式播放真正播完。返回 true = 播完了，false = 被取消/停止。</summary>
        public async Task<bool> WaitDrainAsync(CancellationToken ct)
        {
            if (!_streamMode) return true;
            if (_streamDone || !_streamGotData) return true;
            var tcs = new TaskCompletionSource<bool>();
            _streamWait = tcs;
            if (ct.IsCancellationRequested) { _streamWait = null; return false; }
            var reg = ct.Register(() => { var w = _streamWait; if (w != null) w.TrySetResult(false); });
            try { return await tcs.Task; }
            finally { reg.Dispose(); }
        }

        private void OnStreamDrained()
        {
            _streamDone = true;
            _playing = false;
            App.Log("连续播放：本轮全部播完");
            var w = _streamWait;
            _streamWait = null;
            if (w != null) w.TrySetResult(true);
        }

        // 停顿/设备异常这些细节由 WaveOutStream 自己记日志（「暂时没有数据，等下一段合成…」/「数据接上了，静音 20ms」），
        // 这里只管「整轮播完」这一个状态。

        private void CloseStream()
        {
            var s = _stream;
            _stream = null;
            if (s != null)
            {
                try { s.Drained -= OnStreamDrained; } catch (Exception) { }
                try { s.Stop(); } catch (Exception) { }
                try { s.Dispose(); } catch (Exception) { }
            }
            if (_streamMode) _playing = false;
            _streamMode = false;
            _streamGotData = false;
            _streamDone = false;
            var w = _streamWait;
            _streamWait = null;
            if (w != null) w.TrySetResult(false);
        }

        /// <summary>
        /// 裁掉段首/段尾的数字静音（16 位 PCM）。TTS 每段开头结尾常常带几十~几百毫秒静音，
        /// 段与段直接接起来时它们会叠加成明显的「停顿」。只裁到 -40dBFS 以下的部分，并各留 20ms。
        /// 返回裁掉的字节数（0 = 没裁）。
        /// </summary>
        private static int TrimSilence(ref byte[] pcm, WaveOutStream.WavFormat fmt, out int headMs, out int tailMs)
        {
            headMs = 0; tailMs = 0;
            byte[] buf = pcm;                              // ref 参数不能进局部函数，先落到本地变量
            int align = fmt.nBlockAlign <= 0 ? 2 : fmt.nBlockAlign;
            int total = buf.Length / align;
            if (total <= 0) return 0;

            const int threshold = 300;                      // 约 -40dBFS
            int marginFrames = Math.Max(1, fmt.nSamplesPerSec / 1000 * 20);   // 留 20ms
            int maxTrimFrames = Math.Max(marginFrames, fmt.nSamplesPerSec / 1000 * 400); // 单侧最多裁 400ms

            int Lead(int dir, int start)
            {
                int quiet = 0;
                for (int f = start; f >= 0 && f < total; f += dir)
                {
                    bool loud = false;
                    for (int c = 0; c < align; c += 2)
                    {
                        int v = BitConverter.ToInt16(buf, f * align + c);
                        if (v > threshold || v < -threshold) { loud = true; break; }
                    }
                    if (loud) break;
                    quiet++;
                    if (quiet >= maxTrimFrames) break;
                }
                return quiet;
            }

            int headQuiet = Lead(1, 0);
            int tailQuiet = Lead(-1, total - 1);
            if (headQuiet >= total || tailQuiet >= total) return 0;   // 整段都是静音：不动它

            int head = headQuiet > marginFrames ? headQuiet - marginFrames : 0;
            int tail = tailQuiet > marginFrames ? tailQuiet - marginFrames : 0;
            if (head + tail >= total) return 0;

            headMs = head * 1000 / fmt.nSamplesPerSec;
            tailMs = tail * 1000 / fmt.nSamplesPerSec;
            int startByte = head * align;
            int len = (total - head - tail) * align;
            var dst = new byte[len];
            Buffer.BlockCopy(buf, startByte, dst, 0, len);
            pcm = dst;
            return head * align + tail * align;
        }

        public void Dispose() { Stop(); }
    }
}
