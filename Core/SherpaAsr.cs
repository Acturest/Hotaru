using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace FloatingTranslator
{
    /// <summary>
    /// 离线语音识别：sherpa-onnx + Parakeet 日语 CTC 模型（sherpa-onnx-nemo-parakeet-tdt_ctc-0.6b-ja-35000-int8）。
    /// 纯 CPU、INT8、不联网；模型由用户自己下载解压，这里只按路径加载。
    /// 说明：这是离线（非流式）模型，所以流程是「录一段 → 停止 → 整段解码」。
    /// 要真正流式识别，把 OfflineRecognizer 换成 OnlineRecognizer + 流式 zipformer 模型即可（接口层不用改）。
    /// </summary>
    public sealed class SherpaAsr : IDisposable
    {
        private readonly object _gate = new object();
        private OfflineRecognizer _rec;
        private string _modelPath = "";
        private string _tokensPath = "";
        private int _threads;

        public bool Ready { get { lock (_gate) return _rec != null; } }
        public string ModelPath { get { lock (_gate) return _modelPath; } }
        public int Threads { get { lock (_gate) return _threads; } }

        /// <summary>
        /// 解析模型路径：支持「文件夹」或「model.int8.onnx 文件」。文件夹里可以再套一层解压目录。
        /// </summary>
        public static bool TryResolve(string input, out string modelPath, out string tokensPath, out string error)
        {
            modelPath = "";
            tokensPath = "";
            error = "";
            input = (input ?? "").Trim();
            if (input.Length == 0)
            {
                error = "还没选语音模型文件夹";
                return false;
            }

            string dir = null;
            if (File.Exists(input) && input.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                modelPath = input;
                dir = Path.GetDirectoryName(input);
            }
            else if (Directory.Exists(input))
            {
                dir = input;
                modelPath = FindModel(input);
                if (modelPath.Length == 0)
                {
                    error = "文件夹里没找到 model.int8.onnx（或 model.onnx）。请把下载的 sherpa-onnx-nemo-parakeet-tdt_ctc-0.6b-ja-35000-int8 解压到这里。";
                    return false;
                }
            }
            else
            {
                error = "模型路径不存在：" + input;
                return false;
            }

            tokensPath = FindTokens(dir, modelPath);
            if (tokensPath.Length == 0)
            {
                error = "没找到 tokens.txt（要和 model.int8.onnx 放在一起）";
                return false;
            }
            try
            {
                long len = new FileInfo(modelPath).Length;
                if (len < 1024 * 1024)
                {
                    error = "模型文件只有 " + (len / 1024) + " KB，应该不完整（这个日语模型解压后约 600 MB），请重新解压或重新下载。";
                    return false;
                }
            }
            catch (Exception ex) { App.Log("ASR：读取模型大小失败: " + ex.Message); }
            return true;
        }

        private static string FindModel(string dir)
        {
            // 优先 int8（需求指定 INT8），再退回普通 onnx；只看本层和一层子目录，避免全盘扫
            string[] names = { "model.int8.onnx", "model.onnx" };
            foreach (var name in names)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    foreach (var name in names)
                    {
                        string p = Path.Combine(sub, name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch (Exception ex) { App.Log("ASR：扫描模型文件夹失败: " + ex.Message); }
            return "";
        }

        private static string FindTokens(string dir, string modelPath)
        {
            var probes = new List<string>();
            if (!string.IsNullOrEmpty(modelPath)) probes.Add(Path.Combine(Path.GetDirectoryName(modelPath), "tokens.txt"));
            if (!string.IsNullOrEmpty(dir)) probes.Add(Path.Combine(dir, "tokens.txt"));
            foreach (var p in probes)
                if (File.Exists(p)) return p;
            try
            {
                if (!string.IsNullOrEmpty(modelPath))
                {
                    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(modelPath), "tokens.txt", SearchOption.AllDirectories))
                        return f;
                }
            }
            catch { }
            return "";
        }

        /// <summary>加载模型（在后台线程调用；加载 600MB 模型要几秒）。路径和线程数不变时直接复用。</summary>
        public void Load(string dirOrFile, int threads)
        {
            string model, tokens, err;
            if (!TryResolve(dirOrFile, out model, out tokens, out err)) throw new InvalidOperationException(err);
            threads = threads <= 0 ? Math.Clamp(Environment.ProcessorCount / 2, 2, 6) : Math.Clamp(threads, 1, 16);

            lock (_gate)
            {
                if (_rec != null && _modelPath == model && _tokensPath == tokens && _threads == threads) return;
                var sw = Stopwatch.StartNew();
                var old = _rec;
                _rec = null;
                if (old != null) { try { old.Dispose(); } catch { } }

                var cfg = new OfflineRecognizerConfig();
                cfg.FeatConfig.SampleRate = 16000;
                cfg.FeatConfig.FeatureDim = 80;
                cfg.ModelConfig.NeMoCtc.Model = model;
                cfg.ModelConfig.Tokens = tokens;
                cfg.ModelConfig.Provider = "cpu";
                cfg.ModelConfig.NumThreads = threads;
                cfg.ModelConfig.Debug = 0;
                cfg.DecodingMethod = "greedy_search";

                _rec = new OfflineRecognizer(cfg);
                _modelPath = model;
                _tokensPath = tokens;
                _threads = threads;
                App.Log("ASR：模型加载完成 " + Path.GetFileName(model) + " 线程=" + threads
                        + " 耗时=" + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
            }
        }

        /// <summary>整段解码。samples 为单声道 float（-1..1），sampleRate 不匹配时 sherpa 内部会重采样。</summary>
        public string Recognize(float[] samples, int sampleRate = 16000)
        {
            if (samples == null || samples.Length == 0) return "";
            lock (_gate)
            {
                if (_rec == null) throw new InvalidOperationException("语音模型还没加载");
                var sw = Stopwatch.StartNew();
                using (var stream = _rec.CreateStream())
                {
                    stream.AcceptWaveform(sampleRate, samples);
                    _rec.Decode(stream);
                    string text = stream.Result.Text ?? "";
                    text = CleanText(text);
                    App.Log("ASR：识别完成 音频=" + (samples.Length / (double)sampleRate).ToString("0.00")
                            + "s 解码=" + sw.Elapsed.TotalSeconds.ToString("0.00") + "s 文本长度=" + text.Length);
                    return text;
                }
            }
        }

        /// <summary>直接识别一个 WAV 文件（PCM 8/16/24/32 或 float），内部读取并重采样。</summary>
        public string RecognizeFile(string wavPath)
        {
            int rate;
            float[] samples = WavAudio.ReadMono(wavPath, out rate);
            return Recognize(samples, rate);
        }

        /// <summary>直接识别 16-bit PCM 字节流（小端，单声道），例如网络收到的音频块。</summary>
        public string RecognizePcm16(byte[] pcm, int sampleRate = 16000, int offset = 0, int count = -1)
        {
            if (pcm == null || pcm.Length == 0) return "";
            if (offset < 0 || offset > pcm.Length) offset = 0;
            if (count < 0) count = pcm.Length - offset;
            count = Math.Min(count, pcm.Length - offset);
            count -= count % 2;
            float[] samples = new float[count / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(pcm[offset + i * 2] | (pcm[offset + i * 2 + 1] << 8));
                samples[i] = s / 32768f;
            }
            return Recognize(samples, sampleRate);
        }

        /// <summary>去掉模型输出里的空格/下划线占位符（日语不用词间空格），并压掉多余空白。</summary>
        public static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("▁", " ").Trim();
            text = Regex.Replace(text, @"\s+", " ");
            text = Regex.Replace(text, @"(?<=[\u3040-\u30ff\u3400-\u9fff々ー])[ \u3000]+(?=[\u3040-\u30ff\u3400-\u9fff々ー])", "");
            return text.Trim();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_rec != null) { try { _rec.Dispose(); } catch { } }
                _rec = null;
            }
        }
    }

    /// <summary>最小 WAV 读取（PCM 8/16/24/32 + IEEE float），转成单声道 float。只用于「测试音频 / 文件识别」。</summary>
    public static class WavAudio
    {
        public static float[] ReadMono(string path, out int sampleRate)
        {
            sampleRate = 16000;
            byte[] all = File.ReadAllBytes(path);
            if (all.Length < 44 || Encoding.ASCII.GetString(all, 0, 4) != "RIFF" || Encoding.ASCII.GetString(all, 8, 4) != "WAVE")
                throw new InvalidDataException("不是 WAV 文件：" + path);

            int pos = 12;
            ushort format = 1, channels = 1, bits = 16;
            int rate = 16000;
            int dataOffset = -1, dataLength = 0;
            while (pos + 8 <= all.Length)
            {
                string id = Encoding.ASCII.GetString(all, pos, 4);
                int size = BitConverter.ToInt32(all, pos + 4);
                int body = pos + 8;
                if (size < 0) break;
                if (id == "fmt " && body + 16 <= all.Length)
                {
                    format = BitConverter.ToUInt16(all, body);
                    channels = BitConverter.ToUInt16(all, body + 2);
                    rate = BitConverter.ToInt32(all, body + 4);
                    bits = BitConverter.ToUInt16(all, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = Math.Min(size, all.Length - body);
                    break;
                }
                pos = body + size + (size & 1);
            }
            if (dataOffset < 0) throw new InvalidDataException("WAV 里没有 data 段");
            sampleRate = rate > 0 ? rate : 16000;
            if (channels == 0) channels = 1;

            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) throw new InvalidDataException("不支持的位深：" + bits);
            int frameBytes = bytesPerSample * channels;
            int frames = dataLength / frameBytes;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += SampleAt(all, dataOffset + i * frameBytes + c * bytesPerSample, format, bytesPerSample);
                mono[i] = sum / channels;
            }
            return mono;
        }

        private static float SampleAt(byte[] b, int p, ushort format, int bytes)
        {
            if (format == 3)
            {
                if (bytes == 4) return BitConverter.ToSingle(b, p);
                if (bytes == 8) return (float)BitConverter.ToDouble(b, p);
                return 0;
            }
            if (bytes == 1) return (b[p] - 128) / 128f;
            if (bytes == 2) return BitConverter.ToInt16(b, p) / 32768f;
            if (bytes == 3)
            {
                int v = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                return v / 8388608f;
            }
            if (bytes == 4) return BitConverter.ToInt32(b, p) / 2147483648f;
            return 0;
        }
    }
}
