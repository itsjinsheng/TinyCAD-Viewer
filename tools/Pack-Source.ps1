$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/CadView/CadView.csproj') -Raw
$version = $project.Project.PropertyGroup.Version
$destination = Join-Path $root "dist/TinyCAD-Viewer-$version-source.zip"
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null

# Explicit allowlist keeps build caches, local SDKs, private test drawings and old EXEs out.
$files = @('TinyCAD.sln', '.editorconfig', '.gitignore', 'build.ps1', 'README.md',
    'TESTING.md', 'GPU-EXPERIMENT.md', 'RELEASE-NOTES.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE') |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) }
foreach ($directory in @('src', 'tests', 'tools', 'licenses', 'samples')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $directory) -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
}
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($destination, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName,
            $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
Write-Output $destination
