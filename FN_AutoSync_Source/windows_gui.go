//go:build windows

package main

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"
)

var (
	user32                   = syscall.NewLazyDLL("user32.dll")
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	gdi32                    = syscall.NewLazyDLL("gdi32.dll")
	shell32                  = syscall.NewLazyDLL("shell32.dll")
	ole32                    = syscall.NewLazyDLL("ole32.dll")
	procRegisterClassExW     = user32.NewProc("RegisterClassExW")
	procCreateWindowExW      = user32.NewProc("CreateWindowExW")
	procDefWindowProcW       = user32.NewProc("DefWindowProcW")
	procShowWindow           = user32.NewProc("ShowWindow")
	procUpdateWindow         = user32.NewProc("UpdateWindow")
	procGetMessageW          = user32.NewProc("GetMessageW")
	procTranslateMessage     = user32.NewProc("TranslateMessage")
	procDispatchMessageW     = user32.NewProc("DispatchMessageW")
	procDestroyWindow        = user32.NewProc("DestroyWindow")
	procPostQuitMessage      = user32.NewProc("PostQuitMessage")
	procSetWindowTextW       = user32.NewProc("SetWindowTextW")
	procGetWindowTextW       = user32.NewProc("GetWindowTextW")
	procSendMessageW         = user32.NewProc("SendMessageW")
	procPostMessageW         = user32.NewProc("PostMessageW")
	procMessageBoxW          = user32.NewProc("MessageBoxW")
	procGetModuleHandleW     = kernel32.NewProc("GetModuleHandleW")
	procLoadCursorW          = user32.NewProc("LoadCursorW")
	procGetStockObject       = gdi32.NewProc("GetStockObject")
	procSetTimer             = user32.NewProc("SetTimer")
	procKillTimer            = user32.NewProc("KillTimer")
	procSHBrowseForFolderW   = shell32.NewProc("SHBrowseForFolderW")
	procSHGetPathFromIDListW = shell32.NewProc("SHGetPathFromIDListW")
	procCoTaskMemFree        = ole32.NewProc("CoTaskMemFree")
	procSetFocus             = user32.NewProc("SetFocus")
)

const (
	wsOverlappedWindow = 0x00CF0000
	wsVisible          = 0x10000000
	wsChild            = 0x40000000
	wsBorder           = 0x00800000
	wsVScroll          = 0x00200000
	wsTabStop          = 0x00010000
	wsExClientEdge     = 0x00000200
	esAutoHScroll      = 0x0080
	esMultiLine        = 0x0004
	esReadOnly         = 0x0800
	esAutoVScroll      = 0x0040
	bsPushButton       = 0x00000000
	swShow             = 5
	swMinimize         = 6
	wmCreate           = 0x0001
	wmDestroy          = 0x0002
	wmCommand          = 0x0111
	wmClose            = 0x0010
	wmTimer            = 0x0113
	wmSetFont          = 0x0030
	wmAppRefresh       = 0x8001
	wmAppFinish        = 0x8002
	wmSetText          = 0x000C
	mbOk               = 0
	mbYesNo            = 0x0004
	mbIconQuestion     = 0x0020
	idYes              = 6
	idSrc              = 101
	idNas              = 102
	idBackup           = 103
	idAuto             = 104
	idRestore          = 105
	idStartup          = 106
	idLog              = 107
	idStatus           = 108
	idSourceEdit       = 109
	idNasEdit          = 110
	appTimer           = 1
	timerMs            = 60 * 1000
)

type wndClassEx struct {
	Size       uint32
	Style      uint32
	WndProc    uintptr
	ClsExtra   int32
	WndExtra   int32
	Instance   uintptr
	Icon       uintptr
	Cursor     uintptr
	Background uintptr
	MenuName   *uint16
	ClassName  *uint16
	IconSmall  uintptr
}
type msgStruct struct {
	Hwnd     uintptr
	Message  uint32
	WParam   uintptr
	LParam   uintptr
	Time     uint32
	PtX      int32
	PtY      int32
	LPrivate uint32
}
type browseInfo struct {
	Owner       uintptr
	Root        uintptr
	DisplayName *uint16
	Title       *uint16
	Flags       uint32
	Callback    uintptr
	LParam      uintptr
	Image       int32
}
type config struct {
	Source  string `json:"source"`
	NAS     string `json:"nas"`
	Startup bool   `json:"startup"`
}

