@echo off
chcp 65001 >nul
echo ===============================================================================
echo               飞牛同步增强版 (FN Sync Plus) 一键编译脚本
echo ===============================================================================

set "DOTNET_ROOT=D:\dotnet"
set "PATH=D:\dotnet;%PATH%"

where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] 未找到 dotnet 命令，请确保已安装 .NET 8 SDK。
    pause
    exit /b 1
)

echo [1/3] 编译核心库与自动化测试套件...
dotnet build FNSyncPlus.sln -c Release
if %errorlevel% neq 0 (
    echo [ERROR] 编译失败！
    pause
    exit /b 1
)

echo [2/3] 运行自动化测试套件...
dotnet run --project FNSyncPlus.Tests\FNSyncPlus.Tests.csproj -c Release
if %errorlevel% neq 0 (
    echo [ERROR] 测试未全部通过！
    pause
    exit /b 1
)

echo [3/3] 发布单文件独立可执行程序 (Self-Contained Single-File EXE)...
dotnet publish FNSyncPlus.App\FNSyncPlus.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "..\Release_Build"
if %errorlevel% neq 0 (
    echo [ERROR] 打包失败！
    pause
    exit /b 1
)

copy /y "..\Release_Build\FNSyncPlus.App.exe" "..\FN_Sync_Plus.exe" >nul
echo.
echo ===============================================================================
echo [SUCCESS] 编译打包完成！
echo 生成的独立程序: D:\FN_AutoSync_Package\FN_Sync_Plus.exe
echo ===============================================================================
pause
