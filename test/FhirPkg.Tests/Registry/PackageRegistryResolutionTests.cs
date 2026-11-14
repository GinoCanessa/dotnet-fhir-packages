// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

[Collection("EnvironmentVariable")]
public class PackageRegistryResolutionTests
{
    private const string MarkerPackage = "example.marker";
    private const string FhirListingUrl = "https://fhir.registry.example/packages/example.marker";
    private const string NpmListingUrl = "https://npm.registry.example/packages/example.marker";

    // Literal, offline source specimens; these establish raw data, not Candle eligibility.
    private static readonly string?[] MarkerForms =
    [
        null, "null", "\"\"", "\"true\"", "\"false\"", "\" \\t\\r\\n \"",
    ];

    public static IEnumerable<object?[]> IndividualMarkerCases()
    {
        foreach (RegistryType type in new[] { RegistryType.FhirNpm, RegistryType.Npm })
        {
            for (int index = 0; index < MarkerForms.Length; index++)
            {
                yield return [type, index, MarkerForms[index]];
            }
        }
    }

    [Theory]
    [MemberData(nameof(IndividualMarkerCases))]
    public async Task IndividualClients_UnlistedForms_PreserveSameResponseMetadata(
        RegistryType type, int specimen, string? raw)
    {
        bool fhir = type == RegistryType.FhirNpm;
        string listingUrl = fhir ? FhirListingUrl : NpmListingUrl;
        RoutedHandler handler = new();
        handler.AddJson(listingUrl, SourceListingJson(fhir, raw));
        using HttpClient httpClient = new(handler);
        IRegistryClient client = CreateMarkerClient(httpClient, type);

        PackageListing? listing = await client.GetPackageListingAsync(
            MarkerPackage, TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        listing.PackageId.ShouldBe(MarkerPackage);
        listing.SourceRegistry.ShouldBe(MarkerProvenance(type));
        listing.IsComplete.ShouldBeTrue();
        listing.QueryFailures.ShouldBeEmpty();
        listing.LatestVersion.ShouldBe(fhir ? "1.0.0" : "2.0.0");
        listing.VersionCandidates.Count.ShouldBe(2);
        listing.Versions.Count.ShouldBe(2);
        foreach (string version in new[] { "1.0.0", "2.0.0" })
        {
            PackageVersionInfo candidate = listing.VersionCandidates.Single(item => item.Version == version);
            candidate.ShouldBeSameAs(listing.Versions[version]);
            AssertSourceRecord(candidate, fhir, version, raw);
            candidate.SourceClient.ShouldBeSameAs(client);
        }

        AssertListingSerialization(listing);
        handler.AssertCount(listingUrl, 1);
        handler.TotalRequests.ShouldBe(1, $"marker specimen {specimen}");
    }

    public static IEnumerable<object?[]> CompositeMarkerCases()
    {
        for (int index = 0; index < MarkerForms.Length; index++)
        {
            foreach (bool reverse in new[] { false, true })
            {
                yield return [index, MarkerForms[index], MarkerForms[(index + 1) % MarkerForms.Length], reverse];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CompositeMarkerCases))]
    public async Task CompositeListing_UnlistedMarkers_StayPairedWithSource(
        int specimen, string? fhirRaw, string? npmRaw, bool reverse)
    {
        RoutedHandler handler = new();
        handler.AddJson(FhirListingUrl, SourceListingJson(true, fhirRaw));
        handler.AddJson(NpmListingUrl, SourceListingJson(false, npmRaw));
        using HttpClient httpClient = new(handler);
        IRegistryClient fhirClient = CreateMarkerClient(httpClient, RegistryType.FhirNpm);
        IRegistryClient npmClient = CreateMarkerClient(httpClient, RegistryType.Npm);
        IRegistryClient[] sources = reverse ? [npmClient, fhirClient] : [fhirClient, npmClient];
        RedundantRegistryClient composite = new(sources);

        PackageListing? listing = await composite.GetPackageListingAsync(
            MarkerPackage, TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        AssertMergedSources(listing, fhirRaw, npmRaw, reverse);
        listing.VersionCandidates.Where(candidate => candidate.SourceRegistry!.Type == RegistryType.FhirNpm)
            .ShouldAllBe(candidate => ReferenceEquals(candidate.SourceClient, fhirClient));
        listing.VersionCandidates.Where(candidate => candidate.SourceRegistry!.Type == RegistryType.Npm)
            .ShouldAllBe(candidate => ReferenceEquals(candidate.SourceClient, npmClient));
        AssertListingSerialization(listing);
        handler.AssertCount(FhirListingUrl, 1);
        handler.AssertCount(NpmListingUrl, 1);
        handler.TotalRequests.ShouldBe(2, $"marker specimen {specimen}");
    }

    [Theory]
    [MemberData(nameof(CompositeMarkerCases))]
    public async Task CompositeListing_UnlistedMarkers_RepresentativeOnlyFallbackStaysPaired(
        int specimen, string? fhirRaw, string? npmRaw, bool reverse)
    {
        // Supplemental fallback-branch coverage: no candidates or provenance are supplied.
        PackageListing fhirListing = JsonSerializer.Deserialize<PackageListing>(SourceListingJson(true, fhirRaw))!;
        PackageListing npmListing = JsonSerializer.Deserialize<PackageListing>(SourceListingJson(false, npmRaw))!;
        fhirListing.VersionCandidates.ShouldBeEmpty();
        npmListing.VersionCandidates.ShouldBeEmpty();
        ListingOnlyClient fhirClient = new(MarkerEndpoint(RegistryType.FhirNpm), fhirListing);
        ListingOnlyClient npmClient = new(MarkerEndpoint(RegistryType.Npm), npmListing);
        IRegistryClient[] sources = reverse ? [npmClient, fhirClient] : [fhirClient, npmClient];
        RedundantRegistryClient composite = new(sources);

        PackageListing? listing = await composite.GetPackageListingAsync(
            MarkerPackage, TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        AssertMergedSources(listing, fhirRaw, npmRaw, reverse);
        AssertListingSerialization(listing);
        fhirClient.ListingRequests.ShouldBe(1, $"marker specimen {specimen}");
        npmClient.ListingRequests.ShouldBe(1);
        foreach (PackageVersionInfo candidate in listing.VersionCandidates)
        {
            candidate.SourceClient.ShouldBeSameAs(
                candidate.SourceRegistry!.Type == RegistryType.FhirNpm ? fhirClient : npmClient);
        }
    }

    public static IEnumerable<object?[]> MarkerPolicyCases()
    {
        foreach (string route in new[] { "fhir", "npm", "composite" })
        {
            for (int index = 0; index < MarkerForms.Length; index++)
            {
                yield return [route, index, MarkerForms[index]];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MarkerPolicyCases))]
    public async Task ResolveAndDownload_UnlistedMarker_DoesNotChangeSdkPolicy(
        string route, int specimen, string? raw)
    {
        (string Specifier, bool? AllowPreRelease, FhirRelease? Release, string? Expected)[] cases =
        [
            ("latest", null, null, "2.0.0-beta"),
            ("latest", false, null, "3.0.0"),
            ("latest", false, FhirRelease.R4, "1.0.0"),
            ("latest", true, FhirRelease.R4, "2.1.0-beta"),
            ("latest", false, FhirRelease.R5, "3.0.0"),
            ("2.*.*-*", true, null, "2.1.0-beta"),
            ("2.*.*", false, null, null),
            ("2.*.*-*", true, FhirRelease.R5, "2.0.0-beta"),
            ("2.*.*", true, null, null),
            ("2.*.*-*", false, null, null),
            ("1.*.*", false, FhirRelease.R4, "1.0.0"),
            ("1.0.0", false, FhirRelease.R5, null),
            ("2.0.0-beta", false, null, null),
            ("2.0.0-beta", true, FhirRelease.R5, "2.0.0-beta"),
            ("3.0.0", false, FhirRelease.R5, "3.0.0"),
        ];
        RoutedHandler handler = new();
        bool useFhir = route != "npm";
        bool useNpm = route != "fhir";
        if (useFhir)
            handler.AddJson(FhirListingUrl, PolicyListingJson(true, raw), cases.Length + 1);
        if (useNpm)
            handler.AddJson(NpmListingUrl, PolicyListingJson(false, raw), cases.Length + 1);
        bool selectedFhir = route != "npm";
        string artifact = PolicyArtifactUrl(selectedFhir, "3.0.0");
        byte[] payload = Encoding.UTF8.GetBytes($"offline artifact from {(selectedFhir ? "fhir" : "npm")}");
        handler.AddDownload(artifact, payload);
        using HttpClient httpClient = new(handler);
        IRegistryClient fhirClient = CreateMarkerClient(httpClient, RegistryType.FhirNpm);
        IRegistryClient npmClient = CreateMarkerClient(httpClient, RegistryType.Npm);
        IRegistryClient client = route switch
        {
            "fhir" => fhirClient,
            "npm" => npmClient,
            _ => new RedundantRegistryClient(fhirClient, npmClient),
        };
        PackageListing? listing = await client.GetPackageListingAsync(
            MarkerPackage, TestContext.Current.CancellationToken);
        listing.ShouldNotBeNull();
        listing.LatestVersion.ShouldBe("2.0.0-beta");
        listing.DistTags!["latest"].ShouldBe("2.0.0-beta");
        listing.Versions["3.0.0"].Unlisted.ValueKind.ShouldBe(raw is null
            ? JsonValueKind.Undefined : JsonSerializer.Deserialize<JsonElement>(raw).ValueKind);
        AssertMarker(listing.Versions["3.0.0"].Unlisted, raw);

        ResolvedDirective? explicitKnown = null;
        foreach ((string specifier, bool? allowPreRelease, FhirRelease? release, string? expected) in cases)
        {
            ResolvedDirective? resolved = await client.ResolveAsync(
                PackageDirective.Parse($"{MarkerPackage}#{specifier}"),
                allowPreRelease is null && release is null ? null
                    : new VersionResolveOptions { AllowPreRelease = allowPreRelease ?? true, FhirRelease = release },
                TestContext.Current.CancellationToken);
            if (expected is null)
            {
                resolved.ShouldBeNull($"{route}/{specimen}/{specifier}/{release}/{allowPreRelease}");
                continue;
            }

            resolved.ShouldNotBeNull($"{route}/{specimen}/{specifier}/{release}/{allowPreRelease}");
            resolved.Reference.Version.ShouldBe(expected);
            resolved.TarballUri.AbsoluteUri.ShouldBe(PolicyArtifactUrl(selectedFhir, expected));
            resolved.ShaSum.ShouldBe($"sha-{(selectedFhir ? "fhir" : "npm")}-{expected}");
            resolved.Integrity.ShouldBe($"sha512-{(selectedFhir ? "fhir" : "npm")}-{expected}");
            resolved.SourceRegistry.ShouldBe(MarkerProvenance(selectedFhir ? RegistryType.FhirNpm : RegistryType.Npm));
            if (specifier == "3.0.0")
                explicitKnown = resolved;
        }

        explicitKnown.ShouldNotBeNull();
        await using PackageDownloadResult? download = await client.DownloadAsync(
            explicitKnown, TestContext.Current.CancellationToken);
        download.ShouldNotBeNull();
        download.ContentType.ShouldBe("application/gzip");
        download.ContentLength.ShouldBe(payload.Length);
        using MemoryStream downloaded = new();
        await download.Content.CopyToAsync(downloaded, TestContext.Current.CancellationToken);
        downloaded.ToArray().ShouldBe(payload);
        handler.AssertCount(artifact, 1);
        if (useFhir)
            handler.AssertCount(FhirListingUrl, cases.Length + 1);
        if (useNpm)
            handler.AssertCount(NpmListingUrl, cases.Length + 1);
        handler.TotalRequests.ShouldBe((useFhir && useNpm ? 2 : 1) * (cases.Length + 1) + 1);
    }

    private static RegistryEndpoint MarkerEndpoint(RegistryType type) => new()
    {
        Url = type == RegistryType.FhirNpm
            ? "https://fhir.registry.example/packages/"
            : "https://npm.registry.example/packages/",
        Type = type,
    };

    private static RegistryEndpoint MarkerProvenance(RegistryType type) => new()
    {
        Url = type == RegistryType.FhirNpm
            ? "https://fhir.registry.example/"
            : "https://npm.registry.example/",
        Type = type,
    };

    private static IRegistryClient CreateMarkerClient(HttpClient httpClient, RegistryType type) =>
        type == RegistryType.FhirNpm
            ? new FhirNpmRegistryClient(httpClient, MarkerEndpoint(type), NullLogger<FhirNpmRegistryClient>.Instance)
            : new NpmRegistryClient(httpClient, MarkerEndpoint(type), NullLogger<NpmRegistryClient>.Instance);

    private static string SourceListingJson(bool fhir, string? raw)
    {
        string source = fhir ? "fhir" : "npm";
        return $$"""
            {"name":"{{MarkerPackage}}","description":"{{source}} listing",
             "dist-tags":{"latest":"{{(fhir ? "1.0.0" : "2.0.0")}}","channel":"{{source}}-channel"},
             "versions":{
               "1.0.0":{{SourceVersionJson(fhir, "1.0.0", raw)}},
               "2.0.0":{{SourceVersionJson(fhir, "2.0.0", raw)}}
             } }
            """;
    }

    private static string SourceVersionJson(bool fhir, string version, string? raw)
    {
        string source = fhir ? "fhir" : "npm";
        string fhirMember = fhir ? "\"fhirVersion\":\"4.0.1\"" : "\"fhirVersions\":[\"5.0.0\",\"4.3.0\"]";
        return $$"""
            {"name":"{{MarkerPackage}}","version":"{{version}}","description":"{{source}} {{version}}",
             {{fhirMember}},
             "dist":{"tarball":"https://{{source}}.artifacts.example/{{version}}.tgz","shasum":"sha-{{source}}-{{version}}","integrity":"sha512-{{source}}-{{version}}"},
             "dependencies":{"{{source}}.dependency":"{{version}}","shared.dependency":"{{(fhir ? "4.0.0" : "5.0.0")}}"},
             "canonical":"https://{{source}}.canonical.example/ig","kind":"{{source}}-ig",
             "date":"{{(fhir ? "2024-02-03" : "2025-04-05")}}","count":{{(fhir ? 4 : 5)}},
             "license":"{{(fhir ? "MIT" : "Apache-2.0")}}","url":"https://{{source}}.info.example/ig"
            """ + (raw is null ? "" : ",\"unlisted\":" + raw) + "}";
    }

    private static void AssertSourceRecord(
        PackageVersionInfo candidate, bool fhir, string version, string? raw)
    {
        string source = fhir ? "fhir" : "npm";
        candidate.Name.ShouldBe(MarkerPackage);
        candidate.Version.ShouldBe(version);
        AssertMarker(candidate.Unlisted, raw);
        candidate.Description.ShouldBe($"{source} {version}");
        candidate.Distribution!.TarballUrl.ShouldBe($"https://{source}.artifacts.example/{version}.tgz");
        candidate.Distribution.ShaSum.ShouldBe($"sha-{source}-{version}");
        candidate.Distribution.Integrity.ShouldBe($"sha512-{source}-{version}");
        candidate.Dependencies!.Count.ShouldBe(2);
        candidate.Dependencies[$"{source}.dependency"].ShouldBe(version);
        candidate.Dependencies["shared.dependency"].ShouldBe(fhir ? "4.0.0" : "5.0.0");
        candidate.FhirVersion.ShouldBe(fhir ? "4.0.1" : "5.0.0");
        candidate.FhirVersions!.ShouldBe(fhir ? ["4.0.1"] : ["5.0.0", "4.3.0"]);
        candidate.HasExplicitFhirVersionMetadata.ShouldBeTrue();
        candidate.Canonical.ShouldBe($"https://{source}.canonical.example/ig");
        candidate.Kind.ShouldBe($"{source}-ig");
        candidate.PublicationDate.ShouldBe(fhir ? "2024-02-03" : "2025-04-05");
        candidate.ResourceCount.ShouldBe(fhir ? 4 : 5);
        candidate.License.ShouldBe(fhir ? "MIT" : "Apache-2.0");
        candidate.Url.ShouldBe($"https://{source}.info.example/ig");
        candidate.SourceRegistry.ShouldBe(MarkerProvenance(fhir ? RegistryType.FhirNpm : RegistryType.Npm));
        candidate.IsSourceLatest.ShouldBe(version == (fhir ? "1.0.0" : "2.0.0"));
    }

    private static void AssertMergedSources(
        PackageListing listing, string? fhirRaw, string? npmRaw, bool reverse)
    {
        listing.PackageId.ShouldBe(MarkerPackage);
        listing.SourceRegistry.ShouldBeNull();
        listing.IsComplete.ShouldBeTrue();
        listing.QueryFailures.ShouldBeEmpty();
        listing.Description.ShouldBe(reverse ? "npm listing" : "fhir listing");
        listing.DistTags!["channel"].ShouldBe(reverse ? "npm-channel" : "fhir-channel");
        listing.LatestVersion.ShouldBe("2.0.0");
        listing.VersionCandidates.Count.ShouldBe(4);
        listing.Versions.Count.ShouldBe(2);
        listing.VersionCandidates.Select(candidate => candidate.SourceRegistry!.Type).ShouldBe(
            reverse
                ? [RegistryType.Npm, RegistryType.Npm, RegistryType.FhirNpm, RegistryType.FhirNpm]
                : [RegistryType.FhirNpm, RegistryType.FhirNpm, RegistryType.Npm, RegistryType.Npm]);
        listing.VersionCandidates.Select(candidate => candidate.Version).ShouldBe(
            ["1.0.0", "2.0.0", "1.0.0", "2.0.0"]);
        foreach (PackageVersionInfo candidate in listing.VersionCandidates)
        {
            bool fhir = candidate.SourceRegistry!.Type == RegistryType.FhirNpm;
            AssertSourceRecord(candidate, fhir, candidate.Version, fhir ? fhirRaw : npmRaw);
        }

        foreach (string version in new[] { "1.0.0", "2.0.0" })
        {
            PackageVersionInfo representative = listing.Versions[version];
            representative.ShouldBeSameAs(listing.VersionCandidates.First(candidate => candidate.Version == version));
            AssertSourceRecord(representative, !reverse, version, reverse ? npmRaw : fhirRaw);
        }
    }

    private static void AssertMarker(JsonElement actual, string? raw)
    {
        if (raw is null)
        {
            actual.ValueKind.ShouldBe(JsonValueKind.Undefined);
            return;
        }

        using JsonDocument expected = JsonDocument.Parse(raw);
        actual.ValueKind.ShouldBe(expected.RootElement.ValueKind);
        JsonElement.DeepEquals(actual, expected.RootElement).ShouldBeTrue();
        if (actual.ValueKind == JsonValueKind.String)
            actual.GetString().ShouldBe(expected.RootElement.GetString());
    }

    private static void AssertListingSerialization(PackageListing listing)
    {
        JsonSerializerOptions options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        string json = JsonSerializer.Serialize(listing, options);
        foreach (string ignored in new[]
        {
            "SourceRegistry", "SourceClient", "VersionCandidates", "IsSourceLatest",
            "IsComplete", "QueryFailures", "HasExplicitFhirVersionMetadata",
        })
        {
            json.ShouldNotContain(ignored);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        PackageListing reread = JsonSerializer.Deserialize<PackageListing>(json)!;
        reread.VersionCandidates.ShouldBeEmpty();
        foreach ((string key, PackageVersionInfo original) in listing.Versions)
        {
            JsonElement written = document.RootElement.GetProperty("versions").GetProperty(key);
            written.TryGetProperty("unlisted", out _).ShouldBe(original.Unlisted.ValueKind != JsonValueKind.Undefined);
            AssertSerializedRecord(reread.Versions[key], original);
        }

        foreach (PackageVersionInfo original in listing.VersionCandidates)
        {
            string candidateJson = JsonSerializer.Serialize(original, options);
            candidateJson.ShouldNotContain("SourceRegistry");
            candidateJson.ShouldNotContain("SourceClient");
            PackageVersionInfo rereadCandidate = JsonSerializer.Deserialize<PackageVersionInfo>(candidateJson)!;
            AssertSerializedRecord(rereadCandidate, original);
        }
    }

    private static void AssertSerializedRecord(PackageVersionInfo actual, PackageVersionInfo original)
    {
        actual.Name.ShouldBe(original.Name);
        actual.Version.ShouldBe(original.Version);
        AssertMarker(actual.Unlisted, original.Unlisted.ValueKind == JsonValueKind.Undefined
            ? null : original.Unlisted.GetRawText());
        actual.Distribution.ShouldBe(original.Distribution);
        actual.Dependencies!.ShouldBe(original.Dependencies!);
        actual.FhirVersions!.ShouldBe(original.FhirVersions!);
        actual.Description.ShouldBe(original.Description);
        actual.Canonical.ShouldBe(original.Canonical);
        actual.Kind.ShouldBe(original.Kind);
        actual.PublicationDate.ShouldBe(original.PublicationDate);
        actual.ResourceCount.ShouldBe(original.ResourceCount);
        actual.License.ShouldBe(original.License);
        actual.Url.ShouldBe(original.Url);
        actual.SourceRegistry.ShouldBeNull();
        actual.SourceClient.ShouldBeNull();
        actual.IsSourceLatest.ShouldBeFalse();
    }

    private static string PolicyArtifactUrl(bool fhir, string version) =>
        $"https://{(fhir ? "fhir" : "npm")}.policy.example/{version}.tgz";

    private static string PolicyListingJson(bool fhir, string? raw)
    {
        (string Version, string Release)[] versions =
        [
            ("1.0.0", "4.0.1"), ("2.0.0-beta", "5.0.0"),
            ("2.1.0-beta", "4.0.1"), ("3.0.0", "5.0.0"),
        ];
        string source = fhir ? "fhir" : "npm";
        string[] entries = versions.Select(item => $$"""
            "{{item.Version}}":{"name":"{{MarkerPackage}}","version":"{{item.Version}}",
            "fhirVersion":"{{item.Release}}","dependencies":{"{{source}}.dependency":"1.0.0"},
            "dist":{"tarball":"{{PolicyArtifactUrl(fhir, item.Version)}}","shasum":"sha-{{source}}-{{item.Version}}","integrity":"sha512-{{source}}-{{item.Version}}"}
            """ + (raw is null ? "" : ",\"unlisted\":" + raw) + "}").ToArray();
        return $$"""
            {"name":"{{MarkerPackage}}","dist-tags":{"latest":"2.0.0-beta"},"versions":{ {{string.Join(",", entries)}} } }
            """;
    }

    private sealed class ListingOnlyClient(RegistryEndpoint endpoint, PackageListing listing) : IRegistryClient
    {
        public int ListingRequests { get; private set; }
        public RegistryEndpoint Endpoint => endpoint;
        public IReadOnlyList<PackageNameType> SupportedNameTypes => [PackageNameType.NonHl7Guide];
        public IReadOnlyList<VersionType> SupportedVersionTypes => [VersionType.Exact, VersionType.Latest];

        public Task<PackageListing?> GetPackageListingAsync(string packageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packageId.ShouldBe(MarkerPackage);
            ListingRequests++;
            ListingRequests.ShouldBe(1);
            return Task.FromResult<PackageListing?>(listing);
        }

        public Task<IReadOnlyList<CatalogEntry>> SearchAsync(
            PackageSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ResolvedDirective?> ResolveAsync(
            PackageDirective directive, VersionResolveOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PackageDownloadResult?> DownloadAsync(
            ResolvedDirective resolved, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PublishResult> PublishAsync(
            PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Bytes, string ContentType, int Maximum)> _routes = [];
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public int TotalRequests => _counts.Values.Sum();

        public void AddJson(string url, string json, int maximum = 1) =>
            _routes.Add(url, (Encoding.UTF8.GetBytes(json), "application/json", maximum));
        public void AddDownload(string url, byte[] bytes) =>
            _routes.Add(url, (bytes, "application/gzip", 1));
        public void AssertCount(string url, int expected) =>
            _counts.GetValueOrDefault(url).ShouldBe(expected, url);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Method.ShouldBe(HttpMethod.Get);
            string url = request.RequestUri!.AbsoluteUri;
            if (!_routes.TryGetValue(url, out (byte[] Bytes, string ContentType, int Maximum) route))
                throw new InvalidOperationException($"Unexpected offline URL: {url}");
            int count = _counts.AddOrUpdate(url, 1, (_, previous) => previous + 1);
            count.ShouldBeLessThanOrEqualTo(route.Maximum, url);
            ByteArrayContent content = new(route.Bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(route.ContentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public async Task FhirNpmClient_UsesSharedVersionPolicy()
    {
        await AssertSharedPolicyAsync(
            httpClient => new FhirNpmRegistryClient(
                httpClient,
                new RegistryEndpoint
                {
                    Url = "https://registry.example/",
                    Type = RegistryType.FhirNpm,
                },
                NullLogger<FhirNpmRegistryClient>.Instance));
    }

    [Fact]
    public async Task NpmClient_UsesSharedVersionPolicy()
    {
        await AssertSharedPolicyAsync(
            httpClient => new NpmRegistryClient(
                httpClient,
                new RegistryEndpoint
                {
                    Url = "https://registry.example/",
                    Type = RegistryType.Npm,
                },
                NullLogger<NpmRegistryClient>.Instance));
    }

    private static async Task AssertSharedPolicyAsync(
        Func<HttpClient, IRegistryClient> createClient)
    {
        const string json = """
            {
              "name": "example.package",
              "dist-tags": { "latest": "2.0.0-beta" },
              "versions": {
                "1.0.0": {
                  "name": "example.package",
                  "version": "1.0.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "shasum": "sha",
                    "integrity": "sha512-strong",
                    "tarball": "https://downloads.example/1.0.0.tgz"
                  }
                },
                "2.0.0-beta": {
                  "name": "example.package",
                  "version": "2.0.0-beta",
                  "fhirVersion": "5.0.0",
                  "dist": {
                    "integrity": "sha512-beta",
                    "tarball": "https://downloads.example/2.0.0-beta.tgz"
                  }
                },
                "2.0": {
                  "name": "example.package",
                  "version": "2.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/2.0.tgz"
                  }
                },
                "2.0.0+build": {
                  "name": "example.package",
                  "version": "2.0.0+build",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/2.0.0-build.tgz"
                  }
                },
                "4.0.0": {
                  "name": "example.package",
                  "version": "4.0.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/4.0.0.tgz"
                  }
                },
                "4.1.0": {
                  "name": "example.package",
                  "version": "4.1.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/4.1.0.tgz"
                  }
                },
                "6.0.0-ballot": {
                  "name": "example.package",
                  "version": "6.0.0-ballot",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/6.0.0-ballot.tgz"
                  }
                },
                "6.0.0": {
                  "name": "example.package",
                  "version": "6.0.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/6.0.0.tgz"
                  }
                },
                "6.1.0": {
                  "name": "example.package",
                  "version": "6.1.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "tarball": "https://downloads.example/6.1.0.tgz"
                  }
                }
              }
            }
            """;
        using HttpClient httpClient = new(new JsonHandler(json));
        IRegistryClient client = createClient(httpClient);

        PackageListing? listing = await client.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);
        ResolvedDirective? resolved = await client.ResolveAsync(
            PackageDirective.Parse("example.package#latest"),
            new VersionResolveOptions
            {
                AllowPreRelease = false,
                FhirRelease = FhirRelease.R4,
            },
            TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        listing.SourceRegistry.ShouldBe(client.Endpoint);
        listing.VersionCandidates.Count.ShouldBe(9);
        listing.VersionCandidates.Single(candidate => candidate.Version == "2.0.0-beta")
            .IsSourceLatest.ShouldBeTrue();
        listing.VersionCandidates.Single(candidate => candidate.Version == "1.0.0")
            .Distribution!.Integrity.ShouldBe("sha512-strong");
        resolved.ShouldNotBeNull();
        resolved.Reference.Version.ShouldBe("6.1.0");

        (string Specifier, string Expected)[] cases =
        [
            ("2.0", "2.0"),
            ("2.0.0+*", "2.0.0+build"),
            ("4.x?", "4.1.0"),
            ("6.1?", "6.1.0"),
            ("4.*.*", "4.1.0"),
            ("6.0.x-*", "6.0.0-ballot"),
        ];
        foreach ((string specifier, string expected) in cases)
        {
            ResolvedDirective? wildcard = await client.ResolveAsync(
                PackageDirective.Parse($"example.package#{specifier}"),
                new VersionResolveOptions { AllowPreRelease = true },
                TestContext.Current.CancellationToken);

            wildcard.ShouldNotBeNull();
            wildcard.Reference.Version.ShouldBe(expected);
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }
}
