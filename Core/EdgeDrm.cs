using System;
using System.Collections.Generic;

namespace FloatingTranslator
{
    /// <summary>微软 Edge「大声朗读」的 DRM 令牌（Sec-MS-GEC）。
    /// 2026-10 起服务端只认 v2 令牌：CS64 摘要 + 一把被改过常数的 SHA-256，
    /// 两者都是微软刻意混淆过的实现，不能换成标准 SHA-256。
    /// 这里逐行对照 edge-tts 7.3.1 的 drm.py 移植；算法一旦被再次改动，
    /// 只有这里需要跟着改（对照上游 generate_sec_ms_gec_v2）。</summary>
    internal static class EdgeDrm
    {
        public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

        const double WinEpoch = 11644473600.0;
        const double SToNs = 1e9;

        // 标准 SHA-256 的 64 个轮常量；只有第 14 个被换成了 0xC9DC06A7。
        static readonly uint[] K =
        {
            0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
            0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
            0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
            0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
            0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
            0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
            0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
            0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
        };

        static readonly uint[] Initial = { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
        static readonly uint[] CustomState = { 0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2 };
        static readonly uint[] Schedule =
        {
            0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
            0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564,
        };
        const uint ShaAdjustment = 0xC9DC06A7;
        const uint Sigma0Adjustment = 0x059C0B80;
        const uint Sigma1Adjustment = 0x000B92C0;

        static readonly uint[,] Cs64Coeff =
        {
            { 0xE82E91B1, 0xB32700D0 }, { 0x44DE24C6, 0x88DFF51D }, { 0x3BEB65BC, 0x29514038 },
            { 0xC89C5933, 0xB328CBDF }, { 0x8D1EC211, 0xD405B4D5 }, { 0x314EF5D5, 0x26E55F3C },
            { 0xE92491B1, 0x607801C6 }, { 0xF0A5B9D5, 0x82B2C1FB }, { 0x06064CF3, 0xFA51A3DA },
            { 0xDA79FA60, 0x207CC464 }, { 0xBED20560, 0x3C3C74F3 }, { 0x89F9C9F8, 0xD2D14B61 },
            { 0x35FA38C6, 0xBB04A2C1 }, { 0xCB49E3C6, 0xC5626167 },
        };
        const uint Cs64Factor = 0x82893933;
        const uint Cs64Adjustment0 = 0xF2F90B1A;
        const uint Cs64Adjustment1 = 0x0B1A0000;

        static uint Rol16(uint v) { return (v << 16) | (v >> 16); }
        static uint Sigma0(uint v) { return (v >> 7) ^ (v << 25) ^ (v >> 18) ^ (v << 14) ^ (v >> 3); }
        static uint Sigma1(uint v) { return (v >> 17) ^ (v << 15) ^ (v >> 19) ^ (v << 13) ^ (v >> 10); }
        static uint Rotr(uint v, int n) { return (v >> n) | (v << (32 - n)); }

        /// <summary>生成 v2 令牌："CS64-SHA256"（大写十六进制，中间一个短横）。</summary>
        public static string GenerateSecMsGec(DateTime? utcNow = null)
        {
            double ticks = (utcNow ?? DateTime.UtcNow).ToUniversalTime()
                .Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return GenerateSecMsGec(ticks);
        }

        public static string GenerateSecMsGec(double unixSeconds)
        {
            double ticks = unixSeconds + WinEpoch;
            ticks -= ticks % 300.0;                 // 向下取整到 5 分钟
            ticks *= SToNs / 100.0;                 // 100ns 刻度
            string seed = ((long)Math.Round(ticks)).ToString() + TrustedClientToken;
            byte[] data = System.Text.Encoding.ASCII.GetBytes(seed);
            byte[] digest = Sha256Digest(data);
            uint lo, hi;
            Cs64Digest(data, digest, out lo, out hi);
            return Hex(BitConverter.GetBytes(lo)) + Hex(BitConverter.GetBytes(hi))
                 + "-" + Hex(digest);
        }

        static string Hex(byte[] b)
        {
            var sb = new System.Text.StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }

        /// <summary>第 16 位那两行是微软改过的组合旋转，别按标准 FIPS 版改写。</summary>
        static void ExpandSchedule(uint[] w, int from)
        {
            for (int i = from; i < 64; i++)
            {
                uint x = w[i - 2], y = w[i - 15];
                uint s1 = (x >> 17) ^ (x << 15) ^ (x >> 19) ^ (x << 13) ^ (x >> 10);
                uint s0 = (y >> 7) ^ (y << 25) ^ (y >> 18) ^ (y << 14) ^ (y >> 3);
                w[i] = s1 + w[i - 7] + s0 + w[i - 16];
            }
        }

        static uint[] Rounds(uint[] state, uint[] w, int firstRound)
        {
            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint e = state[4], f = state[5], g = state[6], h = state[7];
            for (int i = firstRound; i < 64; i++)
            {
                uint k = (firstRound == 14 && i == 14) ? ShaAdjustment : K[i];
                uint t1 = h + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + (g ^ (e & (f ^ g))) + k + w[i];
                uint t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) | (c & (a | b)));
                h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            return new[] { a, b, c, d, e, f, g, h };
        }

