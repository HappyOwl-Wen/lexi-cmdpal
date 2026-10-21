using System.Runtime.InteropServices;

namespace Lexi.Dictionary.Import;

/// <summary>
/// Traditional → Simplified Chinese via the Windows NLS API (LCMapStringEx + LCMAP_SIMPLIFIED_CHINESE).
/// Character-level mapping only (no phrase conversion), which is adequate for short dictionary glosses.
/// On non-Windows platforms the text is returned unchanged.
/// </summary>
internal static partial class ChineseScript
{
    private const uint LcmapSimplifiedChinese = 0x02000000;

    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text) || !OperatingSystem.IsWindows())
        {
            return text;
        }

        var hasCjk = false;
        foreach (var c in text)
        {
            if (c >= '⺀')
            {
                hasCjk = true;
                break;
            }
        }

        if (!hasCjk)
        {
            return text;
        }

        var buffer = new char[text.Length + 8];
        var n = LCMapStringEx("zh-CN", LcmapSimplifiedChinese, text, text.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return n > 0 ? new string(buffer, 0, n) : text;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[] lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);
}
