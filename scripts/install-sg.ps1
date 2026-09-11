<#
.SYNOPSIS
    Installs sg from the rolling 'latest-build' release on GitHub.

.DESCRIPTION
    The same install "sg update" does, for a machine that has no sg on it yet to run that with.
    It downloads sg-win-x64.zip from the release, unpacks it over %LOCALAPPDATA%\sg, and puts
    that folder on the user PATH. Nothing is written outside the user's own profile and no
    elevation is asked for.

    No token is needed: the repository is public. One is used if it happens to be there -
    SG_GH_TOKEN, GH_TOKEN, GITHUB_TOKEN, then whatever "gh auth token" prints, the same order sg
    itself uses - because an authenticated call gets a far larger share of GitHub's rate limit.

    A running sg.exe does not stop this: Windows allows renaming a running exe, so an old one is
    moved aside as .sg-old and swept the next time. sg-ui.exe is a window, not a file lock that
    can be renamed around, so it is asked to close first when -Force is given.

.PARAMETER Repo
    owner/name to install from. Defaults to lukaasm/svn-git, or SG_REPO when that is set.

.PARAMETER Destination
    Where to install. Defaults to %LOCALAPPDATA%\sg.

.PARAMETER Force
    Close a running sg-ui before installing, rather than stopping with a message.

.PARAMETER NoPath
    Install, but leave the user PATH alone.

.EXAMPLE
    .\install-sg.ps1

.EXAMPLE
    .\install-sg.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $Repo,
    [string] $Destination,
    [switch] $Force,
    [switch] $NoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ReleaseTag = 'latest-build'
$ZipAsset   = 'sg-win-x64.zip'
$StampFile  = 'build.json'
$OldSuffix  = '.sg-old'

function Step($text) { Write-Host "  $text" }
function Fail($text) { Write-Error $text; exit 1 }

# ---- where it goes ---------------------------------------------------------

if (-not $Repo) { $Repo = if ($env:SG_REPO) { $env:SG_REPO } else { 'lukaasm/svn-git' } }
if (-not $Destination) { $Destination = Join-Path $env:LOCALAPPDATA 'sg' }

# ---- and who is running it -------------------------------------------------
# This writes only inside the user's own profile, so it never needs admin, and running it as admin
# does harm: the files land owned by the elevated token, and the next ordinary install cannot
# overwrite them - which is an access denied that reads like a reason to elevate again. Worse, sg
# started from that same elevated terminal is itself elevated, and an elevated process is refused
# by the picker broker, so every Browse in the app throws.

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($elevated) {
    Write-Warning "This is an elevated terminal, and this script does not need one: it writes only inside your own profile."
    Write-Host "  Installing anyway, because you asked. Two things follow from it:"
    Write-Host "    - the files are owned by the elevated token, so a later ordinary install may be denied."
    Write-Host "      If that happens: rmdir /s `"$Destination`" from here, then install again unelevated."
    Write-Host "    - do not start sg or sg-ui from this window. Anything started here is elevated too."
    Write-Host ""
}

Write-Host "sg  <-  $Repo ($ReleaseTag)"
Write-Host "into $Destination"
Write-Host ""

# ---- the token -------------------------------------------------------------
# Optional. The repository is public, so an anonymous call is answered; a token only buys a bigger
# share of GitHub's rate limit, which is what runs out on a machine that has asked a lot today.

function Get-Token {
    foreach ($name in 'SG_GH_TOKEN', 'GH_TOKEN', 'GITHUB_TOKEN') {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { Step "token from $name"; return $value }
    }
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        try {
            $value = (& $gh.Source auth token 2>$null | Out-String).Trim()
            if ($value) { Step 'token from gh auth token'; return $value }
        } catch { }
    }
    return $null
}

$token = Get-Token
if (-not $token) { Step 'no token; the repository is public, so none is needed' }

$headers = @{
    'User-Agent' = 'install-sg.ps1'
    'Accept'     = 'application/vnd.github+json'
}
if ($token) { $headers['Authorization'] = "Bearer $token" }

# ---- what is already installed ---------------------------------------------

$stampPath = Join-Path $Destination $StampFile

# The workflow writes runId, runNumber, commit, ref and builtUtc. Read defensively anyway: this
# file comes from a build that may be newer than this script, and strict mode turns a field that
# moved into a crash at the very end, after everything has already been installed.
function Read-Stamp($path) {
    if (-not (Test-Path $path)) { return $null }
    try { $json = Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
    $has = { param($n) $null -ne $json.PSObject.Properties[$n] }
    $number = if (& $has 'runNumber') { $json.runNumber } else { $null }
    $commit = if (& $has 'commit') { "$($json.commit)" } else { '' }
    if ($commit.Length -ge 7) { $commit = $commit.Substring(0, 7) }
    $built = if (& $has 'builtUtc') { $json.builtUtc } else { $null }
    $parts = @()
    if ($number) { $parts += "build $number" }
    if ($commit) { $parts += $commit }
    if ($built)  { $parts += $built }
    if ($parts.Count -eq 0) { return 'an unrecognised build' }
    return $parts -join ', '
}

$installed = Read-Stamp $stampPath
if ($installed) { Step "installed: $installed" }
else { Step 'installed: nothing here yet' }

# ---- the release -----------------------------------------------------------

# Everything above has to have run. A copy that lost its head - pasted from half a screen, saved
# from a page that scrolled - still parses and still runs, with every constant empty, and then asks
# GitHub for a release called "" and reports it as missing. Say what actually happened instead.
foreach ($pair in @(@('ReleaseTag', $ReleaseTag), @('ZipAsset', $ZipAsset), @('StampFile', $StampFile), @('OldSuffix', $OldSuffix))) {
    if (-not $pair[1]) {
        Fail "This copy of the script is truncated: `$$($pair[0]) is empty, so the top of the file is missing. Download it again rather than pasting it: scripts/install-sg.ps1 in lukaasm/svn-git."
    }
}

