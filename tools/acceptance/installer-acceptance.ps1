<#
.SYNOPSIS
    Inno Setup acceptance: compile, install, upgrade, roll back, uninstall, reinstall.

.DESCRIPTION
    Uses the real installer produced from FNSyncPlus\installer\FNSyncPlus.iss and the real payloads
    built by build-release.ps1. Everything happens in a private directory under -WorkRoot; the
    per-user default location (%LOCALAPPDATA%\Programs\FNSyncPlus) is never touched.

    Assertions are behavioural, not cosmetic:
      * after install the launcher can actually resolve a version (this is where a pointer that
        still contains the build machine's absolute path used to make the program unlaunchable);
      * an upgrade keeps the previous payload directory and makes a rollback possible;
      * the uninstaller removes the program but leaves user configuration alone;
      * a reinstall after uninstall works.

.PARAMETER PayloadA
    Install root produced by build-release.ps1 for the older version (contains app-<v>, launcher,
    current.json).

.PARAMETER PayloadB
    Same, for the newer version.
#>
param(
    [Parameter(Mandatory = $true)][string]$PayloadA,
    [Parameter(Mandatory = $true)][string]$PayloadB,
    [string]$VersionA = '0.5.0',
    [string]$VersionB = '0.6.0',
    [string]$Iscc = '',
    [string]$WorkRoot = ''
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $repo ('.acceptance\installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (Test-Path -LiteralPath $WorkRoot) { throw "工作目录已存在：$WorkRoot" }
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($Iscc)) {
    $Iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
}
$iss = Join-Path $repo 'FNSyncPlus\installer\FNSyncPlus.iss'
$target = Join-Path $WorkRoot 'app'
$userConfig = Join-Path $env:LOCALAPPDATA 'FNSyncPlus'
$appId = '{4E1C9F62-5F3B-4B7E-9E1C-2B7B0C6D51A1}'

$script:results = New-Object System.Collections.ArrayList
function Record-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$script:results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $tag = 'FAIL'
    if ($Passed) { $tag = 'PASS' }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

function Run-Exe {
    param([string]$Path, [string[]]$Arguments)
    $output = & $Path @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = -1 }
    return [pscustomobject]@{ Code = $code; Output = $output }
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Get-Status {
    param([string]$Root)
    $launcher = Join-Path $Root 'FN_Sync_Plus.exe'
    if (-not (Test-Path -LiteralPath $launcher)) { return [pscustomobject]@{ Code = -1; Version = ''; Output = '缺少启动器' } }
    $run = Run-Exe -Path $launcher -Arguments @('--layout', $Root, 'status')
    $version = ''
    $text = $run.Output.Trim()
    if ($text -match '"current":"([^"]*)"') { $version = $Matches[1] }
    return [pscustomobject]@{ Code = $run.Code; Version = $version; Output = $text }
}

function Invoke-Compile {
    param([string]$Version, [string]$Payload, [string]$OutDir)
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $run = Run-Exe -Path $Iscc -Arguments @("/DAppVersion=$Version", "/DPayloadDir=$Payload", "/O$OutDir", $iss)
    $setup = Join-Path $OutDir "FN_Sync_Plus_${Version}_x64_setup.exe"
    return [pscustomobject]@{ Code = $run.Code; Setup = $setup; Output = $run.Output }
}

function Invoke-Setup {
    param([string]$Setup, [string]$LogName)
    $log = Join-Path $WorkRoot $LogName
    $process = Start-Process -FilePath $Setup -PassThru -Wait -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$target", '/TASKS=', "/LOG=$log")
    return $process.ExitCode
}

try {
    if (-not (Test-Path -LiteralPath $Iscc)) { throw "找不到 ISCC.exe：$Iscc" }
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadA 'current.json'))) { throw "载荷 A 无效：$PayloadA" }
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadB 'current.json'))) { throw "载荷 B 无效：$PayloadB" }

    $alreadyInstalled = Test-Path (Join-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" $appId)
    if ($alreadyInstalled) { throw "本机已存在同 AppId 的安装记录，为免破坏真实安装，放弃本次验收" }

    Write-Host ("安装验收目录：{0}" -f $WorkRoot)

    $compileA = Invoke-Compile -Version $VersionA -Payload $PayloadA -OutDir (Join-Path $WorkRoot 'setup-a')
    Record-Result -Name 'Inno Setup 编译 0.5.0 安装包' `
        -Passed ($compileA.Code -eq 0 -and (Test-Path $compileA.Setup)) `
        -Detail ("exit={0} {1}" -f $compileA.Code, $compileA.Setup)
    if (-not (Test-Path $compileA.Setup)) { throw "未生成安装包，终止后续步骤" }

    $exitA = Invoke-Setup -Setup $compileA.Setup -LogName 'install-a.log'
    $statusA = Get-Status -Root $target
    Record-Result -Name '静默安装 0.5.0 后可解析到版本' `
        -Passed ($exitA -eq 0 -and $statusA.Code -eq 0 -and $statusA.Version -eq $VersionA) `
        -Detail ("setupExit={0} status={1} version={2}" -f $exitA, $statusA.Code, $statusA.Version)

    $reportA = Read-JsonFile -Path (Join-Path $target ".health\report-$VersionA.json")
    Record-Result -Name '安装目录内的启动器可正常启动（指针已指向本机目录）' `
        -Passed ((Test-Path (Join-Path $target "app-$VersionA\FNSyncPlus.WinUI.exe")) -and $statusA.Code -eq 0) `
        -Detail ("app-{0} 存在，status={1}" -f $VersionA, $statusA.Code)

    $compileB = Invoke-Compile -Version $VersionB -Payload $PayloadB -OutDir (Join-Path $WorkRoot 'setup-b')
    Record-Result -Name 'Inno Setup 编译 0.6.0 安装包' -Passed ($compileB.Code -eq 0 -and (Test-Path $compileB.Setup)) `
        -Detail ("exit={0}" -f $compileB.Code)

    $exitB = Invoke-Setup -Setup $compileB.Setup -LogName 'install-b.log'
    $statusB = Get-Status -Root $target
    Record-Result -Name '覆盖安装升级到 0.6.0' `
        -Passed ($exitB -eq 0 -and $statusB.Code -eq 0 -and $statusB.Version -eq $VersionB) `
        -Detail ("setupExit={0} status={1} version={2}" -f $exitB, $statusB.Code, $statusB.Version)

    $keptOld = Test-Path (Join-Path $target "app-$VersionA\FNSyncPlus.WinUI.exe")
    Record-Result -Name '升级保留旧版本目录（可回滚）' -Passed $keptOld `
        -Detail ("app-{0} 保留={1}" -f $VersionA, $keptOld)

    $previous = Read-JsonFile -Path (Join-Path $target 'previous.json')
    Record-Result -Name '升级后写入 previous.json 指向旧版本' `
        -Passed ($null -ne $previous -and $previous.version -eq $VersionA) `
        -Detail ("previous={0}" -f ($(if ($null -eq $previous) { 'null' } else { $previous.version })))

    $launcher = Join-Path $target 'FN_Sync_Plus.exe'
    $rollback = Run-Exe -Path $launcher -Arguments @('--layout', $target, '--rollback', '--no-launch')
    $afterRollback = Get-Status -Root $target
    Record-Result -Name '安装升级后可回滚到 0.5.0' `
        -Passed ($rollback.Code -eq 0 -and $afterRollback.Version -eq $VersionA) `
        -Detail ("exit={0} version={1} {2}" -f $rollback.Code, $afterRollback.Version, ($rollback.Output.Trim() -split "`r?`n")[0])

    $uninstaller = Join-Path $target 'unins000.exe'
    $uninstallExit = -1
    if (Test-Path -LiteralPath $uninstaller) {
        $p = Start-Process -FilePath $uninstaller -PassThru -Wait -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $WorkRoot 'uninstall.log')")
        $uninstallExit = $p.ExitCode
    }
    $launcherGone = -not (Test-Path -LiteralPath $launcher)
    Record-Result -Name '卸载移除程序目录中的启动器' -Passed ($uninstallExit -eq 0 -and $launcherGone) `
        -Detail ("exit={0} launcherGone={1}" -f $uninstallExit, $launcherGone)

    Record-Result -Name '卸载保留用户配置目录' -Passed (Test-Path -LiteralPath $userConfig) `
        -Detail ("{0} 存在={1}" -f $userConfig, (Test-Path -LiteralPath $userConfig))

    Record-Result -Name '卸载清除注册表卸载项' `
        -Passed (-not (Test-Path (Join-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" $appId))) `
        -Detail 'HKCU 卸载项已移除'

    $reinstallExit = Invoke-Setup -Setup $compileB.Setup -LogName 'reinstall-b.log'
    $statusC = Get-Status -Root $target
    Record-Result -Name '卸载后可重新安装同一版本' `
        -Passed ($reinstallExit -eq 0 -and $statusC.Code -eq 0 -and $statusC.Version -eq $VersionB) `
        -Detail ("setupExit={0} status={1} version={2}" -f $reinstallExit, $statusC.Code, $statusC.Version)

    if (Test-Path -LiteralPath (Join-Path $target 'unins000.exe')) {
        $p = Start-Process -FilePath (Join-Path $target 'unins000.exe') -PassThru -Wait -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        Record-Result -Name '收尾：再次卸载' -Passed ($p.ExitCode -eq 0) -Detail ("exit={0}" -f $p.ExitCode)
    }
}
finally {
    $passed = @($script:results | Where-Object { $_.passed }).Count
    $failed = @($script:results | Where-Object { -not $_.passed }).Count
    Write-Host ''
    Write-Host ("合计：PASS {0} / FAIL {1}（共 {2} 项）" -f $passed, $failed, $script:results.Count)
    $summaryPath = Join-Path $WorkRoot 'installer-result.json'
    @{ workRoot = $WorkRoot; passed = $passed; failed = $failed; total = $script:results.Count
       cases = $script:results } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host ("结果文件：{0}" -f $summaryPath)
    if ($failed -gt 0) { exit 1 }
}
