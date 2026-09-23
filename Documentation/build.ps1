[CmdletBinding()]
param(
    [switch]$Serve,
    [switch]$ArticlesOnly,
    [ValidateRange(1, 65535)]
    [int]$Port = 8080
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $pythonNames = if ($env:OS -eq 'Windows_NT') { @('python', 'python3') } else { @('python3', 'python') }
    $pythonCommand = Get-Command $pythonNames -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pythonCommand) { throw 'Python 3.9 or newer is required to generate API block navigation.' }
    & $pythonCommand.Source -c "import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)"
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.9 or newer is required to generate API block navigation.' }
    & dotnet tool restore --tool-manifest "$PSScriptRoot/dotnet-tools.json"
    if ($LASTEXITCODE -ne 0) { throw 'DocFX restore failed. Check your NuGet connection and installed .NET SDK.' }

    $sitePath = Join-Path $PSScriptRoot '_site'

    if ($ArticlesOnly) {
        if (-not (Test-Path "$PSScriptRoot/_api/toc.yml")) {
            throw 'Run a full build first (without -ArticlesOnly) to generate the API reference.'
        }
    } else {
        & dotnet tool run docfx metadata docfx.json
        if ($LASTEXITCODE -ne 0) { throw 'API metadata generation failed. See the DocFX errors above.' }
    }
    & $pythonCommand.Source "$PSScriptRoot/generate_api_blocks.py"
    if ($LASTEXITCODE -ne 0) { throw 'API block navigation generation failed. See the errors above.' }
    & dotnet tool run docfx build docfx.json --output $sitePath
    if ($LASTEXITCODE -ne 0) { throw 'Documentation build failed. See the DocFX errors above.' }

    Write-Host "Documentation built: $sitePath"

    if ($Serve) {
        & dotnet tool run docfx serve $sitePath --port $Port
        if ($LASTEXITCODE -ne 0) { throw 'Documentation preview failed.' }
    }
} finally {
    Pop-Location
}
