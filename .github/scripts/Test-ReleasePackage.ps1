[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[string] $fullPackagePath = [System.IO.Path]::GetFullPath($PackagePath)
if (![System.IO.File]::Exists($fullPackagePath))
{
    throw "Package '$fullPackagePath' does not exist."
}

[string] $expectedFileName = "fhir-pkg-lib.$Version.nupkg"
if ([System.IO.Path]::GetFileName($fullPackagePath) -cne $expectedFileName)
{
    throw "Expected '$expectedFileName', found '$([System.IO.Path]::GetFileName($fullPackagePath))'."
}

[System.IO.Compression.ZipArchive] $archive =
    [System.IO.Compression.ZipFile]::OpenRead($fullPackagePath)

try
{
    [System.IO.Compression.ZipArchiveEntry[]] $nuspecEntries = @(
        $archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })

    if ($nuspecEntries.Count -ne 1)
    {
        throw "Expected one nuspec, found $($nuspecEntries.Count)."
    }

    [System.IO.StreamReader] $reader =
        [System.IO.StreamReader]::new($nuspecEntries[0].Open())

    try
    {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }

    [System.Xml.XmlNamespaceManager] $namespaces =
        [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $namespaces.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)

    [System.Xml.XmlElement] $metadata =
        $nuspec.SelectSingleNode('/n:package/n:metadata', $namespaces)
    if ($null -eq $metadata)
    {
        throw 'The package nuspec has no metadata element.'
    }

    if ($metadata.SelectSingleNode('n:id', $namespaces).InnerText -cne 'fhir-pkg-lib')
    {
        throw 'The package id is not fhir-pkg-lib.'
    }

    if ($metadata.SelectSingleNode('n:version', $namespaces).InnerText -cne $Version)
    {
        throw "The nuspec version does not match '$Version'."
    }

    [System.Xml.XmlElement] $repository =
        $metadata.SelectSingleNode('n:repository', $namespaces)
    if ($null -eq $repository)
    {
        throw 'The package nuspec has no repository metadata.'
    }

    if ($repository.GetAttribute('type') -cne 'git' -or
        $repository.GetAttribute('url') -cne 'https://github.com/GinoCanessa/dotnet-fhir-packages' -or
        $repository.GetAttribute('commit') -cne $RepositoryCommit)
    {
        throw 'The package repository metadata does not match the release commit.'
    }

    [string[]] $requiredEntries = @(
        'lib/net8.0/FhirPkg.dll',
        'lib/net9.0/FhirPkg.dll',
        'lib/net10.0/FhirPkg.dll'
    )

    [System.Collections.Generic.HashSet[string]] $entryNames =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $archive.Entries)
    {
        [void] $entryNames.Add($entry.FullName)
    }

    foreach ($requiredEntry in $requiredEntries)
    {
        if (!$entryNames.Contains($requiredEntry))
        {
            throw "The package is missing '$requiredEntry'."
        }
    }
}
finally
{
    $archive.Dispose()
}

[string] $sha256 = (Get-FileHash -Algorithm SHA256 -Path $fullPackagePath).Hash.ToLowerInvariant()

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content -Path $env:GITHUB_OUTPUT -Value "package_path=$fullPackagePath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$sha256"
}

Write-Output "Verified $expectedFileName ($sha256)."
