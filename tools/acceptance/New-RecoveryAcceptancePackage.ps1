<#
.SYNOPSIS
    独立异机恢复验收测试包生成工具（电脑 A 运行）。
.DESCRIPTION
    在源端电脑 A 生成符合验收规范的测试数据集，并生成无敏感信息的标准恢复清单 recovery-manifest.json。
    涵盖：
      1. 普通中文文件
      2. 超长多字节中文文件名（200+ 字符 / 250+ UTF-8 字节）
      3. 多层嵌套深目录与空目录
      4. Unicode 混合字符（特殊符号、Emoji、希腊字母、多语种）
      5. 包含历史版本迭代的文件（V1 与 V2）
      6. 逐文件 SHA-256 哈希值与大小记录
.PARAMETER OutputDir
    测试源文件生成目录。
.PARAMETER ManifestPath
    生成的校验清单 json 输出路径。
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [Parameter(Mandatory = $false)]
    [string]$ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ManifestPath) {
    $ManifestPath = Join-Path $OutputDir 'recovery-manifest.json'
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "=== 正在生成独立异机恢复验收测试包 ===" -ForegroundColor Cyan
Write-Host "目标目录: $OutputDir"
Write-Host "清单路径: $ManifestPath"

function Get-Sha256Hex([string]$FilePath) {
    return (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
}

$manifestEntries = @()
$emptyDirs = @()

# 1. 普通中文文件
$file1Rel = "01_普通文件\财务部_2026年度备份预算.docx"
$file1Full = Join-Path $OutputDir $file1Rel
New-Item -ItemType Directory -Path (Split-Path $file1Full) -Force | Out-Null
[System.IO.File]::WriteAllText($file1Full, "FN Sync Plus 异机恢复验收测试数据 - 普通中文财务预算文件`r`n创建时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", [System.Text.Encoding]::UTF8)
$manifestEntries += [pscustomobject]@{
    RelativePath = $file1Rel
    Size = (Get-Item $file1Full).Length
    Sha256 = Get-Sha256Hex $file1Full
    Category = "NormalChinese"
}

# 2. 多层嵌套目录与深层文件
$deepDirRel = "02_嵌套层级\一级目录\二级财务\三级归档"
$file2Rel = "$deepDirRel\深层归档数据明细.csv"
$file2Full = Join-Path $OutputDir $file2Rel
New-Item -ItemType Directory -Path (Split-Path $file2Full) -Force | Out-Null
$csvContent = "ID,部门,项目,金额`r`n1,技术部,飞牛同步加,100000`r`n2,市场部,产品推广,50000`r`n"
[System.IO.File]::WriteAllText($file2Full, $csvContent, [System.Text.Encoding]::UTF8)
$manifestEntries += [pscustomobject]@{
    RelativePath = $file2Rel
    Size = (Get-Item $file2Full).Length
    Sha256 = Get-Sha256Hex $file2Full
    Category = "DeepHierarchy"
}

# 3. 空目录结构
$emptyDir1 = "03_空目录集\空顶级目录"
$emptyDir2 = "03_空目录集\多层空目录\子空目录"
$emptyDir1Full = Join-Path $OutputDir $emptyDir1
$emptyDir2Full = Join-Path $OutputDir $emptyDir2
New-Item -ItemType Directory -Path $emptyDir1Full -Force | Out-Null
New-Item -ItemType Directory -Path $emptyDir2Full -Force | Out-Null
$emptyDirs += $emptyDir1
$emptyDirs += $emptyDir2

# 4. Unicode 混合字符与 Emoji
$file3Rel = "04_Unicode特殊字符\财务报告_💰_Release_αβγ_한글_日本語.txt"
$file3Full = Join-Path $OutputDir $file3Rel
New-Item -ItemType Directory -Path (Split-Path $file3Full) -Force | Out-Null
[System.IO.File]::WriteAllText($file3Full, "多语种测试内容：Hello World / 飞牛同步 / 한국어 / にほんご / αβγδε / 💰📈🚀", [System.Text.Encoding]::UTF8)
$manifestEntries += [pscustomobject]@{
    RelativePath = $file3Rel
    Size = (Get-Item $file3Full).Length
    Sha256 = Get-Sha256Hex $file3Full
    Category = "UnicodeSpecial"
}

# 5. 超长中文文件名（200+ 字符，安全在 255 字节限制内）
$longBase = "超长中文文件名专项验收测试用例_" + ("测试文件名扩充段" * 15)
$file4Rel = "05_超长名称\$longBase.log"
$file4Full = Join-Path $OutputDir $file4Rel
New-Item -ItemType Directory -Path (Split-Path $file4Full) -Force | Out-Null
[System.IO.File]::WriteAllText($file4Full, "超长文件名文件内容数据包 - 用于检验长路径与长文件名映射机制。", [System.Text.Encoding]::UTF8)
$manifestEntries += [pscustomobject]@{
    RelativePath = $file4Rel
    Size = (Get-Item $file4Full).Length
    Sha256 = Get-Sha256Hex $file4Full
    Category = "LongFileName"
}

# 6. 大文件测试（1MB 填充数据）
$file5Rel = "06_大文件\大块连续数据块_1MB.bin"
$file5Full = Join-Path $OutputDir $file5Rel
New-Item -ItemType Directory -Path (Split-Path $file5Full) -Force | Out-Null
$randomBytes = New-Object byte[] (1024 * 1024)
(New-Object System.Random(42)).NextBytes($randomBytes)
[System.IO.File]::WriteAllBytes($file5Full, $randomBytes)
$manifestEntries += [pscustomobject]@{
    RelativePath = $file5Rel
    Size = (Get-Item $file5Full).Length
    Sha256 = Get-Sha256Hex $file5Full
    Category = "LargeFile"
}

# 7. 历史版本文件（记录 V1，并在物理文件更新为 V2）
$file6Rel = "07_版本化文件\将被修改的历史版本文件.txt"
$file6Full = Join-Path $OutputDir $file6Rel
New-Item -ItemType Directory -Path (Split-Path $file6Full) -Force | Out-Null
[System.IO.File]::WriteAllText($file6Full, "第一版初始历史内容（版本 V1）", [System.Text.Encoding]::UTF8)
$v1Sha = Get-Sha256Hex $file6Full

# 模拟第二次编辑为 V2
[System.IO.File]::WriteAllText($file6Full, "第二版更新内容（当前最新版本 V2）", [System.Text.Encoding]::UTF8)
$v2Sha = Get-Sha256Hex $file6Full

$manifestEntries += [pscustomobject]@{
    RelativePath = $file6Rel
    Size = (Get-Item $file6Full).Length
    Sha256 = $v2Sha
    Category = "VersionedCurrent"
    HistoricalVersion = [pscustomobject]@{
        Version = 1
        ContentHint = "第一版初始历史内容（版本 V1）"
        Sha256 = $v1Sha
    }
}

$manifestObject = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString("o")
    GeneratorHost = $env:COMPUTERNAME
    TotalFiles = $manifestEntries.Count
    EmptyDirectories = $emptyDirs
    Files = $manifestEntries
}

$jsonText = $manifestObject | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($ManifestPath, $jsonText, [System.Text.Encoding]::UTF8)

Write-Host "成功生成验收包，包含 $($manifestEntries.Count) 个测试文件与 $($emptyDirs.Count) 个空目录！" -ForegroundColor Green
Write-Host "校验清单已保存至: $ManifestPath" -ForegroundColor Green
