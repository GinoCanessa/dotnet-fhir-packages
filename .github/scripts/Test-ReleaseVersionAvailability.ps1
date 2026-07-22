[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $SdkIndexUri =
        'https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib/index.json',

    [string] $CliIndexUri =
        'https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli/index.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$')
{
    throw "Version '$Version' must contain exactly three numeric components."
}

[version] $parsedVersion = [version] $Version
if ($parsedVersion.ToString(3) -cne $Version)
{
    throw "Version '$Version' is not in canonical numeric form."
}

[object[]] $indexes = @(
    [pscustomobject]@{
        PackageId = 'fhir-pkg-lib'
        Uri = $SdkIndexUri
    },
    [pscustomobject]@{
        PackageId = 'fhir-pkg-cli'
        Uri = $CliIndexUri
    }
)
[System.Collections.Generic.List[version]] $canonicalVersions =
    [System.Collections.Generic.List[version]]::new()

foreach ($index in $indexes)
{
    [object] $response = Invoke-RestMethod -Uri $index.Uri
    [string[]] $publishedVersions = @($response.versions)
    if ($Version -cin $publishedVersions)
    {
        throw "$($index.PackageId) '$Version' is already published."
    }

    foreach ($publishedVersion in $publishedVersions)
    {
        if ($publishedVersion -notmatch
            '^[0-9]+\.[0-9]+\.[0-9]+$')
        {
            continue
        }

        [version] $candidateVersion = [version] $publishedVersion
        if ($candidateVersion.ToString(3) -cne $publishedVersion)
        {
            continue
        }

        $canonicalVersions.Add($candidateVersion)
    }
}

if ($canonicalVersions.Count -gt 0)
{
    [version] $highestVersion =
        $canonicalVersions |
            Sort-Object -Descending |
            Select-Object -First 1
    if ($parsedVersion -le $highestVersion)
    {
        throw "Version '$Version' must be greater than the highest published canonical version '$highestVersion'."
    }
}

Write-Output "Validated fresh synchronized release version $Version."
