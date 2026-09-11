<#
.SYNOPSIS
Puts the Monaco editor into src/Sg.App/Assets/monaco/vs, so the app ships its own copy.

.DESCRIPTION
The diff viewer prefers a bundled Monaco and only falls back to the URL in Settings when
there is none. Bundling means diffs are syntax coloured on any machine, with no internet
and no CDN. The folder is git ignored, so run this once after a clone, and again to
change version. The build workflow runs it before it publishes the app.
#>
[CmdletBinding()]
param(
    [string] $Version = '0.52.2',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo 'src\Sg.App\Assets\monaco\vs'
$loader = Join-Path $dest 'loader.js'

if ((Test-Path $loader) -and -not $Force) {
    Write-Host "monaco is already in $dest. Use -Force to fetch it again."
    exit 0
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("monaco-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $temp | Out-Null
try {
    $tgz = Join-Path $temp 'monaco.tgz'
    $url = "https://registry.npmjs.org/monaco-editor/-/monaco-editor-$Version.tgz"
    Write-Host "downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $tgz -UseBasicParsing

    # tar ships with Windows 10 1803 and later.
    tar -xzf $tgz -C $temp
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

    $src = Join-Path $temp 'package\min\vs'
    if (-not (Test-Path (Join-Path $src 'loader.js'))) { throw "no loader.js under $src" }

    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $src '*') $dest -Recurse -Force

    $files = (Get-ChildItem $dest -Recurse -File | Measure-Object -Property Length -Sum)
    Write-Host ("monaco {0}: {1} files, {2:N1} MB into {3}" -f $Version, $files.Count, ($files.Sum / 1MB), $dest)
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