var (
	mainWnd, sourceEdit, nasEdit, logEdit, statusLabel, autoButton, startupButton uintptr
	globalCfg                                                                     config
	logsMu                                                                        sync.Mutex
	logText                                                                       = "准备就绪。\r\n"
	isRunning                                                                     atomic.Bool
	autoEnabled                                                                   atomic.Bool
	showErrLock                                                                   sync.Mutex
	lastStatus                                                                    = "请选择本地资料文件夹与 NAS 共享文件夹，然后点击开始同步。"
	startAutoRequested                                                            = false
)

func ptr(s string) *uint16 { p, _ := syscall.UTF16PtrFromString(s); return p }
func callCreate(class, name string, style, exstyle uint32, x, y, w, h int32, parent, id uintptr) uintptr {
	hinst, _, _ := procGetModuleHandleW.Call(0)
	r, _, _ := procCreateWindowExW.Call(uintptr(exstyle), uintptr(unsafe.Pointer(ptr(class))), uintptr(unsafe.Pointer(ptr(name))), uintptr(style), uintptr(x), uintptr(y), uintptr(w), uintptr(h), parent, id, hinst, 0)
	if r == 0 {
		panic(fmt.Sprintf("创建窗口失败：%s %s", class, name))
	}
	font, _, _ := procGetStockObject.Call(17)
	procSendMessageW.Call(r, wmSetFont, font, 1)
	return r
}
func label(s string, x, y, w, h int32) uintptr {
	return callCreate("STATIC", s, wsChild|wsVisible, xStyle(), x, y, w, h, mainWnd, 0)
}
func xStyle() uint32 { return 0 }
func edit(s string, x, y, w, h int32, id uintptr, multi bool) uintptr {
	st := uint32(wsChild | wsVisible | wsBorder | wsTabStop)
	if multi {
		st |= esMultiLine | esAutoVScroll | esReadOnly | wsVScroll
	} else {
		st |= esAutoHScroll
	}
	return callCreate("EDIT", s, st, wsExClientEdge, x, y, w, h, mainWnd, id)
}
func button(s string, x, y, w, h int32, id uintptr) uintptr {
	return callCreate("BUTTON", s, wsChild|wsVisible|wsTabStop|bsPushButton, 0, x, y, w, h, mainWnd, id)
}
func getText(hwnd uintptr) string {
	buf := make([]uint16, 32768)
	procGetWindowTextW.Call(hwnd, uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
	return syscall.UTF16ToString(buf)
}
func setText(hwnd uintptr, s string) { procSetWindowTextW.Call(hwnd, uintptr(unsafe.Pointer(ptr(s)))) }
func notify(s string) {
	procMessageBoxW.Call(mainWnd, uintptr(unsafe.Pointer(ptr(s))), uintptr(unsafe.Pointer(ptr("飞牛长文件名自动同步"))), mbOk)
}
func confirm(s string) bool {
	r, _, _ := procMessageBoxW.Call(mainWnd, uintptr(unsafe.Pointer(ptr(s))), uintptr(unsafe.Pointer(ptr("请确认"))), mbYesNo|mbIconQuestion)
	return r == idYes
}
func chooseFolder(title string) string {
	display := make([]uint16, 512)
	b := browseInfo{Owner: mainWnd, DisplayName: &display[0], Title: ptr(title), Flags: 0x0040 | 0x0001}
	pidl, _, _ := procSHBrowseForFolderW.Call(uintptr(unsafe.Pointer(&b)))
	if pidl == 0 {
		return ""
	}
	defer procCoTaskMemFree.Call(pidl)
	path := make([]uint16, 32768)
	r, _, _ := procSHGetPathFromIDListW.Call(pidl, uintptr(unsafe.Pointer(&path[0])))
	if r == 0 {
		notify("无法读取该文件夹路径，建议直接在输入框填写 NAS 网络路径，例如 \\\\192.168.1.100\\资料备份")
		return ""
	}
	return syscall.UTF16ToString(path)
}
func dataDir() string {
	dir := filepath.Join(os.Getenv("APPDATA"), "FNLongNameAutoSync")
	_ = os.MkdirAll(dir, 0700)
	return dir
}
func configFile() string { return filepath.Join(dataDir(), "config.json") }
func loadConfig() {
	b, err := os.ReadFile(configFile())
	if err == nil {
		_ = json.Unmarshal(b, &globalCfg)
	}
}
func saveConfig() {
	globalCfg.Source = strings.TrimSpace(getText(sourceEdit))
	globalCfg.NAS = strings.TrimSpace(getText(nasEdit))
	b, _ := json.MarshalIndent(globalCfg, "", "  ")
	_ = os.WriteFile(configFile(), b, 0600)
}
func logLine(s string) {
	logsMu.Lock()
	if len(logText) > 28000 {
		logText = logText[len(logText)-18000:]
	}
	logText += time.Now().Format("15:04:05") + "  " + s + "\r\n"
	logsMu.Unlock()
	if mainWnd != 0 {
		procPostMessageW.Call(mainWnd, wmAppRefresh, 0, 0)
	}
}
func setStatus(s string) {
	showErrLock.Lock()
	lastStatus = s
	showErrLock.Unlock()
	if mainWnd != 0 {
		procPostMessageW.Call(mainWnd, wmAppRefresh, 0, 0)
	}
}
func refreshUi() {
	logsMu.Lock()
	s := logText
	logsMu.Unlock()
	setText(logEdit, s)
	showErrLock.Lock()
	st := lastStatus
	showErrLock.Unlock()
	setText(statusLabel, st)
	if autoEnabled.Load() {
		setText(autoButton, "停止自动同步")
	} else {
		setText(autoButton, "开启自动同步")
	}
	if globalCfg.Startup {
		setText(startupButton, "关闭开机启动")
	} else {
		setText(startupButton, "设置开机启动")
	}
}
func startBackup() {
	if !isRunning.CompareAndSwap(false, true) {
		return
	}
	source := strings.TrimSpace(getText(sourceEdit))
	nas := strings.TrimSpace(getText(nasEdit))
	saveConfig()
	setStatus("正在扫描并同步，请勿退出程序……")
	logLine("开始同步（仅上传；不会删除 NAS 旧文件）")
	go func() {
		stat, err := runBackup(source, nas, logLine)
		if err != nil {
			logLine("错误: " + err.Error())
			setStatus("同步失败：" + err.Error())
		} else if stat.Failed > 0 {
			setStatus(fmt.Sprintf("同步完成，但有 %d 个文件失败；请查看下方日志", stat.Failed))
		} else {
			setStatus(fmt.Sprintf("同步成功：新传 %d，跳过未变化 %d，普通名称不改", stat.Uploaded, stat.Unchanged))
		}
		isRunning.Store(false)
		procPostMessageW.Call(mainWnd, wmAppFinish, 0, 0)
	}()
}
func startRestore() {
	if isRunning.Load() {
		notify("当前有同步任务，请在结束后恢复。")
		return
	}
	nas := strings.TrimSpace(getText(nasEdit))
	if nas == "" {
		notify("请先选择 NAS 共享文件夹。")
		return
	}
	target := chooseFolder("选择恢复到电脑的文件夹（不会覆盖不同内容的同名文件）")
	if target == "" {
		return
	}
	if !confirm("将从 NAS 读取备份映射并还原完整原文件名。\n\nNAS 目录：" + nas + "\n目标目录：" + target + "\n\n同名不同内容的文件不会覆盖。是否继续？") {
		return
	}
	if !isRunning.CompareAndSwap(false, true) {
		return
	}
	setStatus("正在恢复并校验 SHA-256……")
	logLine("开始恢复到: " + target)
	go func() {
		stat, err := runRestore(nas, target, logLine)
		if err != nil {
			logLine("恢复失败: " + err.Error())
			setStatus("恢复失败: " + err.Error())
		} else {
			setStatus(fmt.Sprintf("恢复完成：成功 %d，已存在 %d，冲突 %d，失败 %d", stat.Restored, stat.Unchanged, stat.Conflicts, stat.Failed))
		}
		isRunning.Store(false)
		procPostMessageW.Call(mainWnd, wmAppFinish, 0, 0)
	}()
}
func startupPath() string { exe, _ := os.Executable(); return "\"" + exe + "\" -auto" }
func startupToggle() {
	key := `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
	if globalCfg.Startup {
		out, err := exec.Command("reg.exe", "delete", key, "/v", "FNLongNameAutoSync", "/f").CombinedOutput()
		if err != nil {
			notify("关闭开机启动失败：" + string(out))
			return
		}
		globalCfg.Startup = false
		logLine("已关闭开机启动")
	} else {
		out, err := exec.Command("reg.exe", "add", key, "/v", "FNLongNameAutoSync", "/t", "REG_SZ", "/d", startupPath(), "/f").CombinedOutput()
		if err != nil {
			notify("设置开机启动失败：" + string(out))
			return
		}
		globalCfg.Startup = true
		logLine("已设置登录 Windows 后自动运行并同步")
	}
	saveConfig()
	refreshUi()
}
func wndProc(hwnd uintptr, msg uint32, wparam, lparam uintptr) uintptr {
	switch msg {
	case wmCreate:
		mainWnd = hwnd
		label("飞牛长文件名自动同步  ·  普通文件保留原名，超长名称自动映射", 18, 15, 835, 25)
		label("电脑源文件夹", 18, 55, 200, 20)
		sourceEdit = edit(globalCfg.Source, 18, 80, 680, 30, idSourceEdit, false)
		button("浏览…", 710, 80, 118, 30, idSrc)
		label("NAS 共享文件夹（Windows 可访问的 SMB 路径或映射盘）", 18, 126, 680, 20)
		nasEdit = edit(globalCfg.NAS, 18, 151, 680, 30, idNasEdit, false)
		button("浏览…", 710, 151, 118, 30, idNas)
		button("立即同步一次", 18, 207, 166, 38, idBackup)
		autoButton = button("开启自动同步", 195, 207, 170, 38, idAuto)
		button("原名恢复", 377, 207, 148, 38, idRestore)
		startupButton = button("设置开机启动", 538, 207, 166, 38, idStartup)
		label("自动同步间隔约 1 分钟；只上传新增/修改文件；不会删除 NAS 端旧文件。", 18, 265, 820, 20)
		label("请关闭此目录原有的飞牛同步任务，避免它继续对超长名称报错。", 18, 288, 820, 20)
		statusLabel = label(lastStatus, 18, 321, 820, 42)
		logEdit = edit(logText, 18, 370, 810, 245, idLog, true)
		procSetTimer.Call(hwnd, appTimer, timerMs, 0)
		if startAutoRequested && globalCfg.Source != "" && globalCfg.NAS != "" {
			autoEnabled.Store(true)
			startBackup()
		}
		refreshUi()
		return 0
	case wmCommand:
		switch uint16(wparam & 0xffff) {
		case idSrc:
			if dir := chooseFolder("选择 Windows 原始资料文件夹"); dir != "" {
				setText(sourceEdit, dir)
				saveConfig()
			}
		case idNas:
			if dir := chooseFolder("选择 NAS 已映射或网络共享文件夹"); dir != "" {
				setText(nasEdit, dir)
				saveConfig()
			}
		case idBackup:
			startBackup()
		case idAuto:
			if autoEnabled.Load() {
				autoEnabled.Store(false)
				logLine("已停止自动同步（正在运行的任务会正常完成）")
				setStatus("自动同步已关闭。")
			} else {
				autoEnabled.Store(true)
				logLine("自动同步已开启；程序运行期间每分钟检查一次")
				startBackup()
			}
			refreshUi()
		case idRestore:
			startRestore()
		case idStartup:
			startupToggle()
		}
		return 0
	case wmTimer:
		if wparam == appTimer && autoEnabled.Load() && !isRunning.Load() {
			startBackup()
		}
		return 0
	case wmAppRefresh, wmAppFinish:
		refreshUi()
		return 0
	case wmClose:
		if isRunning.Load() {
			notify("文件正在复制或恢复。请完成当前任务后再退出，避免中断传输。")
			return 0
		}
		procDestroyWindow.Call(hwnd)
		return 0
	case wmDestroy:
		procKillTimer.Call(hwnd, appTimer)
		procPostQuitMessage.Call(0)
		return 0
	}
	r, _, _ := procDefWindowProcW.Call(hwnd, uintptr(msg), wparam, lparam)
	return r
}
func main() {
	loadConfig()
	for _, arg := range os.Args[1:] {
		if arg == "-auto" {
			startAutoRequested = true
		}
	}
	hinst, _, _ := procGetModuleHandleW.Call(0)
	cname := ptr("FNLongNameAutoSyncWindow")
	cursor, _, _ := procLoadCursorW.Call(0, 32512)
	c := wndClassEx{Size: uint32(unsafe.Sizeof(wndClassEx{})), WndProc: syscall.NewCallback(wndProc), Instance: hinst, Cursor: cursor, Background: 16, ClassName: cname}
	atom, _, err := procRegisterClassExW.Call(uintptr(unsafe.Pointer(&c)))
	if atom == 0 {
		panic(fmt.Sprintf("注册窗口失败: %v", err))
	}
	hwnd, _, err := procCreateWindowExW.Call(0, uintptr(unsafe.Pointer(cname)), uintptr(unsafe.Pointer(ptr("飞牛长文件名自动同步 - 独立 EXE"))), wsOverlappedWindow, 150, 80, 860, 680, 0, 0, hinst, 0)
	if hwnd == 0 {
		panic(fmt.Sprintf("创建主窗口失败: %v", err))
	}
	procShowWindow.Call(hwnd, swShow)
	procUpdateWindow.Call(hwnd)
	var message msgStruct
	for {
		n, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&message)), 0, 0, 0)
		if int32(n) <= 0 {
			break
		}
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&message)))
		procDispatchMessageW.Call(uintptr(unsafe.Pointer(&message)))
	}
}
