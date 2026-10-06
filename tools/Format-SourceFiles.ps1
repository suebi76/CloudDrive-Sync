<#
.SYNOPSIS
    Normalises encodings and line endings of all source files of CloudDrive-Sync.
.DESCRIPTION
    *.ps1                                       UTF-8 with BOM, CRLF (Windows PowerShell 5.1 needs the BOM for umlauts)
    *.cs *.xaml *.csproj *.props *.slnx
    *.json *.md *.txt                           UTF-8 without BOM, CRLF
    *.yml *.yaml                                UTF-8 without BOM, LF
    Every file ends with exactly one line break. The unit test SourceFileTests checks the same rules.
.PARAMETER Check
    Only report files that do not comply (exit code 1); nothing is changed.
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rules = @(
    @{ Pattern = '\.ps1$'; Bom = $true; Eol = "`r`n" },
    @{ Pattern = '\.(cs|xaml|csproj|props|slnx|json|md|txt)$'; Bom = $false; Eol = "`r`n" },
    @{ Pattern = '\.(yml|yaml)$'; Bom = $false; Eol = "`n" }
)
$excluded = '\\(\.git|\.vs|\.claude|bin|obj|TestResults)\\'

# Only files that belong to the repository (tracked or not ignored).
$files = $null
if (Get-Command -Name 'git' -ErrorAction SilentlyContinue) {
    $list = & git -C $root -c core.quotepath=off ls-files --cached --others --exclude-standard 2>$null
    if ($LASTEXITCODE -eq 0 -and $list) {
        $files = @($list | ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) -Force -ErrorAction SilentlyContinue } | Where-Object { $_ })
    }
}
if (-not $files) { $files = Get-ChildItem -LiteralPath $root -Recurse -File -Force }

$problems = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    if ($file.FullName -match $excluded) { continue }
    $rule = $rules | Where-Object { $file.Name -match $_.Pattern } | Select-Object -First 1
    if (-not $rule) { continue }

    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $offset = 3 }
    $text = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
    $normalized = ($text -replace "`r`n", "`n") -replace "`r", "`n"
    $normalized = $normalized.TrimEnd("`n") + "`n"
    if ($rule.Eol -eq "`r`n") { $normalized = $normalized -replace "`n", "`r`n" }

    $encoding = New-Object Text.UTF8Encoding($rule.Bom)
    $expected = [byte[]]($encoding.GetPreamble() + $encoding.GetBytes($normalized))
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$bytes, $expected)) {
        $problems.Add($file.FullName.Substring($root.Length + 1))
        if (-not $Check) { [IO.File]::WriteAllBytes($file.FullName, $expected) }
    }
}

if ($Check) {
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "  not normalised: $_" -ForegroundColor Yellow }
        exit 1
    }
    Write-Host '  All source files are normalised.' -ForegroundColor Green
    exit 0
}
$problems | ForEach-Object { Write-Host "  normalised: $_" }
