using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lexi.Extension.Services;

/// <summary>
/// Gives the keyboard to Command Palette when it shows a Lexi page while another application keeps the
/// foreground — the reason why letters typed after Alt+T sometimes went to Chrome.
/// </summary>
/// <remarks>
/// Command Palette (0.100, unchanged in 0.102) shows its window and calls <c>SetForegroundWindow</c> when a hotkey
/// fires, and Windows does not always allow it. The palette is then drawn on top but never activated: it neither
/// receives the keys nor hides, and every further Alt+T fails the same way until the palette is activated some
/// other way (reproduced with artifacts\cmdpal-verify\verify10.ps1). When a top-level Lexi page is opened this
/// watches for that state for a moment and activates the palette from the extension process. A plain
/// SetForegroundWindow from here is refused as well; the empty mouse event before it (no movement, no button)
/// makes this process the last input provider, which lets the call pass the foreground lock — the technique
/// PowerToys Run uses for its own window. Nothing happens while the palette, or another Command Palette window
/// such as its settings or a menu, is in front.
/// </remarks>
internal static partial class PaletteFocus
{
    private const string HostProcessName = "Microsoft.CmdPal.UI";
    private const string PaletteWindowClass = "WinUIDesktopWin32WindowClass";
    private const int WatchMs = 1500;
    private const int PollMs = 25;
    private const int MaxActivations = 3;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsDisabled = 0x08000000;
    private const long WsThickFrame = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint DwmwaCloaked = 14;

    private static long _generation;

    /// <summary>Set when running as Command Palette's extension; the self-test leaves it off.</summary>
    public static bool Enabled { get; set; }

    /// <summary>What the last watch found, for diagnostics.</summary>
    public static string? LastOutcome { get; private set; }

    /// <summary>Call when a top-level Lexi page is opened. Returns at once; the newest call owns the watch.</summary>
    public static void OnPageOpened()
    {
        if (Enabled && OperatingSystem.IsWindows())
        {
            var generation = Interlocked.Increment(ref _generation);
            _ = Task.Run(() => WatchAsync(generation));
        }
    }

    /// <summary>Process IDs of the running Command Palette.</summary>
    internal static HashSet<uint> HostProcessIds()
    {
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(HostProcessName))
        {
            ids.Add((uint)process.Id);
            process.Dispose();
        }

        return ids;
    }

    /// <summary>The palette window while Command Palette shows it; 0 while it is hidden.</summary>
    internal static nint FindPalette(HashSet<uint> hostIds)
    {
        nint found = 0;
        for (var hwnd = FindWindowEx(0, 0, PaletteWindowClass, null); hwnd != 0; hwnd = FindWindowEx(0, hwnd, PaletteWindowClass, null))
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (hostIds.Contains(pid) && IsShownPalette(hwnd))
            {
                if (found != 0)
                {
                    // More than one candidate: Command Palette changed in a way this does not know; leave it alone.
                    return 0;
                }

                found = hwnd;
            }
        }

        return found;
    }

    /// <summary>Where the palette stands right now; <paramref name="palette"/> is its window while shown.</summary>
    internal static PaletteState Check(HashSet<uint> hostIds, out nint palette)
    {
        palette = FindPalette(hostIds);
        if (palette == 0)
        {
            return PaletteState.NotShown;
        }

        var foreground = GetForegroundWindow();
        if (foreground == palette)
        {
            return PaletteState.InFront;
        }

        _ = GetWindowThreadProcessId(foreground, out var pid);
        return foreground != 0 && hostIds.Contains(pid) ? PaletteState.OtherHostWindowInFront : PaletteState.Behind;
    }

    /// <summary>Size of the INPUT passed to SendInput, which rejects any other size.</summary>
    internal static int InputSize => Marshal.SizeOf<Input>();

    private static async Task WatchAsync(long generation)
    {
        var sw = Stopwatch.StartNew();
        var activations = 0;
        try
        {
            var hostIds = HostProcessIds();
            while (sw.ElapsedMilliseconds < WatchMs && Interlocked.Read(ref _generation) == generation)
            {
                switch (Check(hostIds, out var palette))
                {
                    case PaletteState.InFront:
                        LastOutcome = activations == 0
                            ? "palette already in front"
                            : $"palette was behind another window; activated after {sw.ElapsedMilliseconds} ms ({activations} attempt(s))";
                        return;
                    case PaletteState.OtherHostWindowInFront:
                        LastOutcome = "another Command Palette window is in front";
                        return;
                    case PaletteState.Behind when activations < MaxActivations:
                        activations++;
                        Activate(palette);
                        break;
                }

                await Task.Delay(PollMs);
            }

            LastOutcome = activations == 0 ? "palette not shown" : $"palette stayed behind after {activations} attempt(s)";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastOutcome = $"check failed: {ex.Message}";
        }
    }

    private static void Activate(nint palette)
    {
        var input = default(Input); // INPUT_MOUSE with no flags: moves and clicks nothing
        _ = SendInput(1, in input, Marshal.SizeOf<Input>());
        _ = SetForegroundWindow(palette);
    }

    /// <summary>Visible and not DWM-cloaked (how the palette hides); enabled (unlike its toast), framed (unlike the dock), a tool window (unlike settings).</summary>
    private static bool IsShownPalette(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0))
        {
            return false;
        }

        long style = GetWindowLongPtr(hwnd, GwlStyle);
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        return (style & WsDisabled) == 0 && (style & WsThickFrame) != 0 && (exStyle & WsExToolWindow) != 0;
    }

    internal enum PaletteState
    {
        NotShown,
        InFront,
        OtherHostWindowInFront,
        Behind,
    }

    /// <summary>INPUT; the union member is MOUSEINPUT, whose pointer-sized field puts it at offset 8 (40 bytes in all).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, in Input input, int size);
}
