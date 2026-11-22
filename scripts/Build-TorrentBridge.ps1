[CmdletBinding()]
param(
    [string]$Version,
    [string]$SourceDirectory = (Join-Path $PSScriptRoot 'torrent-bridge'),
    [string]$NodeExecutable = (Get-Command node -CommandType Application | Select-Object -First 1 -ExpandProperty Source)
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    $project = [xml](Get-Content -Raw (Join-Path $projectRoot 'Jellyfin.Plugin.LinkAdder/Jellyfin.Plugin.LinkAdder.csproj'))
    $Version = $project.Project.PropertyGroup.Version -replace '\.0$', ''
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part release version.' }

$nodeInfo = & $NodeExecutable -p 'process.platform + "-" + process.arch'
if ($LASTEXITCODE -ne 0 -or $nodeInfo -ne 'win32-x64') {
    throw 'Use a Windows x64 Node.js executable to build the Windows bundle.'
}
$nodeLicense = Join-Path (Split-Path -Parent $NodeExecutable) 'LICENSE'
$dependencies = Join-Path $SourceDirectory 'node_modules'
if (!(Test-Path -LiteralPath $dependencies -PathType Container) -or !(Test-Path -LiteralPath $nodeLicense -PathType Leaf)) {
    throw 'Run npm ci on Windows x64 and provide Node.js from a distribution that includes its LICENSE.'
}

$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) ("torrent-bridge-bundle-" + [Guid]::NewGuid().ToString('N'))
$bridge = Join-Path $stage 'torrent-bridge'
New-Item -ItemType Directory -Path $bridge | Out-Null
try {
    foreach ($file in @('bridge.mjs', 'bridge.test.mjs', 'package.json', 'package-lock.json')) {
        Copy-Item -LiteralPath (Join-Path $SourceDirectory $file) -Destination $bridge
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $bridge
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'torrent-bridge/INSTALL.md') -Destination (Join-Path $bridge 'README.md')

    $sourceZip = Join-Path $dist "Torrent-Bridge-$Version-source.zip"
    Compress-Archive -LiteralPath $bridge -DestinationPath $sourceZip -Force

    $runtime = Join-Path $bridge 'runtime'
    New-Item -ItemType Directory -Path $runtime | Out-Null
    Copy-Item -LiteralPath $NodeExecutable -Destination (Join-Path $runtime 'node.exe')
    Copy-Item -LiteralPath $nodeLicense -Destination $runtime
    Copy-Item -LiteralPath $dependencies -Destination $bridge -Recurse
    $windowsZip = Join-Path $dist "Torrent-Bridge-$Version-windows-x64.zip"
    Compress-Archive -LiteralPath $bridge -DestinationPath $windowsZip -Force
    Get-Item -LiteralPath $sourceZip, $windowsZip
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $stageItem = Get-Item -LiteralPath $resolvedStage
    if ([IO.Path]::GetDirectoryName($resolvedStage) -eq [IO.Path]::TrimEndingDirectorySeparator($tempRoot) -and
        $stageItem.Name -match '^torrent-bridge-bundle-[a-f0-9]{32}$' -and
        -not ($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
