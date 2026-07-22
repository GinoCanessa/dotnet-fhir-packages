[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Tag,

    [string] $GitHubRef = $env:GITHUB_REF,

    [string] $SdkIndexUri =
        'https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib/index.json',

    [string] $CliIndexUri =
        'https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli/index.json',

    [switch] $AllowPublishedVersion
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

foreach ($component in @(
    $parsedVersion.Major,
    $parsedVersion.Minor,
    $parsedVersion.Build))
{
    if ($component -gt 65534)
    {
        throw "Version '$Version' cannot be represented as an assembly version."
    }
}

[string] $expectedTag = "v$Version"
if ($Tag -cne $expectedTag)
{
    throw "Tag '$Tag' must exactly match '$expectedTag'."
}

[string] $expectedRef = "refs/tags/$Tag"
if (![string]::IsNullOrWhiteSpace($GitHubRef) -and $GitHubRef -cne $expectedRef)
{
    throw "The workflow must run from '$expectedRef', not '$GitHubRef'."
}

[string] $headCommit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $headCommit -notmatch '^[0-9a-f]{40}$')
{
    throw 'Unable to resolve the checked-out commit.'
}

& git fetch --force origin "refs/tags/${Tag}:refs/tags/${Tag}"
if ($LASTEXITCODE -ne 0)
{
    throw "Unable to fetch release tag '$Tag' from origin."
}

[string] $tagCommit = (& git rev-list -n 1 $Tag).Trim()
if ($LASTEXITCODE -ne 0 -or $tagCommit -notmatch '^[0-9a-f]{40}$')
{
    throw "Unable to resolve tag '$Tag'."
}

if ($tagCommit -cne $headCommit)
{
    throw "Tag '$Tag' points to '$tagCommit', not checked-out commit '$headCommit'."
}

& git fetch --no-tags origin `
    '+refs/heads/main:refs/remotes/origin/main'
if ($LASTEXITCODE -ne 0)
{
    throw "Unable to fetch 'origin/main'."
}

[string] $mainCommit = (& git rev-parse origin/main).Trim()
if ($LASTEXITCODE -ne 0 -or $mainCommit -notmatch '^[0-9a-f]{40}$')
{
    throw "Unable to resolve 'origin/main'."
}

& git merge-base --is-ancestor $headCommit origin/main
if ($LASTEXITCODE -eq 1)
{
    throw "Release commit '$headCommit' is not an ancestor of origin/main '$mainCommit'."
}

if ($LASTEXITCODE -ne 0)
{
    throw "Unable to verify release ancestry against 'origin/main'."
}

if (!$AllowPublishedVersion)
{
    & (Join-Path `
        $PSScriptRoot `
        'Test-ReleaseVersionAvailability.ps1') `
        -Version $Version `
        -SdkIndexUri $SdkIndexUri `
        -CliIndexUri $CliIndexUri
}

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content -Path $env:GITHUB_OUTPUT -Value "commit=$headCommit"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "main_commit=$mainCommit"
}

Write-Output "Validated release $Version at $headCommit on origin/main $mainCommit."
