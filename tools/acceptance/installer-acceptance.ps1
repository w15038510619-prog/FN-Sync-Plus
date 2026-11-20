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
    [string]$WorkRoot = '',
    [switch]$VerifyUi,
    [string]$ExistingSetupA = '',
    [string]$ProfileSource = ''
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
$userConfig = Join-Path $WorkRoot 'retained-profile'
New-Item -ItemType Directory -Path $userConfig | Out-Null
$profileFile = Join-Path $userConfig 'settings.json'
'{"task_name":"保留测试任务","auto_sync":false}' | Set-Content -LiteralPath $profileFile -Encoding utf8
$profileHash = (Get-FileHash -LiteralPath $profileFile -Algorithm SHA256).Hash
$appId = 'FNSyncPlus.InstallerAcceptance_is1'

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
    $run = Run-Exe -Path $Iscc -Arguments @('/DAcceptanceInstaller=1', "/DAppVersion=$Version", "/DPayloadDir=$Payload", "/O$OutDir", $iss)
    $setup = Join-Path $OutDir "FN_Sync_Plus_${Version}_x64_setup.exe"
    return [pscustomobject]@{ Code = $run.Code; Setup = $setup; Output = $run.Output }
}

function Invoke-Setup {
    param([string]$Setup, [string]$LogName)
    $log = Join-Path $WorkRoot $LogName
    $process = Start-Process -FilePath $Setup -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="{0}"' -f $target), '/TASKS=desktopicon', ('/LOG="{0}"' -f $log))
    return $process.ExitCode
}

# Optional real-profile copy: disable execution in the copy, retain task identifiers/targets,
# and prove BOTH installed binaries actually load it. Never write the original profile.
$probeRoot = Join-Path $WorkRoot 'profile-runtime'
$probeConfig = Join-Path $probeRoot 'local\FNSyncPlus\versioned-beta\settings.json'
$probeCredential = Join-Path (Split-Path -Parent $probeConfig) 'credentials.dat'
if ($ProfileSource) {
    $fixture = Get-Content -LiteralPath $ProfileSource -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    $fixture.RememberCredentials = $false
    $fixture.StartWithWindows = $false
    foreach ($task in $fixture.Tasks) {
        $task.enabled = $false
        $task.auto_sync_enabled = $false
        $task.realtime_watch_enabled = $false
        $task.is_user_paused = $true
    }
    $expectedTasks = @($fixture.Tasks).Count
    New-Item -ItemType Directory -Path (Split-Path -Parent $probeConfig) -Force | Out-Null
    $fixture | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $probeConfig -Encoding utf8
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('acceptance-only credential fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($probeCredential,$protected)
    $configBefore = (Get-FileHash -LiteralPath $probeConfig).Hash
    $credentialBefore = (Get-FileHash -LiteralPath $probeCredential).Hash
}
function Test-InstalledProfile([string]$Version) {
    if (!$ProfileSource) { return }
    $originalRoot = $env:FNSYNC_DATA_ROOT
    try {
        $env:FNSYNC_DATA_ROOT = $probeRoot
        $app = Join-Path $target "app-$Version\FNSyncPlus.WinUI.exe"
        $process = Start-Process -FilePath $app -ArgumentList '--update-health-check' -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(45000)) { throw 'Isolated installed profile probe did not exit.' }
        $healthPath = Join-Path $target ".health\report-$Version.json"
        $health = Get-Content -LiteralPath $healthPath -Raw -Encoding utf8 | ConvertFrom-Json
        Record-Result -Name "$Version 安装的真实程序加载沿用配置" -Passed ($health.usable -and $health.task_count -eq $expectedTasks) -Detail "tasks=$($health.task_count) expected=$expectedTasks usable=$($health.usable)"
        Record-Result -Name "$Version 配置及目标路径逐字节保留" -Passed ((Get-FileHash -LiteralPath $probeConfig).Hash -ceq $configBefore) -Detail 'SHA-256 before/after identical'
        $clear = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($probeCredential),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        Record-Result -Name "$Version DPAPI 测试数据文件保留且可解密" -Passed (((Get-FileHash -LiteralPath $probeCredential).Hash -ceq $credentialBefore) -and [Text.Encoding]::UTF8.GetString($clear) -ceq 'acceptance-only credential fixture') -Detail 'Synthetic DPAPI fixture; no production credential copied'
    } finally { $env:FNSYNC_DATA_ROOT = $originalRoot }
}

