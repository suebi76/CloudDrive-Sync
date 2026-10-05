<#
.SYNOPSIS
    Builds the setup program and the update packages of CloudDrive-Sync with Velopack - and publishes them on GitHub
    when asked to.

.DESCRIPTION
    1. Publishes the program self-contained for 64-bit Windows: .NET is included, so nothing else has to be installed
       and no administrator rights are needed.
    2. Fetches the newest release from GitHub, so Velopack can add a small delta update to the full package.
    3. Packs the setup program, the update packages and the feed (releases.win.json).
    4. With -Publish: tags the current commit "v<version>" and creates the GitHub release with these files. Versions
       with a suffix such as "-preview.2" become pre-releases - test versions that only reach testers.

    The version comes from Directory.Build.props, the release notes from its section in CHANGELOG.md.

.EXAMPLE
    tools/New-Release.ps1
    Builds into %USERPROFILE%\.clouddrive-sync-build\releases.

.EXAMPLE
    tools/New-Release.ps1 -Publish
    Builds and publishes. Needs the GitHub CLI (gh), signed in with the right to create releases.
#>
param([switch]$Publish)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repository = 'suebi76/CloudDrive-Sync'
$repoUrl = "https://github.com/$repository"

function Get-Version {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}

# The section of this version in CHANGELOG.md, without its heading.
function Get-ReleaseNotes([string]$Version) {
    $lines = Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding utf8
    $start = [Array]::FindIndex([string[]]$lines, [Predicate[string]] { param($l) $l -like "## `[$Version`]*" })
    if ($start -lt 0) { throw "CHANGELOG.md has no section for $Version." }
    $end = [Array]::FindIndex([string[]]$lines, $start + 1, [Predicate[string]] { param($l) $l -like '## `[*' })
    if ($end -lt 0) { $end = $lines.Count }
    ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
}

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

$version = Get-Version
$isTestVersion = $version.Contains('-')
$build = Join-Path $env:USERPROFILE '.clouddrive-sync-build'
$programDir = Join-Path $build 'release-program'
$releaseDir = Join-Path $build 'releases'
$notesFile = Join-Path $build 'release-notes.md'
Write-Host "CloudDrive-Sync $version$(if ($isTestVersion) { ' - Testversion' })"

# 1. The program, self-contained.
if (Test-Path $programDir) { Remove-Item $programDir -Recurse -Force }
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Force $releaseDir | Out-Null
Invoke-Checked 'dotnet publish' {
    dotnet publish (Join-Path $root 'src\CloudDriveSync.App\CloudDriveSync.App.csproj') -c Release -r win-x64 --self-contained -o $programDir -nologo -v q
}

Push-Location $root
try {
    Invoke-Checked 'dotnet tool restore' { dotnet tool restore | Out-Null }

    # 2. The newest release on GitHub, for a delta update. The very first release has none.
    $download = @('vpk', 'download', 'github', '--repoUrl', $repoUrl, '--outputDir', $releaseDir)
    if ($isTestVersion) { $download += '--pre' }
    dotnet @download
    if ($LASTEXITCODE -ne 0) { Write-Host 'No earlier release found - only the full package is built.' }

    # 3. Setup program, packages, feed.
    Get-ReleaseNotes $version | Set-Content $notesFile -Encoding utf8
    Invoke-Checked 'vpk pack' {
        dotnet vpk pack --packId CloudDriveSync --packVersion $version --packDir $programDir --mainExe 'CloudDrive-Sync.exe' `
            --packTitle 'CloudDrive-Sync' --packAuthors 'Steffen Schwabe' `
            --icon (Join-Path $root 'src\CloudDriveSync.App\Assets\clouddrive-sync.ico') --releaseNotes $notesFile --outputDir $releaseDir
    }
}
finally {
    Pop-Location
}

# A name people recognise for the setup program.
$setup = Join-Path $releaseDir 'CloudDrive-Sync-Setup.exe'
Move-Item (Join-Path $releaseDir 'CloudDriveSync-win-Setup.exe') $setup -Force
$files = @($setup, (Join-Path $releaseDir 'releases.win.json')) +
    @(Get-ChildItem $releaseDir -Filter "CloudDriveSync-$version-*.nupkg" | ForEach-Object FullName)
Write-Host "Built:`n  $(($files | ForEach-Object { Split-Path $_ -Leaf }) -join "`n  ")"

# 4. Publish.
if ($Publish) {
    $tag = "v$version"
    Invoke-Checked 'git tag' { git -C $root tag -a $tag -m "CloudDrive-Sync $version" }
    Invoke-Checked 'git push' { git -C $root push -q origin $tag }
    $release = @('release', 'create', $tag, '--repo', $repository, '--verify-tag', '--title', "CloudDrive-Sync $version", '--notes-file', $notesFile)
    if ($isTestVersion) { $release += '--prerelease' }
    Invoke-Checked 'gh release create' { gh @release @files }
    Write-Host "Published: $repoUrl/releases/tag/$tag"
}