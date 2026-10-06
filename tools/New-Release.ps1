<#
.SYNOPSIS
    Builds the setup program and the update packages of CloudDrive-Sync with Velopack - and publishes a release.

.DESCRIPTION
    Releases are built on GitHub, not on a private PC: the release workflow (.github/workflows/release.yml) runs this
    script on a GitHub-hosted runner for every version tag, so everyone can trace a release back to its source code -
    which code signing through SignPath also requires.

    Building:
    1. Publishes the program self-contained for 64-bit Windows: .NET is included, so nothing else has to be installed
       and no administrator rights are needed.
    2. Fetches the newest release from GitHub, so Velopack can add a small delta update to the full package.
    3. Packs the setup program, the update packages and the feed (releases.win.json).

    The version comes from Directory.Build.props, the release notes from its section in CHANGELOG.md. Versions with a
    suffix such as "-preview.2" become pre-releases - test versions that only reach testers.

.PARAMETER Publish
    On a PC: tags the current commit "v<version>" and pushes the tag; the release workflow then builds and publishes.
    The commit must be on GitHub's main branch already.
    In the release workflow: builds and creates the GitHub release for the tag.

.PARAMETER OutputDir
    Where the built files go (default: %USERPROFILE%\.clouddrive-sync-build\releases).

.EXAMPLE
    tools/New-Release.ps1
    A trial build. Nothing is published.

.EXAMPLE
    tools/New-Release.ps1 -Publish
    Tags and pushes the version; GitHub builds and publishes it. Needs Git, signed in to GitHub.
#>
param(
    [switch]$Publish,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repository = 'suebi76/CloudDrive-Sync'
$repoUrl = "https://github.com/$repository"
$inWorkflow = $env:GITHUB_ACTIONS -eq 'true'

function Get-Version {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}

# The section of this version in CHANGELOG.md, without its heading.
function Get-ReleaseNotes([string]$Version) {
    $lines = Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding utf8
    $start = [Array]::FindIndex([string[]]$lines, [Predicate[string]] { param($l) $l.StartsWith("## [$Version]", [StringComparison]::Ordinal) })
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
$tag = "v$version"
$isTestVersion = $version.Contains('-')
Write-Host "CloudDrive-Sync $version$(if ($isTestVersion) { ' - Testversion' })"
# The release notes must exist before anything is tagged or built. Every release page links to the code signing policy
# and the privacy policy.
$notes = (Get-ReleaseNotes $version) + "`n`n---`n`n[Code signing policy]($repoUrl#code-signing-policy) · [Datenschutz / privacy policy]($repoUrl/blob/main/PRIVACY.md)"

# On a PC, publishing means: tag the commit and let GitHub build the release from it.
if ($Publish -and -not $inWorkflow) {
    if (git -C $root status --porcelain) { throw 'The working tree has uncommitted changes. Commit them first.' }
    Invoke-Checked 'git fetch' { git -C $root fetch -q origin main }
    git -C $root merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) { throw 'This commit is not on GitHub''s main branch yet. Push it first.' }
    Invoke-Checked 'git tag' { git -C $root tag -a $tag -m "CloudDrive-Sync $version" }
    Invoke-Checked 'git push' { git -C $root push -q origin $tag }
    Write-Host "Tag $tag pushed. GitHub builds and publishes the release: $repoUrl/actions/workflows/release.yml"
    return
}
# In the release workflow, the tag that started it must name this version.
if ($Publish -and $env:GITHUB_REF_NAME -ne $tag) { throw "The workflow runs for '$env:GITHUB_REF_NAME', but the version is $version (tag $tag)." }

$build = Join-Path $env:USERPROFILE '.clouddrive-sync-build'
$programDir = Join-Path $build 'release-program'
$releaseDir = if ($OutputDir) { [IO.Path]::GetFullPath($OutputDir) } else { Join-Path $build 'releases' }
$notesFile = Join-Path $build 'release-notes.md'

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

    # 2. The newest release on GitHub, for a delta update. The very first release has none. The workflow's token
    # spares the runner GitHub's limit for anonymous requests.
    $download = @('vpk', 'download', 'github', '--repoUrl', $repoUrl, '--outputDir', $releaseDir)
    if ($isTestVersion) { $download += '--pre' }
    if ($env:GH_TOKEN) { $download += @('--token', $env:GH_TOKEN) }
    dotnet @download
    if ($LASTEXITCODE -ne 0) { Write-Host 'No earlier release found - only the full package is built.' }
    # A version that is out already cannot be released again; a trial build then builds it without a delta update.
    if (Test-Path (Join-Path $releaseDir "CloudDriveSync-$version-full.nupkg")) {
        if ($Publish) { throw "Version $version is released already. Raise the version in Directory.Build.props." }
        Write-Host "Version $version is released already - trial build without a delta update."
        Get-ChildItem $releaseDir | Remove-Item -Recurse -Force
    }

    # 3. Setup program, packages, feed.
    New-Item -ItemType Directory -Force $build | Out-Null
    $notes | Set-Content $notesFile -Encoding utf8
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

# 4. Publish (in the release workflow).
if ($Publish) {
    $release = @('release', 'create', $tag, '--repo', $repository, '--verify-tag', '--title', "CloudDrive-Sync $version", '--notes-file', $notesFile)
    if ($isTestVersion) { $release += '--prerelease' }
    Invoke-Checked 'gh release create' { gh @release @files }
    Write-Host "Published: $repoUrl/releases/tag/$tag"
}
