[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceSha
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')
$head = & git -C $repo.Path rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $SourceSha) { throw 'HEAD does not match SourceSha.' }
$project = Join-Path $repo.Path 'FNSyncPlus\FNSyncPlus.AcceptanceRunner\FNSyncPlus.AcceptanceRunner.csproj'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }

& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:SourceRevisionId=$SourceSha" -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Acceptance runner publish failed ($LASTEXITCODE)." }
$exe = Join-Path $OutputDirectory 'FNSyncPlus.AcceptanceRunner.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Acceptance runner executable is missing.' }
