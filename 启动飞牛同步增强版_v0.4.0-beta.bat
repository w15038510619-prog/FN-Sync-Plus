@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在启动 飞牛同步增强版 v0.4.0-beta (WinUI 3 原生桌面版)...
start "" "Release_WinUI\FNSyncPlus.WinUI.exe"
