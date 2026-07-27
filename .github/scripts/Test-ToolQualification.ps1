[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ToolPath,

    [Parameter(Mandatory)]
    [string] $CandidateDirectory,

    [Parameter(Mandatory)]
    [ValidateSet('net8.0', 'net9.0', 'net10.0')]
    [string] $Framework,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit,

    [Parameter(Mandatory)]
    [string] $ExpectedPackageSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[string] $fullToolPath = [System.IO.Path]::GetFullPath($ToolPath)
if (![System.IO.Directory]::Exists($fullToolPath))
{
    throw "Tool path '$fullToolPath' does not exist."
}

[string] $shimName =
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows))
    {
        'fhir-pkg.exe'
    }
    else
    {
        'fhir-pkg'
    }
[string] $shimPath =
    [System.IO.Path]::Combine($fullToolPath, $shimName)
if (![System.IO.File]::Exists($shimPath))
{
    throw "Installed tool shim '$shimPath' does not exist."
}

[string] $storeRoot =
    [System.IO.Path]::Combine(
        $fullToolPath,
        '.store',
        'fhir-pkg-cli',
        $Version)
[string] $assetsPath =
    [System.IO.Path]::Combine($storeRoot, 'project.assets.json')
if (![System.IO.File]::Exists($assetsPath))
{
    throw "Tool restore assets '$assetsPath' do not exist."
}

[object] $assets =
    Get-Content -Raw -Path $assetsPath |
        ConvertFrom-Json
[System.Management.Automation.PSPropertyInfo[]] $targets = @(
    $assets.targets.PSObject.Properties |
        Where-Object {
            $_.Name.StartsWith(
                "$Framework/",
                [StringComparison]::Ordinal)
        })
if ($targets.Count -ne 1)
{
    throw "Expected one tool restore target for '$Framework', found $($targets.Count)."
}

[string] $libraryName = "fhir-pkg-cli/$Version"
[System.Management.Automation.PSPropertyInfo] $targetLibrary =
    $targets[0].Value.PSObject.Properties[$libraryName]
if ($null -eq $targetLibrary -or
    $targetLibrary.Value.type -cne 'package')
{
    throw "Tool restore target does not contain '$libraryName'."
}

[string[]] $toolAssets =
    @($targetLibrary.Value.tools.PSObject.Properties.Name)
foreach ($asset in @(
    "tools/$Framework/any/DotnetToolSettings.xml",
    "tools/$Framework/any/FhirPkg.Cli.dll",
    "tools/$Framework/any/FhirPkg.Cli.deps.json",
    "tools/$Framework/any/FhirPkg.Cli.runtimeconfig.json",
    "tools/$Framework/any/FhirPkg.dll"))
{
    if ($asset -cnotin $toolAssets)
    {
        throw "Tool restore target did not select '$asset'."
    }
}

[string] $candidatePackagePath =
    [System.IO.Path]::Combine(
        [System.IO.Path]::GetFullPath($CandidateDirectory),
        "fhir-pkg-cli.$Version.nupkg")
if (![System.IO.File]::Exists($candidatePackagePath))
{
    throw "Candidate package '$candidatePackagePath' does not exist."
}

[string[]] $restoredPackages = @(
    [System.IO.Directory]::GetFiles(
        $storeRoot,
        "fhir-pkg-cli.$Version.nupkg",
        [System.IO.SearchOption]::AllDirectories))
if ($restoredPackages.Count -ne 1)
{
    throw "Expected one restored fhir-pkg-cli package, found $($restoredPackages.Count)."
}

[string] $candidateHash =
    (Get-FileHash -Algorithm SHA256 -Path $candidatePackagePath).
        Hash.
        ToLowerInvariant()
[string] $restoredHash =
    (Get-FileHash -Algorithm SHA256 -Path $restoredPackages[0]).
        Hash.
        ToLowerInvariant()
if ($candidateHash -cne $ExpectedPackageSha256 -or
    $restoredHash -cne $ExpectedPackageSha256)
{
    throw "Candidate/restored CLI package hashes do not match '$ExpectedPackageSha256'."
}

[string] $isolationRoot =
    [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(),
        "fhirpkg-tool-qualification-$([Guid]::NewGuid().ToString('N'))")
[string] $workingDirectory =
    [System.IO.Path]::Combine($isolationRoot, 'work')
