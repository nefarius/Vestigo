#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run with Windows PowerShell (powershell.exe), so the payload is tested on .NET Framework rather than .NET.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectDir = Join-Path $repoRoot 'installer/Vestigo.Installer.Actions'
$project = Join-Path $projectDir 'Vestigo.Installer.Actions.csproj'
& dotnet build $project -c $Configuration -m:1 -p:UseSharedCompilation=false -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Custom-action build failed.' }

$toolsDir = (& dotnet msbuild $project -nologo -getProperty:MakeSfxCAPath).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not locate the restored DTF packaging tools.' }
[void][Reflection.Assembly]::LoadFrom((Join-Path $toolsDir 'WixToolset.Dtf.Compression.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $toolsDir 'WixToolset.Dtf.Compression.Cab.dll'))

$package = Join-Path $projectDir "bin/$Configuration/net48/Vestigo.Installer.Actions.CA.dll"
$archive = New-Object WixToolset.Dtf.Compression.Cab.CabInfo($package)
$files = @($archive.GetFiles() | ForEach-Object Name)
$response = Join-Path $projectDir "obj/$Configuration/net48/Vestigo.Installer.Actions.CA.rsp"
$payloadSources = @(Get-Content -LiteralPath $response | ForEach-Object { $_.Trim('"') })
$payloadSources += Join-Path $projectDir "bin/$Configuration/net48/Vestigo.Installer.Actions.dll"

# Use a fresh folder so an earlier build's dependencies cannot hide missing CAB entries.
$extracted = Join-Path $projectDir ("obj/$Configuration/net48/package-smoke-" + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($extracted)
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = (New-Object Reflection.AssemblyName($eventArgs.Name)).Name
    $path = Join-Path $extracted ($name + '.dll')
    if (Test-Path -LiteralPath $path) {
        # Byte loads avoid locking the extracted files; no bin/ or NuGet fallback is registered.
        return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
    }
    return $null
}

try {
    $archive.Unpack($extracted)
    foreach ($source in $payloadSources) {
        $name = [IO.Path]::GetFileName($source)
        if ($files -notcontains $name) { throw "Missing CAB payload: $name" }
        $packed = Join-Path $extracted $name
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $packed).Hash) {
            throw "Stale CAB payload: $name"
        }
    }
    if ($files -notcontains 'System.Text.Json.dll') { throw 'System.Text.Json was not packaged.' }
    if ($files -notcontains 'CustomAction.config') { throw 'The CLR host configuration was not packaged.' }

    [AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $extracted 'Vestigo.Installer.Actions.dll')))
    $framework = $assembly.GetCustomAttributesData() | Where-Object {
        $_.AttributeType.FullName -eq 'System.Runtime.Versioning.TargetFrameworkAttribute'
    }
    if ($framework.ConstructorArguments[0].Value -ne '.NETFramework,Version=v4.8') {
        throw 'The packaged custom-action assembly does not target net48.'
    }

    # Run the actual extracted implementation, not the source linked into the net10 tests.
    $existing = [char]0xFEFF + @'
/* settings */ {"Keep": [1e3, -0, 1.2300, 123456789012345678901234567890],
 "Vestigo": {"ListenAddress":"127.0.0.1", "ListenPort":6000, "ApiTokens":["existing-test-token"],
 "volumeroots":2, "volumerootaccess":7,
 "modules":{"devices":{"enabled":false,"Keep":1.5E+03}},
 "Duplicate":{"A":1,"B":2,"A":3},},}
'@
    $settings = [Vestigo.Installer.Actions.Logic.ListenSettings]::ReadExisting($existing)
    if ($settings.Port -ne 6000 -or $settings.Tokens[0] -ne 'existing-test-token') { throw 'Listen settings could not be read.' }
    $roots = [Vestigo.Installer.Actions.Logic.RootSettings]::ReadExisting($existing)
    if ($roots.VolumeRoots -ne 'FixedVolumes' -or $roots.VolumeRootAccess -ne 'Read, Write, Execute') { throw 'Numeric root settings changed.' }
    $modules = [Vestigo.Installer.Actions.Logic.ModuleSelection]::ReadExisting($existing)
    if ($modules['Devices'] -ne $false) { throw 'Module settings could not be read.' }
    $modules['Devices'] = $true
    $resolved = [Vestigo.Installer.Actions.Logic.ListenSettings]::Resolve('localhost', 'token', '6001', 'existing-test-token', $settings, $false, $false)
    $resolvedRoots = [Vestigo.Installer.Actions.Logic.RootSettings]::Resolve('fixed', 'rwx', $roots, $true, $true)
    $merged = [Vestigo.Installer.Actions.Logic.ListenSettings]::Merge($existing, $resolved, $modules, $resolvedRoots)
    foreach ($expected in @('1e3', '-0', '1.2300', '123456789012345678901234567890', '1.5E+03', '"volumeroots"', '"devices"', '"enabled": true')) {
        if (-not $merged.Contains($expected)) { throw "Merge changed expected content: $expected" }
    }
    if ($merged.Contains("`r") -or $merged[0] -eq [char]0xFEFF -or -not $merged.EndsWith("`n")) { throw 'Output encoding or line endings changed.' }
    if ([Vestigo.Installer.Actions.Logic.ListenSettings]::ReadExisting($merged).Port -ne 6001) { throw 'Merged settings could not be read.' }
    if ($null -ne [Vestigo.Installer.Actions.Logic.ListenSettings]::ReadExisting('{"Vestigo":{"ListenPort":01}}')) { throw 'Malformed JSON was accepted.' }

    Write-Host "Verified net48 custom-action CAB: $($files.Count) entries; extracted settings read/merge/write passed on CLR $([Environment]::Version)."
}
finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
    # Check the resolved deletion target remains inside this project's intermediate output.
    $intermediateRoot = [IO.Path]::GetFullPath((Join-Path $projectDir "obj/$Configuration/net48")) + [IO.Path]::DirectorySeparatorChar
    $cleanupPath = [IO.Path]::GetFullPath($extracted)
    if (-not $cleanupPath.StartsWith($intermediateRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a path outside the custom-action intermediate directory.'
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force
}
