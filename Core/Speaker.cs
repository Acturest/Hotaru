using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace FloatingTranslator
{
    /// <summary>离线语音朗读。两条后端依次尝试，都不需要安装任何东西：
    /// ①Windows.Media.SpeechSynthesis（OneCore / WinRT，Win10 起自带，合成成 wav 再用 SoundPlayer 播放）
    /// ②System.Speech（SAPI5，直接发声）
    /// 说明：某些受限权限（低完整性令牌）下 SAPI 会报「没有当前安全设置可用的语音」，
    /// 而 OneCore 走的是另一套语音仓库，先试它成功率更高；两条都失败时把两边的原始报错一起写日志。</summary>
    public class Speaker
    {
        private const int CREATE_NO_WINDOW = 0x08000000;
        private Process _proc;
        private readonly object _lock = new object();

        public bool Busy
        {
            get
            {
                lock (_lock)
                {
                    try { return _proc != null && !_proc.HasExited; }
                    catch { return false; }
                }
            }
        }

        public bool Speak(string text, string lang, int rate, Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > 3000) text = text.Substring(0, 3000);
            Stop();

            string prefix = (lang ?? "").Split('-')[0].ToLowerInvariant();
            if (prefix == "auto") prefix = "";
            if (prefix == "zh-hant") prefix = "zh";
            int r = Math.Max(-10, Math.Min(10, rate));
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" +
                                BuildScript(b64, prefix, r).Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    // 脚本里已经把 [Console]::OutputEncoding 设成 UTF-8，这里必须用同一套编码，
                    // 否则 PowerShell 写出的中文报错到 C# 这边会变成乱码（日志里全是问号方块）。
                    StandardErrorEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                var p = Process.Start(psi);
                if (p == null) return false;
                lock (_lock) { _proc = p; }

                var t = new Thread(() => Watch(p, onError)) { IsBackground = true, Name = "tts-watch" };
                t.Start();
                return true;
            }
            catch (Exception ex)
            {
                App.Log("朗读启动失败: " + ex.Message);
                if (onError != null) onError("朗读失败：系统语音不可用");
                return false;
            }
        }

        /// <summary>拼 PowerShell 脚本。注意：整段脚本通过 -Command "…" 传入，
        /// 因此脚本里**不能出现双引号**，字符串一律用单引号。</summary>
        private static string BuildScript(string b64, string prefix, int rate)
        {
            string pick = prefix.Length == 0
                ? ""
                : "$v=$all|Where-Object{$_.Language -like '" + prefix + "*'}|Select-Object -First 1;if($v){$sy.Voice=$v};";

            string pick2 = prefix.Length == 0
                ? ""
                : "$c=$s.GetInstalledVoices()|Where-Object{$_.Enabled -and $_.VoiceInfo.Culture.Name -like '" +
                  prefix + "*'}|Select-Object -First 1;if($c){$s.SelectVoice($c.VoiceInfo.Name)};";

            var sb = new StringBuilder();
            sb.Append("[Console]::OutputEncoding=[Text.Encoding]::UTF8;");
            sb.Append("$t=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('").Append(b64).Append("'));");
            sb.Append("$errs=@();$ok='';");
            // ---- ① WinRT / OneCore ----
            // 注意：PowerShell 5.1 里投影类型必须写成 [完整名,Windows.Media,ContentType=WindowsRuntime]，
            // 否则会报「找不到类型 …请确认加载了包含该类型的程序集」。
            sb.Append("try{");
            sb.Append("Add-Type -AssemblyName System.Runtime.WindowsRuntime|Out-Null;");
            sb.Append("$asTask=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0];");
            sb.Append("$tSyn=[Windows.Media.SpeechSynthesis.SpeechSynthesizer,Windows.Media,ContentType=WindowsRuntime];");
            sb.Append("$tStm=[Windows.Media.SpeechSynthesis.SpeechSynthesisStream,Windows.Media,ContentType=WindowsRuntime];");
            sb.Append("$sy=$tSyn::new();");
            sb.Append("$all=$tSyn::AllVoices;");
            sb.Append(pick);
            sb.Append("$m=$asTask.MakeGenericMethod($tStm);");
            sb.Append("$task=$m.Invoke($null,@($sy.SynthesizeTextToStreamAsync($t)));");
            sb.Append("$task.Wait(60000)|Out-Null;$st=$task.Result;");
            sb.Append("$ms=New-Object System.IO.MemoryStream;");
            sb.Append("([System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($st)).CopyTo($ms);");
            sb.Append("$wav=[IO.Path]::GetTempFileName();[IO.File]::WriteAllBytes($wav,$ms.ToArray());");
            sb.Append("$pl=New-Object Media.SoundPlayer $wav;$pl.PlaySync();");
            sb.Append("Remove-Item $wav -Force -ErrorAction SilentlyContinue;");
            sb.Append("$ok='winrt'");
            sb.Append("}catch{$errs+='winrt: '+$_.Exception.Message};");
            // ---- ② System.Speech (SAPI5) ----
            sb.Append("if(-not $ok){try{");
            sb.Append("Add-Type -AssemblyName System.Speech;");
            sb.Append("$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;");
            sb.Append("$s.Rate=").Append(rate.ToString()).Append(";");
            sb.Append(pick2);
            sb.Append("$s.Speak($t);$s.Dispose();$ok='sapi'");
            sb.Append("}catch{$errs+='sapi: '+$_.Exception.Message}};");
            sb.Append("if(-not $ok){[Console]::Error.WriteLine(($errs -join ' | '));exit 3};");
            sb.Append("Write-Output ('TTS-OK:'+$ok)");
            return sb.ToString();
        }

        private static void Watch(Process p, Action<string> onError)
        {
            string err = "";
            string outp = "";
            try
            {
                err = p.StandardError.ReadToEnd();
                outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
            }
            catch { return; }

            string raw = (err ?? "").Trim();
            string info = (outp ?? "").Trim();
            if (raw.Length == 0 && p.ExitCode == 0)
            {
                if (info.Length > 0) App.Log("朗读成功（" + info + "）");
                return;
            }
            App.Log("朗读失败 rc=" + p.ExitCode + ": " + (raw.Length > 400 ? raw.Substring(0, 400) : raw));
            if (onError == null) return;
            string low = raw.ToLowerInvariant();
            string msg;
            if (low.Contains("security setting") || raw.Contains("安全设置") || raw.Contains("权限"))
                msg = "朗读失败：系统语音在当前权限下不可用";
            else if (low.Contains("no voice installed") || raw.Contains("未安装语音") || raw.Contains("未找到"))
                msg = "朗读失败：系统未安装语音包";
            else msg = "朗读失败：系统语音不可用";
            onError(msg);
        }

        public void Stop()
        {
            Process p;
            lock (_lock) { p = _proc; _proc = null; }
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }
    }
}