try {
    if (-not (Test-Path -LiteralPath $Iscc)) { throw "找不到 ISCC.exe：$Iscc" }
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadA 'current.json'))) { throw "载荷 A 无效：$PayloadA" }
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadB 'current.json'))) { throw "载荷 B 无效：$PayloadB" }

    $alreadyInstalled = Test-Path (Join-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" $appId)
    if ($alreadyInstalled) { throw "本机已存在同 AppId 的安装记录，为免破坏真实安装，放弃本次验收" }

    Write-Host ("安装验收目录：{0}" -f $WorkRoot)

    $compileA = if ($ExistingSetupA) {
        [pscustomobject]@{ Code = 0; Setup = (Resolve-Path -LiteralPath $ExistingSetupA).Path; Output = 'Reusing baseline installer' }
    } else {
        Invoke-Compile -Version $VersionA -Payload $PayloadA -OutDir (Join-Path $WorkRoot 'setup-a')
    }
    Record-Result -Name "Inno Setup 编译 $VersionA 安装包" `
        -Passed ($compileA.Code -eq 0 -and (Test-Path $compileA.Setup)) `
        -Detail ("exit={0} {1}" -f $compileA.Code, $compileA.Setup)
    if (-not (Test-Path $compileA.Setup)) { throw "未生成安装包，终止后续步骤" }

    $exitA = Invoke-Setup -Setup $compileA.Setup -LogName 'install-a.log'
    $statusA = Get-Status -Root $target
    Record-Result -Name "静默安装 $VersionA 后可解析到版本" `
        -Passed ($exitA -eq 0 -and $statusA.Code -eq 0 -and $statusA.Version -eq $VersionA) `
        -Detail ("setupExit={0} status={1} version={2}" -f $exitA, $statusA.Code, $statusA.Version)

    Record-Result -Name '安装目录载荷存在且指针可解析' `
        -Passed ((Test-Path (Join-Path $target "app-$VersionA\FNSyncPlus.WinUI.exe")) -and $statusA.Code -eq 0) `
        -Detail ("app-{0} 存在，status={1}" -f $VersionA, $statusA.Code)

    Test-InstalledProfile $VersionA

    $compileB = Invoke-Compile -Version $VersionB -Payload $PayloadB -OutDir (Join-Path $WorkRoot 'setup-b')
    Record-Result -Name "Inno Setup 编译 $VersionB 安装包" -Passed ($compileB.Code -eq 0 -and (Test-Path $compileB.Setup)) `
        -Detail ("exit={0}" -f $compileB.Code)

    $exitB = Invoke-Setup -Setup $compileB.Setup -LogName 'install-b.log'
    $statusB = Get-Status -Root $target
    Record-Result -Name "覆盖安装升级到 $VersionB" `
        -Passed ($exitB -eq 0 -and $statusB.Code -eq 0 -and $statusB.Version -eq $VersionB) `
        -Detail ("setupExit={0} status={1} version={2}" -f $exitB, $statusB.Code, $statusB.Version)

    Test-InstalledProfile $VersionB

    $keptOld = Test-Path (Join-Path $target "app-$VersionA\FNSyncPlus.WinUI.exe")
    Record-Result -Name '升级保留旧版本目录（可回滚）' -Passed $keptOld `
        -Detail ("app-{0} 保留={1}" -f $VersionA, $keptOld)

    $previous = Read-JsonFile -Path (Join-Path $target 'previous.json')
    Record-Result -Name '升级后写入 previous.json 指向旧版本' `
        -Passed ($null -ne $previous -and $previous.version -eq $VersionA) `
        -Detail ("previous={0}" -f ($(if ($null -eq $previous) { 'null' } else { $previous.version })))

    $shell = New-Object -ComObject WScript.Shell
    $shellFiles = New-Object -ComObject Shell.Application
    foreach ($shortcut in @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'FN Sync Plus Installation Acceptance.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'FN Sync Plus Installation Acceptance\FN Sync Plus Installation Acceptance.lnk'))) {
        $link = $shell.CreateShortcut($shortcut)
        $item = $shellFiles.NameSpace((Split-Path $shortcut)).ParseName((Split-Path $shortcut -Leaf))
        $identity = $item.ExtendedProperty('System.AppUserModel.ID')
        Record-Result -Name ('安装快捷方式 ' + (Split-Path $shortcut -Leaf)) `
            -Passed ((Test-Path $shortcut) -and $link.TargetPath -eq (Join-Path $target 'FN_Sync_Plus.exe') -and
                $link.IconLocation.StartsWith((Join-Path $target 'FN_Sync_Plus.exe')) -and $identity -eq 'FNSyncPlus.Desktop.Acceptance') `
            -Detail ("target={0} icon={1} identity={2}" -f $link.TargetPath, $link.IconLocation, $identity)
    }
    if ($VerifyUi) {
        & (Join-Path $PSScriptRoot 'ui-visual-acceptance.ps1') -Payload (Join-Path $target "app-$VersionB") -OutRoot (Join-Path $WorkRoot 'installed-ui')
        $uiReport = Read-JsonFile (Join-Path $WorkRoot 'installed-ui\ui-acceptance-result.json')
        Record-Result -Name '安装升级后的真实 WinUI 页面验收' -Passed ($null -ne $uiReport -and $uiReport.failed -eq 0) -Detail 'installed-ui/ui-acceptance-result.json'
    }

    $launcher = Join-Path $target 'FN_Sync_Plus.exe'
    $rollback = Run-Exe -Path $launcher -Arguments @('--layout', $target, '--rollback', '--no-launch')
    $afterRollback = Get-Status -Root $target
    Record-Result -Name "安装升级后可回滚到 $VersionA" `
        -Passed ($rollback.Code -eq 0 -and $afterRollback.Version -eq $VersionA) `
        -Detail ("exit={0} version={1} {2}" -f $rollback.Code, $afterRollback.Version, ($rollback.Output.Trim() -split "`r?`n")[0])

    $uninstaller = Join-Path $target 'unins000.exe'
    $uninstallExit = -1
    if (Test-Path -LiteralPath $uninstaller) {
        $p = Start-Process -FilePath $uninstaller -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="{0}"' -f (Join-Path $WorkRoot 'uninstall.log')))
        $uninstallExit = $p.ExitCode
    }
    $launcherGone = -not (Test-Path -LiteralPath $launcher)
    Record-Result -Name '卸载移除程序目录中的启动器' -Passed ($uninstallExit -eq 0 -and $launcherGone) `
        -Detail ("exit={0} launcherGone={1}" -f $uninstallExit, $launcherGone)

    Record-Result -Name '卸载保留隔离配置原始字节' -Passed ((Test-Path -LiteralPath $profileFile) -and (Get-FileHash -LiteralPath $profileFile -Algorithm SHA256).Hash -eq $profileHash) `
        -Detail ("SHA-256={0}" -f $profileHash)

    Record-Result -Name '卸载清除注册表卸载项' `
        -Passed (-not (Test-Path (Join-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" $appId))) `
        -Detail 'HKCU 卸载项已移除'

    $reinstallExit = Invoke-Setup -Setup $compileB.Setup -LogName 'reinstall-b.log'
    $statusC = Get-Status -Root $target
    Record-Result -Name '卸载后可重新安装同一版本' `
        -Passed ($reinstallExit -eq 0 -and $statusC.Code -eq 0 -and $statusC.Version -eq $VersionB) `
        -Detail ("setupExit={0} status={1} version={2}" -f $reinstallExit, $statusC.Code, $statusC.Version)

    if (Test-Path -LiteralPath (Join-Path $target 'unins000.exe')) {
        $p = Start-Process -FilePath (Join-Path $target 'unins000.exe') -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
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
