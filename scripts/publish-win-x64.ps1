param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\POE2LootLens\POE2LootLens.csproj"
$output = Join-Path $repositoryRoot "publish"

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue

$args = @(
    "publish",
    $project,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--output", $output,
    "--self-contained", $(if ($FrameworkDependent) { "false" } else { "true" })
)

Push-Location $repositoryRoot
try {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
    Write-Host "Published to: $output"
}
finally {
    Pop-Location
}
