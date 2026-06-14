param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\POE2LootLens.Tests\POE2LootLens.Tests.csproj"

Push-Location $repositoryRoot
try {
    & dotnet test $project --configuration $Configuration --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
