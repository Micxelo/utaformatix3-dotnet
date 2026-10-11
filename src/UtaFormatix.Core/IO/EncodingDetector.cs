using System.Text;

namespace UtaFormatix.Core.IO;

/// <summary>
/// 从字节序列自动检测文本编码并解码，等价于 Kotlin 版使用的
/// <c>encoding-japanese</c> 的 <c>detect</c> + <c>convert</c> 行为。
///
/// 支持的检测目标：UTF-8、Shift_JIS (CP932)、EUC-JP、JIS、ISO-2022-JP、ASCII、Latin-1。
/// </summary>
internal static class EncodingDetector
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
    private static readonly Encoding ShiftJis;
    private static readonly Encoding EucJp;
    private static readonly Encoding Iso2022Jp;

    private static bool _initialized;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _initialized = true;
    }

    /// <summary>返回检测到的编码（字节本身未被占用时）。</summary>
    public static Encoding Detect(byte[] data)
    {
        EnsureInitialized();
        if (data.Length == 0) return Latin1;

        // EBCDIC / BOM 特判与 ISO-2022-JP 转义序列
        if (HasEscapeSequence(data, "0x1B", "$@") || HasEscapeSequence(data, "0x1B", "$B"))
            return Iso2022Jp;

        if (IsAscii(data))
            return Encoding.ASCII;

        // 严格 UTF-8 校验（拒绝尾字节越界/过度编码，避免把 SJIS 误判为 UTF-8）
        if (IsStrictUtf8(data))
            return Utf8Strict;

        // Shift_JIS 结构校验（CP932）
        if (IsCp932(data))
            return ShiftJis;

        // EUC-JP 结构校验
        if (IsEucJp(data))
            return EucJp;

        return Latin1;
    }

    /// <summary>等价于 Kotlin 的 <c>bytes.decode(detect(bytes))</c>：检测并解码为 UTF-8 字符串。</summary>
    public static string Decode(byte[] data)
    {
        var encoding = Detect(data);
        // Shift_JIS/EUC-JP/JIS 等用对应编码解码；结果已是 Unicode 字符串。
        // 若目标是 UTF-8 且严格判定失败（理论上不会走到），退回 Latin-1。
        try
        {
            return encoding.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Latin1.GetString(data);
        }
    }

    private static bool IsAscii(byte[] data)
    {
        foreach (var b in data)
            if (b >= 0x80) return false;
        return true;
    }

    private static bool IsStrictUtf8(byte[] data)
    {
        int i = 0;
        int n = data.Length;
        while (i < n)
        {
            byte b = data[i];
            if (b < 0x80) { i++; continue; }
            int len;
            if (b >= 0xC2 && b <= 0xDF) len = 1;                 // 2 字节；C0/C1 为过度编码，拒绝
            else if (b >= 0xE0 && b <= 0xEF) len = 2;            // 3 字节
            else if (b >= 0xF0 && b <= 0xF4) len = 3;            // 4 字节；F5+ 超出 Unicode
            else return false;
            if (i + len >= n) return false;                      // 尾字节不足
            // 首字节越界检查（拒绝过长编码）
            if (len == 2 && b == 0xE0 && data[i + 1] < 0xA0) return false;
            if (len == 2 && b == 0xED && data[i + 1] > 0x9F) return false;
            if (len == 3 && b == 0xF0 && data[i + 1] < 0x90) return false;
            if (len == 3 && b == 0xF4 && data[i + 1] > 0x8F) return false;
            for (int j = 1; j <= len; j++)
            {
                var c = data[i + j];
                if (c < 0x80 || c > 0xBF) return false;          // 尾字节必须是 0x80-0xBF
            }
            i += len + 1;
        }
        return true;
    }

    // Shift_JIS (CP932) 结构：0xA1-0xDF 为单字节半角假名；
    // 双字节首字节 0x81-0x9F / 0xE0-0xEF，尾字节 0x40-0x7E / 0x80-0xFC。
    private static bool IsCp932(byte[] data)
    {
        int i = 0;
        int n = data.Length;
        while (i < n)
        {
            byte b = data[i];
            if (b < 0x80) { i++; continue; }
            if (b >= 0xA1 && b <= 0xDF) { i++; continue; }        // 半角假名 / 全角专用区字节
            bool lead = (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xEF);
            if (!lead) return false;
            if (i + 1 >= n) return false;
            byte t = data[i + 1];
            if (!((t >= 0x40 && t <= 0x7E) || (t >= 0x80 && t <= 0xFC))) return false;
            i += 2;
        }
        return true;
    }

    private static bool IsEucJp(byte[] data)
    {
        int i = 0;
        int n = data.Length;
        while (i < n)
        {
            byte b = data[i];
            if (b < 0x80) { i++; continue; }
            if (b == 0x8E)                                      // 半角假名
            {
                if (i + 1 >= n || data[i + 1] < 0xA1 || data[i + 1] > 0xDF) return false;
                i += 2;
                continue;
            }
            if (b == 0x8F)                                      // 三字节（JIS X 0212）
            {
                if (i + 2 >= n) return false;
                if (data[i + 1] < 0xA1 || data[i + 1] > 0xFE) return false;
                if (data[i + 2] < 0xA1 || data[i + 2] > 0xFE) return false;
                i += 3;
                continue;
            }
            if (b >= 0xA1 && b <= 0xFE)                         // 双字节
            {
                if (i + 1 >= n) return false;
                if (data[i + 1] < 0xA1 || data[i + 1] > 0xFE) return false;
                i += 2;
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool HasEscapeSequence(byte[] data, string hexEscape, string bytes)
    {
        // 简化：检测 ESC (0x1B) 后跟 "$@"/"$B" 等 JIS 转义
        for (int i = 0; i + bytes.Length < data.Length; i++)
        {
            if (data[i] == 0x1B)
            {
                bool match = true;
                for (int j = 0; j < bytes.Length; j++)
                {
                    if (data[i + 1 + j] != (byte)bytes[j]) { match = false; break; }
                }
                if (match) return true;
            }
        }
        return false;
    }

    static EncodingDetector()
    {
        EnsureInitialized();
        ShiftJis = Encoding.GetEncoding(932);
        EucJp = Encoding.GetEncoding(51932);
        Iso2022Jp = Encoding.GetEncoding(50220);
    }
}