        static uint[] Sha256Init(byte[] salt)
        {
            var w = new uint[64];
            w[14] = (uint)((salt[0] << 16) | (salt[1] << 8) | salt[2]);
            w[15] = (uint)((salt[3] << 24) | (salt[4] << 16) | (salt[5] << 8) | salt[6]);
            for (int i = 16; i < 30; i++)
            {
                uint v = Schedule[i - 16];
                if (i - 2 >= 14) v += i == 16 ? (Sigma1(w[i - 2]) ^ Sigma1Adjustment) : Sigma1(w[i - 2]);
                if (i - 7 >= 14) v += w[i - 7];
                if (i - 15 >= 14) v += i == 29 ? (Sigma0(w[i - 15]) ^ Sigma0Adjustment) : Sigma0(w[i - 15]);
                w[i] = v;
            }
            w[30] = Sigma1(w[28]) + w[23] + Sigma0(w[15]) + w[14] + ShaAdjustment - K[14];
            ExpandSchedule(w, 31);
            uint[] working = Rounds(CustomState, w, 14);
            var st = new uint[8];
            for (int i = 0; i < 8; i++) st[i] = Initial[i] + working[i];
            return st;
        }

        static byte[] Sha256Digest(byte[] data)
        {
            if (data.Length < 7) throw new ArgumentException("令牌种子至少 7 字节");
            int length = 57 + data.Length;
            int pad = (55 - length) % 64;
            if (pad < 0) pad += 64;
            var padded = new List<byte>(length + pad);
            for (int i = 7; i < data.Length; i++) padded.Add(data[i]);
            padded.Add(0x80);
            for (int i = 0; i < pad; i++) padded.Add(0);
            ulong bits = (ulong)length * 8;
            for (int i = 7; i >= 0; i--) padded.Add((byte)(bits >> (i * 8)));

            uint[] st = Sha256Init(Slice(data, 0, 7));
            byte[] all = padded.ToArray();
            for (int off = 0; off < all.Length; off += 64)
            {
                var w = new uint[64];
                for (int i = 0; i < 16; i++)
                    w[i] = (uint)((all[off + i * 4] << 24) | (all[off + i * 4 + 1] << 16)
                                | (all[off + i * 4 + 2] << 8) | all[off + i * 4 + 3]);
                ExpandSchedule(w, 16);
                uint[] working = Rounds(st, w, 0);
                for (int i = 0; i < 8; i++) st[i] += working[i];
            }
            var digest = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                digest[i * 4] = (byte)(st[i] >> 24);
                digest[i * 4 + 1] = (byte)(st[i] >> 16);
                digest[i * 4 + 2] = (byte)(st[i] >> 8);
                digest[i * 4 + 3] = (byte)st[i];
            }
            return digest;
        }

        static void Cs64Digest(byte[] data, byte[] digest, out uint first, out uint second)
        {
            uint keyA = BitConverter.ToUInt32(digest, 0) | 1;
            uint keyB = BitConverter.ToUInt32(digest, 4) | 1;
            uint[] factors = { keyA * Cs64Factor, keyB * Cs64Factor };

            var terms = new List<uint>(14 + data.Length + 4);
            var offsets = new List<uint>(terms.Capacity);
            for (int i = 0; i < 14; i++)
            {
                terms.Add(Cs64Coeff[i, 0] * factors[i % 2]);
                offsets.Add(Cs64Coeff[i, 1]);
            }

            // 前导一个 0 字节，按小端 4 字节切词，词数为奇数时补 0
            var buf = new List<byte>(data.Length + 5) { 0 };
            buf.AddRange(data);
            while (buf.Count % 4 != 0) buf.Add(0);
            int words = buf.Count / 4;
            for (int i = 0; i < words; i++)
            {
                uint word = BitConverter.ToUInt32(buf.ToArray(), i * 4);
                uint term = word * (i % 2 == 0 ? keyA : keyB);
                uint offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
                if (i == 0)
                {
                    term += Cs64Adjustment0 * factors[0];
                    offset += Cs64Adjustment1;
                }
                terms.Add(term);
                offsets.Add(offset);
            }
            if (terms.Count % 2 != 0) { terms.Add(0); offsets.Add(0); }

            uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
            for (int i = 0; i < terms.Count; i += 2)
            {
                uint x = terms[i], y = terms[i + 1];
                uint t = Rol16(offsets[i] + x);
                uint q = t * 0x76F50F34u;
                swapAcc += Rol16(q) * 0xC3EFEA97u + q;
                t = Rol16(offsets[i + 1] + y);
                q = t * 0x1881CE3Cu;
                swapLast = Rol16(q) * 0x35BD1EC9u + q;
                swapAcc += swapLast;

                state = state * keyA + x;
                state = Rol16(state) * 0xCF98B111u;
                state = Rol16(state) * 0x87085B9Fu;
                state = Rol16(state) * 0x12CEB96Du;
                state = Rol16(state) * 0x257E1D83u;
                acc += state;

                t = state * keyB + y;
                t = Rol16(t) * 0xA27416F5u;
                t = Rol16(t) * 0xD38396FFu;
                state = Rol16(Rol16(t) * 0x7C932B89u) * 0xBFA49F69u;
                acc += state;
            }
            first = swapLast ^ state;
            second = swapAcc ^ acc;
        }

        static byte[] Slice(byte[] src, int start, int count)
        {
            var r = new byte[count];
            Array.Copy(src, start, r, 0, count);
            return r;
        }
    }
}
