param([switch]$Test, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $PSScriptRoot '../.tools/dotnet/dotnet.exe' }
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$project = Join-Path $PSScriptRoot 'src/CadView/CadView.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = $projectXml.Project.PropertyGroup.Version
$publishDirectory = Join-Path $PSScriptRoot "dist/$version"
if ($Test) {
    & $sdk run --project (Join-Path $PSScriptRoot 'tests/CadView.Tests/CadView.Tests.csproj') -c Release -- $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $sdk run --project (Join-Path $PSScriptRoot 'tests/CadView.Tests/CadView.Tests.csproj') -c Release -- $PSScriptRoot --gpu-check
    if ($LASTEXITCODE -ne 0) { throw 'GPU tests failed' }
}
if ($Publish) {
    & $sdk publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -p:DebugSymbols=false -o $publishDirectory
} else { & $sdk build $project -c Release }
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
