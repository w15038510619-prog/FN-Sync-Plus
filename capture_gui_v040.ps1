$code = @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class WinFindCap {
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlink, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public const uint SRCCOPY = 0x00CC0020;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static IntPtr FindMainWindow(uint targetPid) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) => {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == targetPid) {
                StringBuilder sbClass = new StringBuilder(256);
                GetClassName(hWnd, sbClass, 256);
                if (sbClass.ToString() == "WinUIDesktopWin32WindowClass") {
                    result = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr CaptureHBitmap(IntPtr hWnd, int width, int height) {
        IntPtr hdcSrc = GetDC(hWnd);
        IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcSrc, width, height);
        IntPtr hOld = SelectObject(hdcDest, hBitmap);

        bool pwSuccess = PrintWindow(hWnd, hdcDest, PW_RENDERFULLCONTENT);
        if (!pwSuccess) {
            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, SRCCOPY);
        }

        SelectObject(hdcDest, hOld);
        DeleteDC(hdcDest);
        ReleaseDC(hWnd, hdcSrc);
        return hBitmap;
    }
}
"@

Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing

$exePath = "D:\FN_AutoSync_Package\Release_WinUI\FNSyncPlus.WinUI.exe"
$outDir = "C:\Users\S\.gemini\antigravity\brain\eacd3aa8-c774-45a0-b5d4-d23d66a0eaa6"

$views = @(
    @{ View="Tasks"; Step=0; File="gui_v0.4.0_dashboard.png"; WaitSec=4 },
    @{ View="AddFolder"; Step=0; File="gui_v0.4.0_wizard_step1.png"; WaitSec=4 },
    @{ View="AddFolder"; Step=2; File="gui_v0.4.0_wizard_step2_nas_browser.png"; WaitSec=4 },
    @{ View="AddFolder"; Step=3; File="gui_v0.4.0_wizard_step3_plan.png"; WaitSec=4 },
    @{ View="Device"; Step=0; File="gui_v0.4.0_device_connected.png"; WaitSec=4 },
    @{ View="TestLab"; Step=0; File="gui_v0.4.0_testlab_ready.png"; WaitSec=4 },
    @{ View="TestLabReport"; Step=0; File="gui_v0.4.0_testlab_report.png"; WaitSec=9 }
)

foreach ($v in $views) {
    Write-Host "Capturing $($v.File) (View: $($v.View), Step: $($v.Step))..."
    $argList = @("--view=$($v.View)")
    if ($v.Step -gt 0) {
        $argList += "--wizard-step=$($v.Step)"
    }

    $proc = Start-Process -FilePath $exePath -ArgumentList $argList -PassThru
    Start-Sleep -Seconds $v.WaitSec

    $pList = Get-Process -Name "FNSyncPlus.WinUI" -ErrorAction SilentlyContinue
    if ($pList -and $pList.Count -gt 0) {
        $actualPid = $pList[0].Id
        $hWnd = [WinFindCap]::FindMainWindow($actualPid)
        if ($hWnd -ne [IntPtr]::Zero) {
            [WinFindCap]::ShowWindow($hWnd, 9)
            [WinFindCap]::SetForegroundWindow($hWnd)
            Start-Sleep -Milliseconds 600

            $rect = New-Object WinFindCap+RECT
            [WinFindCap]::GetWindowRect($hWnd, [ref]$rect)
            $w = $rect.Right - $rect.Left
            $h = $rect.Bottom - $rect.Top

            if ($w -lt 400 -or $h -lt 300) {
                $w = 1200
                $h = 780
            }

            $hBmp = [WinFindCap]::CaptureHBitmap($hWnd, $w, $h)
            $bmp = [System.Drawing.Image]::FromHbitmap($hBmp)
            [WinFindCap]::DeleteObject($hBmp)

            $savePath = Join-Path $outDir $v.File
            $bmp.Save($savePath, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            $len = (Get-Item $savePath).Length
            Write-Host "SUCCESS: Saved $savePath ($w x $h, $len bytes)"
        } else {
            Write-Host "ERROR: WinUIDesktopWin32WindowClass not found for $($v.File)"
        }
    } else {
        Write-Host "ERROR: Process not running for $($v.File)"
    }

    Stop-Process -Name "FNSyncPlus.WinUI" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

Write-Host "All v0.4.0 captures completed successfully!"