$api = "https://api.github.com/repos/$Repo/releases/tags/$ReleaseTag"
try {
    $release = Invoke-RestMethod -Uri $api -Headers $headers -Method Get
} catch {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($code -eq 404) {
        Fail "No '$ReleaseTag' release in $Repo. The build workflow publishes it on every push to main. If $Repo is private, 404 is also what GitHub answers with no token: run 'gh auth login', or set SG_GH_TOKEN."
    }
    if ($code -eq 401) { Fail "GitHub refused the token. Clear SG_GH_TOKEN, or run 'gh auth login' again. The repository is public, so no token at all also works." }
    if ($code -eq 403) { Fail "GitHub answered 403: either the rate limit is spent, which 'gh auth login' raises a long way, or the token cannot read $Repo." }
    Fail "Could not read the release: $($_.Exception.Message)"
}

$asset = $release.assets | Where-Object { $_.name -eq $ZipAsset } | Select-Object -First 1
if (-not $asset) { Fail "The '$ReleaseTag' release has no $ZipAsset. Assets on it: $(($release.assets.name) -join ', ')" }

Step "release: $($release.name), $ZipAsset is $([math]::Round($asset.size / 1MB, 1)) MB"

# ---- download --------------------------------------------------------------
# The asset URL rather than browser_download_url: the API one is the one that takes a token, so
# this keeps working if the repository is ever made private again.

