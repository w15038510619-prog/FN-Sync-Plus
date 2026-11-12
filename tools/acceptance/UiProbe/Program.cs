using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UiProbe;

/// <summary>
/// Acceptance helper: finds the main window of a process, optionally resizes it, and saves a PNG
/// of exactly that window plus a JSON record (rect, DPI, title). Windows that are hidden, minimised
/// or zero-sized are reported as missing so the harness can fail instead of silently producing an
/// empty screenshot.
/// </summary>
internal static class Program
{
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNotTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmMouseWheel = 0x020A;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extraInfo);

    private const uint PwRenderFullContent = 0x00000002;
    private const uint MouseEventWheel = 0x0800;

    /// <summary>
    /// True when the two frames are effectively identical (99% of sampled pixels equal within a
    /// small tolerance). A wheel scroll that did not reach the target window leaves the page
    /// untouched, and the harness must not save that frame as "the scrolled section".
    /// </summary>
    private static bool NearlyIdentical(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        int total = 0;
        int different = 0;
        for (int y = 0; y < a.Height; y += 6)
        {
            for (int x = 0; x < a.Width; x += 6)
            {
                total++;
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                if (Math.Abs(pa.R - pb.R) > 4 || Math.Abs(pa.G - pb.G) > 4 || Math.Abs(pa.B - pb.B) > 4)
                {
                    different++;
                }
            }
        }
        return total == 0 || different <= total / 100;
    }

    /// <summary>
    /// Scrolls the window's content with REAL wheel input (the pointer is parked over the window
    /// centre for the duration and restored afterwards). WinUI routes wheel messages by pointer
    /// position, so synthetic PostMessage scrolling is unreliable; this is the deterministic way
    /// to reach a section that starts below the fold.
    /// </summary>
    private static void ScrollWindow(IntPtr hwnd, int notches)
    {
        if (notches == 0) return;
        // Wheel input only reaches the window that owns the pointer: bring it to the front and
        // move it fully on-screen first (a background window, or one whose centre is covered by
        // the terminal, silently ignores the wheel and the page never moves).
        ShowWindow(hwnd, SwRestore);
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoSize | SwpShowWindow);
        SetForegroundWindow(hwnd);
        Thread.Sleep(250);
        if (!GetWindowRect(hwnd, out Rect rect)) return;
        int centerX = rect.Left + (rect.Right - rect.Left) / 2;
        int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;
        GetCursorPos(out var original);
        SetCursorPos(centerX, centerY);
        Thread.Sleep(200);
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            int delta = notches > 0 ? -120 : 120;
            // Physical wheel first (exercises the real input pipeline when the window holds the
            // pointer focus)...
            mouse_event(MouseEventWheel, 0, 0, delta, UIntPtr.Zero);
            // ...and the same wheel as a window message: wheel input is delivered to the focused
            // window, so a background app would silently ignore it. SendMessage targets this hwnd
            // and carries the pointer position for WinUI's hit test.
            IntPtr wheelWParam = (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000));
            IntPtr wheelLParam = (IntPtr)((centerY << 16) | (centerX & 0xFFFF));
            SendMessage(hwnd, WmMouseWheel, wheelWParam, wheelLParam);
            Thread.Sleep(140);
        }
        Thread.Sleep(600);
        SetCursorPos(original.X, original.Y);
        // Leave the window where the rest of the probe expects it (not always-on-top).
        SetWindowPos(hwnd, HwndNotTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
    }

    private static bool PrintWindowBitmap(IntPtr hwnd, Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc();
        try
        {
            return PrintWindow(hwnd, hdc, PwRenderFullContent);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    private static int Main(string[] args)
    {
        var options = ParseArgs(args);
        var result = new Dictionary<string, object?>();
        try
        {
            int pid = options.Pid;
            if (pid <= 0)
            {
                var process = Process.GetProcessesByName(options.ProcessName ?? "FNSyncPlus.WinUI")
                    .OrderByDescending(p => p.StartTime)
                    .FirstOrDefault();
                if (process == null) return Fail(result, options, "找不到进程");
                pid = process.Id;
            }

            IntPtr hwnd = IntPtr.Zero;
            var deadline = DateTime.UtcNow.AddSeconds(options.WaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindow(pid, options.TitleMatch);
                if (hwnd != IntPtr.Zero) break;
                Thread.Sleep(400);
            }
            if (hwnd == IntPtr.Zero) return Fail(result, options, "在等待时间内没有找到可见主窗口");

            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            Thread.Sleep(600);

            if (options.Width > 0 && options.Height > 0)
            {
                // SWP_NOZORDER only: the caller wants a real resize at the given DPI.
                SetWindowPos(hwnd, IntPtr.Zero, 60, 60, options.Width, options.Height, SwpNoZOrder);
                Thread.Sleep(700);
            }

            if (!GetWindowRect(hwnd, out Rect rect)) return Fail(result, options, "GetWindowRect 失败");
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return Fail(result, options, $"窗口尺寸无效 {width}x{height}");

            uint dpi = GetDpiForWindow(hwnd);
            var title = new StringBuilder(GetWindowTextLength(hwnd) + 2);
            GetWindowText(hwnd, title, title.Capacity);

            if (options.ScrollNotches != 0)
            {
                // Capture a reference frame, then verify the wheel really moved the page. A
                // previous run saved an identical "scrolled" screenshot because the wheel went to
                // the terminal window; the harness must detect that instead of accepting it.
                using var before = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                PrintWindowBitmap(hwnd, before);
                bool effective = false;
                int attempt = 0;
                int notches = options.ScrollNotches;
                while (attempt < 3 && !effective)
                {
                    attempt++;
                    ScrollWindow(hwnd, notches);
                    Thread.Sleep(350);
                    using var after = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    PrintWindowBitmap(hwnd, after);
                    if (!NearlyIdentical(before, after)) { effective = true; break; }
                    notches *= 2;
                }
                result["scroll_effective"] = effective;
                result["scroll_attempts"] = attempt;
            }

            if (options.Capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutPath))!);
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                // PrintWindow asks the window to render itself, so the shot is correct even when
                // another window (the terminal that started the harness) is on top. CopyFromScreen
                // is kept as a fallback for windows that refuse to render off-screen.
                bool printed = PrintWindowBitmap(hwnd, bitmap);
                double ratio = ContentRatio(bitmap);
                double coverage = ContentCoverage(bitmap);
                if (!printed || (ratio < 0.02 && coverage < 0.05))
                {
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(400);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                    ratio = ContentRatio(bitmap);
                    coverage = ContentCoverage(bitmap);
                    result["capture"] = "screen";
                }
                else
                {
                    result["capture"] = "printwindow";
                }
                bitmap.Save(options.OutPath, ImageFormat.Png);
                result["content_ratio"] = ratio;
                result["content_coverage"] = coverage;
                result["mean_luminance"] = MeanLuminance(bitmap);
            }

            result["ok"] = true;
            result["pid"] = pid;
            result["hwnd"] = hwnd.ToInt64();
            result["title"] = title.ToString();
            result["left"] = rect.Left;
            result["top"] = rect.Top;
            result["width"] = width;
            result["height"] = height;
            result["dpi"] = dpi;
            result["scale_percent"] = (int)Math.Round(dpi * 100.0 / 96.0);
            result["png"] = options.Capture ? Path.GetFullPath(options.OutPath) : null;
            if (options.Width > 0) { result["requested_width"] = options.Width; result["requested_height"] = options.Height; }
            WriteJson(options, result);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(result, options, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Fail(Dictionary<string, object?> result, Options options, string message)
    {
        result["ok"] = false;
        result["error"] = message;
        WriteJson(options, result);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 1;
    }

    private static void WriteJson(Options options, Dictionary<string, object?> result)
    {
        if (string.IsNullOrWhiteSpace(options.JsonPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonPath))!);
        File.WriteAllText(options.JsonPath, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static IntPtr FindMainWindow(int pid, string? titleMatch)
    {
        IntPtr found = IntPtr.Zero;
        int bestArea = -1;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            if (!string.IsNullOrWhiteSpace(titleMatch))
            {
                var text = new StringBuilder(length + 2);
                GetWindowText(hwnd, text, text.Capacity);
                if (!text.ToString().Contains(titleMatch, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (!GetWindowRect(hwnd, out Rect rect)) return true;
            int area = (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                found = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Share of distinct sampled colours. A window that has not painted its content yet is a flat
    /// surface (two or three tones at most) and scores near zero, which is how the harness avoids
    /// saving — and later "reviewing" — an empty screenshot.
    /// </summary>
    private static double ContentRatio(Bitmap bitmap)
    {
        var colours = new HashSet<int>();
        int sampled = 0;
        for (int y = 0; y < bitmap.Height; y += 6)
        {
            for (int x = 0; x < bitmap.Width; x += 6)
            {
                sampled++;
                var pixel = bitmap.GetPixel(x, y);
                // Quantise to 8 levels per channel so anti-aliasing noise is not counted as content.
                colours.Add(((pixel.R >> 5) << 10) | ((pixel.G >> 5) << 5) | (pixel.B >> 5));
            }
        }
        // 256 possible quantised colours; an unpainted window uses a handful, a real UI far more.
        return Math.Round(colours.Count / 256.0, 4);
    }

    /// <summary>
    /// Share of sampled pixels that differ from the dominant colour. Independent of the number of
    /// distinct shades, so a page rendered without subpixel anti-aliasing (or without Mica, when
    /// the window is not the active one) still scores as content, while a flat unpainted surface
    /// scores near zero.
    /// </summary>
    private static double ContentCoverage(Bitmap bitmap)
    {
        var counts = new Dictionary<int, int>();
        int sampled = 0;
        for (int y = 0; y < bitmap.Height; y += 6)
        {
            for (int x = 0; x < bitmap.Width; x += 6)
            {
                sampled++;
                var pixel = bitmap.GetPixel(x, y);
                int key = (pixel.R << 16) | (pixel.G << 8) | pixel.B;
                counts.TryGetValue(key, out int seen);
                counts[key] = seen + 1;
            }
        }
        if (sampled == 0 || counts.Count == 0) return 0;
        int dominant = 0;
        foreach (var pair in counts) { if (pair.Value > dominant) dominant = pair.Value; }
        return Math.Round(1.0 - (double)dominant / sampled, 4);
    }

    /// <summary>Average luminance (0-255) so the harness can tell a dark theme from a light one.</summary>
    private static int MeanLuminance(Bitmap bitmap)
    {
        long total = 0;
        int sampled = 0;
        for (int y = 0; y < bitmap.Height; y += 6)
        {
            for (int x = 0; x < bitmap.Width; x += 6)
            {
                var pixel = bitmap.GetPixel(x, y);
                total += (299L * pixel.R + 587L * pixel.G + 114L * pixel.B) / 1000;
                sampled++;
            }
        }
        return sampled == 0 ? 0 : (int)(total / sampled);
    }

    private sealed class Options
    {
        public int Pid { get; init; }
        public string? ProcessName { get; init; }
        public string OutPath { get; init; } = "window.png";
        public string? JsonPath { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int WaitSeconds { get; init; } = 20;
        public string? TitleMatch { get; init; }
        public bool Capture { get; init; } = true;
        public int ScrollNotches { get; init; }
    }

    private static Options ParseArgs(string[] args)
    {
        int pid = 0, width = 0, height = 0, wait = 20;
        string? name = null, json = null, titleMatch = null;
        string output = "window.png";
        bool capture = true;
        int scrollNotches = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length: pid = int.Parse(args[++i]); break;
                case "--process" when i + 1 < args.Length: name = args[++i]; break;
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                case "--json" when i + 1 < args.Length: json = args[++i]; break;
                case "--size" when i + 1 < args.Length:
                    var parts = args[++i].Split('x');
                    width = int.Parse(parts[0]);
                    height = int.Parse(parts[1]);
                    break;
                case "--wait" when i + 1 < args.Length: wait = int.Parse(args[++i]); break;
                case "--title-match" when i + 1 < args.Length: titleMatch = args[++i]; break;
                case "--no-capture": capture = false; break;
                case "--scroll" when i + 1 < args.Length: scrollNotches = int.Parse(args[++i]); break;
            }
        }
        return new Options
        {
            Pid = pid,
            ProcessName = name,
            OutPath = output,
            JsonPath = json,
            Width = width,
            Height = height,
            WaitSeconds = wait,
            TitleMatch = titleMatch,
            Capture = capture,
            ScrollNotches = scrollNotches
        };
    }
}
