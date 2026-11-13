<#
.SYNOPSIS
    Set up ImageGen on a fresh machine: check the SDK, build, test, and
    optionally download models and trust the HTTPS dev certificate.

.EXAMPLE
    ./bootstrap.ps1                      # build + test only
    ./bootstrap.ps1 -Models sdturbo      # ...and fetch the fastest model (~2 GB)
    ./bootstrap.ps1 -AllModels -TrustDevCert

.NOTES
    Works in Windows PowerShell 5.1 and PowerShell 7+. Run from anywhere; it
    switches to the repo root itself. Safe to re-run: builds are incremental and
    downloads skip files already present.
#>
[CmdletBinding()]
param(
    # Model presets to download (sd15, sdturbo, sdxlturbo, sdxl).
    [string[]] $Models = @(),
    # Download every preset (large: tens of GB).
    [switch] $AllModels,
    # Trust the ASP.NET Core HTTPS dev cert (needed for the web UI's https URL).
    [switch] $TrustDevCert,
    # Skip the test run.
    [switch] $SkipTests,
    # Point Claude Code's auto-memory at the repo's .claude/memory folder.
    [switch] $ClaudeMemory
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Step([string] $msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Invoke-Checked([string] $exe, [string[]] $argList) {
    & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw "'$exe $($argList -join ' ')' failed with exit code $LASTEXITCODE" }
}

# 1. .NET SDK -----------------------------------------------------------------
Step 'Checking for the .NET 10 SDK'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
}
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw "No .NET 10 SDK found (have: $($sdks -join '; ')). Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
}
& dotnet --version

# 2. Build + test -------------------------------------------------------------
Step 'Building ImageGen.slnx'
Invoke-Checked dotnet @('build', 'ImageGen.slnx', '-nologo', '-v', 'q')

if (-not $SkipTests) {
    Step 'Running tests'
    Invoke-Checked dotnet @('test', 'ImageGen.slnx', '--no-build', '-nologo', '-v', 'q')
}

# 3. HTTPS dev cert (web UI) --------------------------------------------------
if ($TrustDevCert) {
    Step 'Trusting the ASP.NET Core HTTPS dev certificate'
    Invoke-Checked dotnet @('dev-certs', 'https', '--trust')
}

# 4. Models -------------------------------------------------------------------
$dlArgs = @()
if ($AllModels) { $dlArgs = @('--all') }
else { foreach ($m in $Models) { $dlArgs += @('--preset', $m) } }

if ($dlArgs.Count -gt 0) {
    Step "Downloading models ($($dlArgs -join ' '))"
    Invoke-Checked dotnet (@('run', '--project', 'src/ImageGen', '--no-build', '--', 'download') + $dlArgs)
}

# 5. Claude Code memory pointer (optional) ------------------------------------
if ($ClaudeMemory) {
    Step 'Pointing Claude Code auto-memory at .claude/memory'
    $settingsPath = Join-Path $PSScriptRoot '.claude/settings.local.json'
    $memoryDir = Join-Path $PSScriptRoot '.claude\memory'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    } else {
        $settings = New-Object PSObject
    }
    $settings | Add-Member -NotePropertyName autoMemoryDirectory -NotePropertyValue $memoryDir -Force
    $json = $settings | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "autoMemoryDirectory = $memoryDir"
}

# Done ------------------------------------------------------------------------
Step 'Done'
$present = @(Get-ChildItem -Path 'models' -Filter '*.safetensors' -ErrorAction SilentlyContinue)
if ($present.Count -eq 0) {
    Write-Host 'No models in ./models yet. Fetch one with:  ./bootstrap.ps1 -Models sdturbo'
}
Write-Host 'Try:  dotnet run --project src/ImageGen -- generate sdturbo "a red fox in a snowy forest"'
Write-Host 'Web:  dotnet run --project src/ImageGen.Web   (then open https://localhost:7192)'