$temp = Join-Path ([IO.Path]::GetTempPath()) ("sg-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$zip = Join-Path $temp $ZipAsset

try {
    Step 'downloading...'
    $dl = $headers.Clone()
    $dl['Accept'] = 'application/octet-stream'
    $before = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes this many times slower
    try { Invoke-WebRequest -Uri $asset.url -Headers $dl -OutFile $zip }
    finally { $ProgressPreference = $before }

    $payload = Join-Path $temp 'payload'
    Expand-Archive -Path $zip -DestinationPath $payload -Force
    $files = Get-ChildItem $payload -Recurse -File
    if ($files.Count -eq 0) { Fail "$ZipAsset unpacked to nothing." }
    Step "unpacked $($files.Count) file(s)"

    # ---- make room ---------------------------------------------------------
    # sg.exe may be running: a running exe can be renamed but not overwritten, and sg sweeps the
    # renamed one on its next update. sg-ui.exe is a window and is asked to close instead.

    $ui = Get-Process sg-ui -ErrorAction SilentlyContinue
    if ($ui) {
        if (-not $Force) {
            Fail "sg-ui is running (pid $($ui.Id -join ', ')). Close it, or run this again with -Force."
        }
        Step 'closing sg-ui'
        $ui | Stop-Process -Force
        Start-Sleep -Milliseconds 700
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    # Anything an earlier install renamed out of the way. It unlocks once that process is gone.
    Get-ChildItem $Destination -Recurse -File -Filter "*$OldSuffix" -ErrorAction SilentlyContinue |
        ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

    # ---- install -----------------------------------------------------------

    $installedCount = 0
    foreach ($file in $files) {
        $rel = $file.FullName.Substring($payload.Length).TrimStart('\', '/')
        $dest = Join-Path $Destination $rel
        New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
        if (Test-Path $dest) {
            try {
                Remove-Item $dest -Force
            } catch {
                # In use, or not ours. Renaming works for a running exe and is what sg's own updater
                # does; it does not work for a file an elevated install left behind, and that one has
                # to be said out loud, because "access denied" reads like a reason to elevate again
                # and elevating again is what put the file there.
                try {
                    Move-Item $dest ($dest + $OldSuffix) -Force
                } catch {
                    $why = if ($elevated) { '' } else {
                        " If an earlier install of sg was run as admin, these files belong to that token and an ordinary install cannot replace them: delete $Destination and install again from an ordinary terminal."
                    }
                    Fail "Could not replace $dest. Close anything using it - sg.exe, sg-ui, a terminal sitting in that folder - and run this again.$why"
                }
            }
        }
        Copy-Item $file.FullName $dest -Force
        $installedCount++
    }
    Step "installed $installedCount file(s)"
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- PATH ------------------------------------------------------------------
# The user's own PATH, not the machine's: nothing here needs elevation, and nothing here should
# change the machine for everyone on it.

if (-not $NoPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = if ($userPath) { $userPath.Split(';', [StringSplitOptions]::RemoveEmptyEntries) } else { @() }
    if ($parts -notcontains $Destination) {
        [Environment]::SetEnvironmentVariable('Path', (($parts + $Destination) -join ';'), 'User')
        Step "added $Destination to your PATH (new terminals see it)"
    } else {
        Step 'already on your PATH'
    }
    if (($env:Path -split ';') -notcontains $Destination) { $env:Path = "$env:Path;$Destination" }
}

# ---- say what landed -------------------------------------------------------

Write-Host ""
$stamp = Read-Stamp $stampPath
if ($stamp) { Write-Host "sg is installed in $Destination : $stamp" }
else { Write-Host "sg is installed in $Destination" }

$sg = Join-Path $Destination 'sg.exe'
if (Test-Path $sg) {
    # Its own report, and it names git and svn too. Whatever it says, the install itself is done:
    # sg version exits non-zero when git is missing, and a fresh machine is exactly where that is
    # true, so letting it decide this script's exit code would call every first install a failure.
    try { Write-Host (& $sg version 2>&1 | Out-String).Trim() } catch { }
    $global:LASTEXITCODE = 0
} else {
    Write-Warning "sg.exe is not in $Destination. The release may have changed shape."
}

# ---- what sg needs that this does not install ------------------------------
# sg runs git.exe and svn.exe; it does not carry them. A machine that has neither is the same
# machine that had no sg, so this is where to say so, with the command that fixes it.

$missing = @()
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $missing += @{ Name = 'git'; Id = 'Git.Git' } }
if (-not (Get-Command svn -ErrorAction SilentlyContinue)) { $missing += @{ Name = 'svn'; Id = 'Slik.Subversion' } }

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Warning "sg needs $(($missing.Name) -join ' and ') on PATH, and $(if ($missing.Count -eq 1) { 'it is' } else { 'they are' }) not there yet."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        foreach ($tool in $missing) {
            Write-Host "  winget install --id $($tool.Id) --exact --source winget"
        }
        Write-Host "  (Slik.Subversion is the command line svn; TortoiseSVN installs its own tools only if asked.)"
    } else {
        Write-Host "  git: https://git-scm.com/download/win"
        Write-Host "  svn: https://sliksvn.com/download/"
    }
    Write-Host "  Then open a new terminal, so it picks up the PATH they add."
}

Write-Host ""
Write-Host "  sg status          what it makes of the folder you are in"
Write-Host "  sg-ui              the app"
Write-Host "  sg update          from here on, sg installs its own updates"
exit 0
