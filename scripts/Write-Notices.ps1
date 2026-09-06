param([Parameter(Mandatory)][string]$PublishPath, [Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$licenseRoot = Join-Path $PublishPath 'licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'CUE4Parse/LICENSE') -Destination (Join-Path $licenseRoot 'CUE4Parse-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'CUE4Parse/NOTICE') -Destination (Join-Path $licenseRoot 'CUE4Parse-NOTICE.txt')
$assets = Get-Content -LiteralPath (Join-Path $repoRoot 'FModel.Mcp/obj/project.assets.json') -Raw | ConvertFrom-Json
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Third-party dependencies')
$lines.Add('')
$lines.Add('This inventory is generated from the resolved build. CUE4Parse and its embedded components are covered by the notices in licenses/. The self-contained .NET runtime includes its own LICENSE.txt and ThirdPartyNotices.txt. Package license files are preserved below when supplied by the package.')
$lines.Add('')
$lines.Add('| Package | Version | License | Upstream |')
$lines.Add('| --- | --- | --- | --- |')
foreach ($library in ($assets.libraries.PSObject.Properties | Sort-Object Name)) {
    if ($library.Value.type -ne 'package') { continue }
    $directory = Join-Path $PackagePath $library.Value.path
    $spec = Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' | Select-Object -First 1
    [xml]$xml = Get-Content -LiteralPath $spec.FullName -Raw
    $metadata = $xml.package.metadata
    $license = [string]$metadata.license.InnerText
    if ($metadata.license.type -eq 'file') {
        $licenseFile = [IO.Path]::GetFullPath((Join-Path $directory $license))
        if (!$licenseFile.StartsWith([IO.Path]::GetFullPath($directory) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package license path.' }
        $licenseName = $library.Name.Replace('/', '-') + '.txt'
        Copy-Item -LiteralPath $licenseFile -Destination (Join-Path $licenseRoot $licenseName)
        $license = "[Included license](licenses/$licenseName)"
    }
    if (!$license) { $license = [string]$metadata.licenseUrl }
    $upstream = [string]$metadata.repository.url
    if (!$upstream) { $upstream = [string]$metadata.projectUrl }
    $lines.Add("| $($metadata.id) | $($metadata.version) | $license | $upstream |")
}
[IO.File]::WriteAllLines((Join-Path $PublishPath 'THIRD-PARTY-NOTICES.md'), $lines, [Text.UTF8Encoding]::new($false))