[string] $homeDirectory =
    [System.IO.Path]::Combine($isolationRoot, 'home')
[string] $cacheDirectory =
    [System.IO.Path]::Combine($isolationRoot, 'cache')
[System.IO.Directory]::CreateDirectory($workingDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($homeDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($cacheDirectory) | Out-Null

[string[]] $environmentNames = @(
    'HOME',
    'USERPROFILE',
    'DOTNET_CLI_HOME',
    'PACKAGE_CACHE_FOLDER',
    'HTTP_PROXY',
    'HTTPS_PROXY',
    'ALL_PROXY',
    'NO_PROXY',
    'http_proxy',
    'https_proxy',
    'all_proxy',
    'no_proxy'
)
[System.Collections.Generic.Dictionary[string, string]] $originalEnvironment =
    [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
foreach ($name in $environmentNames)
{
    [string] $value =
        [Environment]::GetEnvironmentVariable($name)
    $originalEnvironment.Add($name, $value)
}
[System.Management.Automation.PathInfo] $originalLocation = Get-Location

try
{
    [Environment]::SetEnvironmentVariable('HOME', $homeDirectory)
    [Environment]::SetEnvironmentVariable('USERPROFILE', $homeDirectory)
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $homeDirectory)
    [Environment]::SetEnvironmentVariable(
        'PACKAGE_CACHE_FOLDER',
        $cacheDirectory)
    foreach ($name in @(
        'HTTP_PROXY',
        'HTTPS_PROXY',
        'ALL_PROXY',
        'http_proxy',
        'https_proxy',
        'all_proxy'))
    {
        [Environment]::SetEnvironmentVariable(
            $name,
            'http://127.0.0.1:1')
    }
    [Environment]::SetEnvironmentVariable('NO_PROXY', '')
    [Environment]::SetEnvironmentVariable('no_proxy', '')
    Set-Location -LiteralPath $workingDirectory

    [string[]] $versionOutput = @(& $shimPath --version 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "The installed tool failed --version: $([string]::Join("`n", $versionOutput))"
    }

    [string] $informationalVersion =
        [string]::Join("`n", $versionOutput).Trim()
    [System.Text.RegularExpressions.Match] $versionMatch =
        [System.Text.RegularExpressions.Regex]::Match(
            $informationalVersion,
            '^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<build>[0-9]+)\+(?<commit>[0-9a-f]{40})$',
            [System.Text.RegularExpressions.RegexOptions]::
                CultureInvariant)
    if (!$versionMatch.Success)
    {
        throw "Tool informational version '$informationalVersion' is invalid."
    }

    [string[]] $expectedComponents = $Version.Split('.')
    foreach ($component in @('major', 'minor', 'build'))
    {
        [int] $index = switch ($component)
        {
            'major' { 0 }
            'minor' { 1 }
            default { 2 }
        }
        if ([uint64] $versionMatch.Groups[$component].Value -ne
            [uint64] $expectedComponents[$index])
        {
            throw "Tool informational version '$informationalVersion' does not match '$Version'."
        }
    }

    if ($versionMatch.Groups['commit'].Value -cne $RepositoryCommit)
    {
        throw "Tool informational version commit does not match '$RepositoryCommit'."
    }

    [string[]] $listOutput = @(
        & $shimPath `
            --package-cache-folder $cacheDirectory `
            --json `
            list `
            2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "The installed tool failed the empty-cache list smoke test: $([string]::Join("`n", $listOutput))"
    }

    [object] $listResult =
        [string]::Join("`n", $listOutput) |
            ConvertFrom-Json
    if ([int] $listResult.count -ne 0 -or
        @($listResult.packages).Count -ne 0)
    {
        throw 'The empty-cache list smoke test did not return an empty package list.'
    }
}
finally
{
    Set-Location -LiteralPath $originalLocation.Path
    foreach ($name in $environmentNames)
    {
        [Environment]::SetEnvironmentVariable(
            $name,
            $originalEnvironment[$name])
    }

    if ([System.IO.Directory]::Exists($isolationRoot))
    {
        [System.IO.Directory]::Delete($isolationRoot, $true)
    }
}

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content -Path $env:GITHUB_OUTPUT -Value "shim_path=$shimPath"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "informational_version=$informationalVersion"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "restored_sha256=$restoredHash"
}

Write-Output "Verified fhir-pkg-cli $Version for $Framework ($informationalVersion)."
