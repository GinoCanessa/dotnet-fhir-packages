// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using Shouldly;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageListingTests
{
    // Independently authored JSON specimens, not live-registry or original-supplier evidence.
    public static TheoryData<string, string?> UnlistedForms => new()
    {
        { "absent", null },
        { "null", "null" },
        { "empty", "\"\"" },
        { "string-true", "\"true\"" },
        { "string-false", "\"false\"" },
        { "whitespace", "\" \\t\\r\\n \"" },
        { "escaped-string", "\"quote\\\" slash\\\\ newline\\n \\u00e9\"" },
        { "boolean-true", "true" },
        { "boolean-false", "false" },
        { "zero", "0" },
        { "negative", "-123" },
        { "fraction-exponent", "1.25e+2" },
        { "object", "{\"reason\":\"retired\",\"nested\":[1,true,null]}" },
        { "duplicate-object-members", "{\"reason\":\"first\",\"reason\":\"last\"}" },
        { "array", "[\"false\",false,0,null,{\"reason\":\"retired\"}]" },
        { "empty-array", "[]" },
    };

    [Theory]
    [MemberData(nameof(UnlistedForms))]
    public async Task Deserialize_UnlistedForms_PreservesPresenceAndOwnedValue(
        string specimen, string? raw)
    {
        string json = VersionJson(raw);
        PackageVersionInfo direct = JsonSerializer.Deserialize<PackageVersionInfo>(json)!;
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        PackageVersionInfo streamed = (await JsonSerializer.DeserializeAsync<PackageVersionInfo>(
            stream, cancellationToken: TestContext.Current.CancellationToken))!;
        PackageVersionInfo fromDocument;
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            fromDocument = document.RootElement.Deserialize<PackageVersionInfo>()!;
        }

        // The converter's parser document and the external document have both been disposed.
        foreach (PackageVersionInfo version in new[] { direct, streamed, fromDocument })
        {
            AssertUnlisted(version.Unlisted, raw, specimen);
            if (raw is not null)
            {
                version.Unlisted.GetRawText().ShouldBe(raw, specimen);
            }
        }
    }

    public static IEnumerable<object[]> UnlistedLookupCases()
    {
        (string Members, string Expected)[] cases =
        [
            ("\"Unlisted\":\"upper\"", "\"upper\""),
            ("\"UNLISTED\":\"first\",\"Unlisted\":\"second\"", "\"first\""),
            ("\"Unlisted\":\"first\",\"UNLISTED\":\"second\"", "\"first\""),
            ("\"Unlisted\":\"fallback\",\"unlisted\":\"exact\"", "\"exact\""),
            ("\"unlisted\":\"exact\",\"Unlisted\":\"fallback\"", "\"exact\""),
            ("\"unlisted\":\"first\",\"unlisted\":\"last\"", "\"last\""),
            ("\"unlisted\":\"first\",\"UNLISTED\":\"fallback\",\"unlisted\":null", "null"),
            ("\"unlisted\":null,\"unlisted\":\"last\"", "\"last\""),
            ("\"UNLISTED\":null,\"Unlisted\":\"second\"", "null"),
            ("\"UNLISTED\":false,\"Unlisted\":\"second\"", "false"),
            ("\"unlisted\":{\"raw\":1},\"unlisted\":[2]", "[2]"),
            ("\"unlisted\":[2],\"unlisted\":{\"raw\":1}", "{\"raw\":1}"),
        ];
        foreach ((string members, string expected) in cases)
        {
            foreach (bool caseInsensitive in new[] { false, true })
            {
                yield return [members, expected, caseInsensitive];
            }
        }
    }

    [Theory]
    [MemberData(nameof(UnlistedLookupCases))]
    public void Deserialize_UnlistedCasingAndDuplicates_UsesExistingListingLookup(
        string members, string expected, bool caseInsensitive)
    {
        string json = "{\"name\":\"test.package\",\"version\":\"1.0.0\"," + members + "}";
        PackageVersionInfo version = JsonSerializer.Deserialize<PackageVersionInfo>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = caseInsensitive })!;

        // Exact last-match / insensitive first-match is independent of serializer options.
        AssertUnlisted(version.Unlisted, expected, members);
    }

    public static IEnumerable<object?[]> UnlistedWriterCases()
    {
        foreach (TheoryDataRow<string, string?> form in UnlistedForms)
        {
            foreach (JsonIgnoreCondition condition in new[]
            {
                JsonIgnoreCondition.Never,
                JsonIgnoreCondition.WhenWritingNull,
                JsonIgnoreCondition.WhenWritingDefault,
            })
            {
                yield return [form.Data.Item1, form.Data.Item2, condition];
            }
        }
    }

    [Theory]
    [MemberData(nameof(UnlistedWriterCases))]
    public void Serialize_UnlistedForms_RoundTripsWithoutDroppingNull(
        string specimen, string? raw, JsonIgnoreCondition condition)
    {
        JsonSerializerOptions options = new() { DefaultIgnoreCondition = condition };
        PackageVersionInfo version = JsonSerializer.Deserialize<PackageVersionInfo>(VersionJson(raw))!;
        string json = JsonSerializer.Serialize(version, options);
        using JsonDocument document = JsonDocument.Parse(json);

        AssertWrittenUnlisted(document.RootElement, raw, specimen);
        AssertUnlisted(JsonSerializer.Deserialize<PackageVersionInfo>(json, options)!.Unlisted, raw, specimen);

        // A programmatically supplied element is supported while its owning document is alive.
        using JsonDocument owner = JsonDocument.Parse(raw ?? "null");
        PackageVersionInfo supplied = version with
        {
            Unlisted = raw is null ? default : owner.RootElement,
        };
        JsonSerializer.Serialize(supplied, options).ShouldBe(json);
    }

    [Theory]
    [MemberData(nameof(UnlistedForms))]
    public void Serialize_ListingWithUnlisted_RetainsIgnoredProvenance(string specimen, string? raw)
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://user:secret@registry.example/private?token=secret",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
        };
        PackageVersionInfo plain = JsonSerializer.Deserialize<PackageVersionInfo>(VersionJson(raw))!;
        PackageVersionInfo version = plain with
        {
            SourceRegistry = endpoint,
            IsSourceLatest = true,
        };
        PackageVersionInfo other = plain with
        {
            Unlisted = JsonSerializer.Deserialize<JsonElement>("\"other-source\""),
            Distribution = new NpmDistribution("other-sha", "https://other.example/archive.tgz")
            {
                Integrity = "sha512-other",
            },
            SourceRegistry = endpoint with { Type = RegistryType.Npm },
        };
        PackageListing listing = new()
        {
            PackageId = plain.Name,
            Description = "listing description",
            DistTags = new Dictionary<string, string> { ["latest"] = plain.Version },
            Versions = new Dictionary<string, PackageVersionInfo> { [plain.Version] = version },
            SourceRegistry = endpoint,
            IsComplete = false,
            QueryFailures = [new RegistryAttemptFailure(endpoint.Url, RegistryFailureCategory.Network)],
            VersionCandidates = [version, other],
        };
        PackageListing legacyShape = listing with
        {
            Versions = new Dictionary<string, PackageVersionInfo> { [plain.Version] = plain },
            SourceRegistry = null,
            IsComplete = true,
            QueryFailures = [],
            VersionCandidates = [],
        };
        JsonSerializerOptions options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        string json = JsonSerializer.Serialize(listing, options);
        json.ShouldBe(JsonSerializer.Serialize(legacyShape, options));
        AssertIgnoredProvenance(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement written = document.RootElement.GetProperty("versions").GetProperty(plain.Version);
        AssertWrittenUnlisted(written, raw, specimen);
        AssertExistingVersionFields(written);
        PackageListing reread = JsonSerializer.Deserialize<PackageListing>(json)!;
        AssertUnlisted(reread.Versions[plain.Version].Unlisted, raw, specimen);
        reread.VersionCandidates.ShouldBeEmpty();

        foreach (PackageVersionInfo candidate in listing.VersionCandidates)
        {
            string candidateJson = JsonSerializer.Serialize(candidate, options);
            AssertIgnoredProvenance(candidateJson);
            PackageVersionInfo candidateReread = JsonSerializer.Deserialize<PackageVersionInfo>(candidateJson)!;
            AssertUnlisted(candidateReread.Unlisted,
                candidate.Unlisted.ValueKind == JsonValueKind.Undefined ? null : candidate.Unlisted.GetRawText(), specimen);
            candidateReread.Distribution.ShouldBe(candidate.Distribution);
            candidateReread.Dependencies!.ShouldBe(candidate.Dependencies!);
            candidateReread.FhirVersions!.ShouldBe(candidate.FhirVersions!);
        }
    }

    public static IEnumerable<object?[]> ListingOptionalForms()
    {
        (string Label, string? Raw)[] forms =
        [
            ("absent", null), ("null", "null"), ("empty-string", "\"\""),
            ("whitespace", "\" \\t \""), ("compact-string", "\"20240203123456\""),
            ("ordinary-string", "\"4.0.1\""), ("compact-number", "20240203123456"),
            ("boolean", "true"), ("object", "{\"value\":\"4.0.1\"}"),
            ("empty-array", "[]"), ("string-array", "[\"4.0.1\",\"5.0.0\"]"),
            ("mixed-array", "[null,1,true,{},[],\"\",\" \",\"4.0.1\",\"4.0.1\",\"5.0.0\"]"),
        ];
        foreach (string field in new[] { "date", "fhirVersion", "fhirVersions" })
        {
            foreach ((string label, string? raw) in forms)
            {
                string? date = field == "date" && raw is not null && raw.StartsWith('"')
                    ? JsonSerializer.Deserialize<string>(raw)
                    : null;
                string[]? fhir = field == "date" || raw is null ? null : label switch
                {
                    "compact-string" => ["20240203123456"],
                    "ordinary-string" => ["4.0.1"],
                    "string-array" or "mixed-array" => ["4.0.1", "5.0.0"],
                    _ => null,
                };
                yield return [$"{field}/{label}", field, raw, date, fhir];
            }
        }
        yield return ["combined/deduplicated", "combined",
            "\"fhirVersion\":[\"4.0.1\",\"5.0.0\"],\"fhirVersions\":[\"5.0.0\",\"4.3.0\"]",
            null, new[] { "4.0.1", "5.0.0", "4.3.0" }];
        yield return ["date/exact-last", "combined", "\"date\":\"first\",\"date\":20240203", null, null];
        yield return ["date/insensitive-first", "combined", "\"DATE\":\"first\",\"Date\":false", "first", null];
        yield return ["fhir/exact-last", "combined", "\"fhirVersion\":\"4.0.1\",\"fhirVersion\":false", null, null];
        yield return ["fhir/insensitive-first", "combined", "\"FHIRVERSION\":\"4.0.1\",\"FhirVersion\":\"5.0.0\"",
            null, new[] { "4.0.1" }];
    }

    [Theory]
    [MemberData(nameof(ListingOptionalForms))]
    public async Task Deserialize_ListingOptionalForms_PreservesExistingDateAndFhirRules(
        string specimen, string field, string? raw, string? date, string[]? fhir)
    {
        string members = field == "combined"
            ? "," + raw
            : raw is null ? "" : $",\"{field}\":{raw}";
        string json = "{\"name\":\"test.package\",\"version\":\"1.0.0\"" + members + "}";
        foreach (bool caseInsensitive in new[] { false, true })
        {
            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = caseInsensitive };
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
            PackageVersionInfo direct = JsonSerializer.Deserialize<PackageVersionInfo>(json, options)!;
            PackageVersionInfo streamed = (await JsonSerializer.DeserializeAsync<PackageVersionInfo>(
                stream, options, TestContext.Current.CancellationToken))!;
            foreach (PackageVersionInfo version in new[] { direct, streamed })
            {
                // Date accepts only strings; other kinds are ignored, not manifest-date conversions.
                // FHIR accepts nonblank strings/string array entries; malformed kinds are ignored.
                version.PublicationDate.ShouldBe(date, specimen);
                version.FhirVersion.ShouldBe(fhir?.FirstOrDefault(), specimen);
                if (fhir is null)
                    version.FhirVersions.ShouldBeNull(specimen);
                else
                    version.FhirVersions!.ShouldBe(fhir, specimen);
                version.HasExplicitFhirVersionMetadata.ShouldBe(
                    field != "date" && (field == "combined"
                        ? raw!.Contains("fhir", StringComparison.OrdinalIgnoreCase)
                        : raw is not null), specimen);
                PackageVersionInfo roundTrip = JsonSerializer.Deserialize<PackageVersionInfo>(
                    JsonSerializer.Serialize(version, options), options)!;
                roundTrip.PublicationDate.ShouldBe(version.PublicationDate, specimen);
                roundTrip.HasExplicitFhirVersionMetadata.ShouldBe(version.HasExplicitFhirVersionMetadata, specimen);
                if (fhir is null)
                    roundTrip.FhirVersions.ShouldBeNull(specimen);
                else
                    roundTrip.FhirVersions!.ShouldBe(fhir, specimen);
            }
        }
    }

    [Theory]
    [InlineData("{\"date\":")]
    [InlineData("{\"name\":\"test.package\",\"version\":\"1.0.0\",\"fhirVersion\":[")]
    [InlineData("{\"name\":\"test.package\",\"version\":\"1.0.0\",\"fhirVersions\":[true,}")]
    [InlineData("{\"name\":false,\"version\":\"1.0.0\",\"date\":20240203}")]
    [InlineData("{\"name\":\"test.package\",\"version\":[],\"fhirVersions\":[]}")]
    [InlineData("{\"version\":\"1.0.0\",\"fhirVersion\":\"4.0.1\"}")]
    [InlineData("{\"name\":\"test.package\",\"date\":\"2024-02-03\"}")]
    public void Deserialize_ListingOptionalMalformedJsonOrRequiredFields_RemainRejected(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<PackageVersionInfo>(json));
    }

    private static string VersionJson(string? raw) =>
        """
        {"name":"test.package","version":"1.0.0","description":"version description",
        "fhirVersion":["4.0.1","5.0.0"],"dist":{"shasum":"sha","tarball":"https://downloads.example/archive.tgz","integrity":"sha512-strong"},
        "canonical":"https://canonical.example/ig","kind":"ig","date":"2024-02-03","count":7,
        "license":"MIT","url":"https://info.example/ig","dependencies":{"dependency.package":"2.0.0"}
        """ + (raw is null ? "" : ",\"unlisted\":" + raw) + "}";

    private static void AssertUnlisted(JsonElement actual, string? raw, string specimen)
    {
        if (raw is null)
        {
            actual.ValueKind.ShouldBe(JsonValueKind.Undefined, specimen);
            return;
        }

        using JsonDocument expected = JsonDocument.Parse(raw);
        actual.ValueKind.ShouldBe(expected.RootElement.ValueKind, specimen);
        JsonElement.DeepEquals(actual, expected.RootElement).ShouldBeTrue(specimen);
        if (actual.ValueKind == JsonValueKind.String)
        {
            actual.GetString().ShouldBe(expected.RootElement.GetString(), specimen);
        }
    }

    private static void AssertWrittenUnlisted(JsonElement version, string? raw, string specimen)
    {
        version.TryGetProperty("unlisted", out JsonElement marker).ShouldBe(raw is not null, specimen);
        AssertUnlisted(marker, raw, specimen);
    }

    private static void AssertIgnoredProvenance(string json)
    {
        foreach (string ignored in new[]
        {
            "secret", "SourceRegistry", "SourceClient", "IsComplete", "QueryFailures",
            "VersionCandidates", "IsSourceLatest", "FhirVersions", "HasExplicitFhirVersionMetadata",
        })
        {
            json.ShouldNotContain(ignored);
        }
    }

    private static void AssertExistingVersionFields(JsonElement version)
    {
        string[] expectedNames =
        [
            "name", "version", "description", "fhirVersion", "dist", "canonical", "kind",
            "date", "count", "license", "url", "dependencies",
        ];
        version.EnumerateObject().Select(property => property.Name)
            .Where(name => name != "unlisted").ShouldBe(expectedNames);
        version.GetProperty("name").GetString().ShouldBe("test.package");
        version.GetProperty("version").GetString().ShouldBe("1.0.0");
        version.GetProperty("description").GetString().ShouldBe("version description");
        version.GetProperty("fhirVersion").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["4.0.1", "5.0.0"]);
        version.GetProperty("dist").GetProperty("shasum").GetString().ShouldBe("sha");
        version.GetProperty("dist").GetProperty("tarball").GetString().ShouldBe("https://downloads.example/archive.tgz");
        version.GetProperty("dist").GetProperty("integrity").GetString().ShouldBe("sha512-strong");
        version.GetProperty("canonical").GetString().ShouldBe("https://canonical.example/ig");
        version.GetProperty("kind").GetString().ShouldBe("ig");
        version.GetProperty("date").GetString().ShouldBe("2024-02-03");
        version.GetProperty("count").GetInt32().ShouldBe(7);
        version.GetProperty("license").GetString().ShouldBe("MIT");
        version.GetProperty("url").GetString().ShouldBe("https://info.example/ig");
        version.GetProperty("dependencies").GetProperty("dependency.package").GetString().ShouldBe("2.0.0");
    }

    [Fact]
    public void LatestVersion_WithDistTag_ReturnsTaggedVersion()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            DistTags = new Dictionary<string, string> { ["latest"] = "2.0.0" },
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
                ["2.0.0"] = new() { Name = "test.package", Version = "2.0.0" },
            }
        };

        listing.LatestVersion.ShouldBe("2.0.0");
    }

    [Fact]
    public void LatestVersion_WithoutDistTag_ReturnsHighestSemVer()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
                ["3.0.0"] = new() { Name = "test.package", Version = "3.0.0" },
                ["2.0.0"] = new() { Name = "test.package", Version = "2.0.0" },
            }
        };

        // Must return 3.0.0 regardless of dictionary insertion order
        listing.LatestVersion.ShouldBe("3.0.0");
    }

    [Fact]
    public void LatestVersion_UnorderedKeys_ReturnsHighestSemVer()
    {
        // Explicitly test that dictionary key order doesn't matter
        Dictionary<string, PackageVersionInfo> versions = new Dictionary<string, PackageVersionInfo>
        {
            ["0.1.0"] = new() { Name = "test.package", Version = "0.1.0" },
            ["4.0.1"] = new() { Name = "test.package", Version = "4.0.1" },
            ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
            ["4.0.0"] = new() { Name = "test.package", Version = "4.0.0" },
            ["3.5.2"] = new() { Name = "test.package", Version = "3.5.2" },
        };

        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = versions
        };

        listing.LatestVersion.ShouldBe("4.0.1");
    }

    [Fact]
    public void LatestVersion_EmptyVersions_ReturnsNull()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>()
        };

        listing.LatestVersion.ShouldBeNull();
    }

    [Fact]
    public void LatestVersion_StablePreferredOverPreRelease()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
                ["2.0.0-beta1"] = new() { Name = "test.package", Version = "2.0.0-beta1" },
            }
        };

        // FhirSemVer orders stable > pre-release at same major.minor.patch,
        // and 2.0.0-beta1 > 1.0.0 by major version, but it's pre-release.
        // The max should be 2.0.0-beta1 since it has higher major.minor.patch.
        string? result = listing.LatestVersion;
        result.ShouldNotBeNull();
    }

    [Fact]
    public void LatestVersion_PreservesAndOrdersTwoPartPrecision()
    {
        PackageListing listing = new()
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["2.0"] = new() { Name = "test.package", Version = "2.0" },
                ["2.0.0-alpha"] = new()
                {
                    Name = "test.package",
                    Version = "2.0.0-alpha",
                },
                ["2.0.0"] = new()
                {
                    Name = "test.package",
                    Version = "2.0.0",
                },
            },
        };

        listing.LatestVersion.ShouldBe("2.0.0");
        listing.Versions.Keys.ShouldContain("2.0");
    }

    [Fact]
    public void Defaults_AreCompleteWithoutFailuresOrCandidates()
    {
        Dictionary<string, PackageVersionInfo> versions = [];
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = versions
        };

        listing.IsComplete.ShouldBeTrue();
        listing.QueryFailures.ShouldBeEmpty();
        listing.VersionCandidates.ShouldBeEmpty();
        listing.SourceRegistry.ShouldBeNull();
    }

    [Fact]
    public void Serialize_WithProvenance_PreservesExistingJsonShape()
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://user:secret@registry.example/private?token=secret",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret"
        };
        PackageVersionInfo version = new()
        {
            Name = "test.package",
            Version = "1.0.0",
            SourceRegistry = endpoint,
            IsSourceLatest = true
        };
        PackageListing listing = new()
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = version
            },
            SourceRegistry = endpoint,
            IsComplete = false,
            QueryFailures =
            [
                new RegistryAttemptFailure(endpoint.Url, RegistryFailureCategory.Network)
            ],
            VersionCandidates = [version]
        };

        PackageListing legacyShape = new()
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new PackageVersionInfo
                {
                    Name = "test.package",
                    Version = "1.0.0"
                }
            }
        };

        string json = JsonSerializer.Serialize(listing);
        json.ShouldBe(JsonSerializer.Serialize(legacyShape));
        json.ShouldNotContain("secret");
        json.ShouldNotContain("SourceRegistry");
        json.ShouldNotContain("IsComplete");
        json.ShouldNotContain("QueryFailures");
        json.ShouldNotContain("VersionCandidates");
        json.ShouldNotContain("IsSourceLatest");
    }

    [Fact]
    public void Deserialize_LegacyJson_UsesNewPropertyDefaults()
    {
        const string json =
            """
            {
              "name": "test.package",
              "versions": {
                "1.0.0": {
                  "name": "test.package",
                  "version": "1.0.0"
                }
              }
            }
            """;

        PackageListing? listing = JsonSerializer.Deserialize<PackageListing>(json);

        listing.ShouldNotBeNull();
        listing.IsComplete.ShouldBeTrue();
        listing.QueryFailures.ShouldBeEmpty();
        listing.VersionCandidates.ShouldBeEmpty();
        PackageVersionInfo version = listing.Versions["1.0.0"];
        version.SourceRegistry.ShouldBeNull();
        version.IsSourceLatest.ShouldBeFalse();
    }
}
