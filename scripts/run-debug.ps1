param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\POE2LootLens\POE2LootLens.csproj"

Push-Location $repositoryRoot
try {
    & dotnet run --project $project --configuration $Configuration -- --debug
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
