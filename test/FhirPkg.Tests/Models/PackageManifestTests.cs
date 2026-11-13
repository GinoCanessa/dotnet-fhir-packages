// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

[Collection("EnvironmentVariable")]
public class PackageManifestTests
{
    private const string FullManifestJson = """
        {
            "name": "hl7.fhir.r4.core",
            "version": "4.0.1",
            "description": "FHIR R4 Core definitions",
            "license": "CC0-1.0",
            "author": "HL7 International",
            "homepage": "https://hl7.org/fhir/R4/",
            "canonical": "http://hl7.org/fhir",
            "fhirVersions": ["4.0.1"],
            "type": "fhir.core",
            "title": "FHIR R4",
            "dependencies": {
                "hl7.fhir.r4.expansions": "4.0.1"
            },
            "keywords": ["fhir", "r4"]
        }
        """;

    private const string MinimalManifestJson = """
        {
            "name": "minimal.package",
            "version": "1.0.0"
        }
        """;

    private const string ProjectedAuthor = "Example Author <author@example.test> (https://example.test/)";

    private const string ObjectAuthorManifestJson = """
        {
            "name": "test.package",
            "version": "1.0.0",
            "author": {
                "name": "Example Author",
                "email": "author@example.test",
                "url": "https://example.test/"
            }
        }
        """;

    private const string StringAuthorManifestJson = """
        {
            "name": "test.package",
            "version": "1.0.0",
            "author": "Example Author <author@example.test> (https://example.test/)"
        }
        """;

    private static readonly string[] s_manifestReaders = ["String", "Stream", "DefaultSerializer"];

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_FullObjectAuthor_ProjectsToString(string reader)
    {
        PackageManifest manifest = DeserializeManifest(ObjectAuthorManifestJson, reader);

        manifest.Name.ShouldBe("test.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Author.ShouldBe(ProjectedAuthor);
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_ProjectedStringAuthor_ReturnsUnchanged(string reader)
    {
        PackageManifest manifest = DeserializeManifest(StringAuthorManifestJson, reader);

        manifest.Name.ShouldBe("test.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Author.ShouldBe(ProjectedAuthor);
    }

    [Theory]
    [MemberData(nameof(AuthorForms))]
    public void Deserialize_AuthorForms_ReturnExpectedValue(string? authorJson, string? expected, string reader)
    {
        PackageManifest manifest = DeserializeManifest(CreateAuthorManifest(authorJson), reader);

        manifest.Name.ShouldBe("test.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Author.ShouldBe(expected);
        manifest.Description.ShouldBe("After author");
        manifest.Dependencies.ShouldNotBeNull();
        manifest.Dependencies["dependency.package"].ShouldBe("2.0.0");
    }

    public static IEnumerable<object?[]> AuthorForms()
    {
        (string? AuthorJson, string? Expected)[] cases =
        [
            (null, null),
            ("null", null),
            ("\"\"", ""),
            ("\"   \"", "   "),
            ("\"  Éxample 作者 <a+b@example.test> (https://example.test/✓)!\\t  \"",
                "  Éxample 作者 <a+b@example.test> (https://example.test/✓)!\t  "),
            ("""{"name":"Example Author","email":"author@example.test","url":"https://example.test/"}""", ProjectedAuthor),
            ("""{"name":"Example Author"}""", "Example Author"),
            ("""{"email":"author@example.test"}""", "<author@example.test>"),
            ("""{"url":"https://example.test/"}""", "(https://example.test/)"),
            ("""{"name":"Example Author","email":"author@example.test"}""", "Example Author <author@example.test>"),
            ("""{"name":"Example Author","url":"https://example.test/"}""", "Example Author (https://example.test/)"),
            ("""{"email":"author@example.test","url":"https://example.test/"}""", "<author@example.test> (https://example.test/)"),
            ("""{"url":"https://example.test/","email":"author@example.test","name":"Example Author"}""", ProjectedAuthor),
            ("{}", null),
            ("""{"name":null,"email":null,"url":null}""", null),
            ("""{"name":"","email":"","url":""}""", null),
            ("""{"name":null,"email":"","url":null}""", null),
            ("""{"name":"","email":"author@example.test","url":null}""", "<author@example.test>"),
            ("""{"name":"Example Author","email":null,"url":""}""", "Example Author"),
            ("""{"name":null,"email":"","url":"https://example.test/"}""", "(https://example.test/)"),
            ("""{"unknown":{"name":42,"nested":[true,{"email":false}]},"other":[null,1]}""", null),
            ("""{"name":"Example Author","unknown":{"name":42,"nested":[true,{"email":false}]},"email":"author@example.test","other":[null,1],"url":"https://example.test/"}""", ProjectedAuthor),
            ("""{"name":"   "}""", "   "),
            ("""{"email":"   "}""", "<   >"),
            ("""{"url":"   "}""", "(   )"),
            ("""{"name":"  Éxample 作者  ","email":" a+b@example.test ","url":" https://example.test/✓ "}""",
                "  Éxample 作者   < a+b@example.test > ( https://example.test/✓ )"),
        ];

        foreach ((string? authorJson, string? expected) in cases)
        {
            foreach (string reader in s_manifestReaders)
            {
                yield return [authorJson, expected, reader];
            }
        }
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_AuthorMemberCasing_RespectsSerializerOptions(string reader)
    {
        string json = CreateAuthorManifest(
            """{"Name":"Example Author","EMAIL":"author@example.test","Url":"https://example.test/"}""");

        PackageManifest manifest = DeserializeManifest(json, reader);

        manifest.Name.ShouldBe("test.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Author.ShouldBe(reader == "DefaultSerializer" ? null : ProjectedAuthor);
        manifest.Description.ShouldBe("After author");
        manifest.Dependencies.ShouldNotBeNull();

        string differentlyCasedManifest = json.Replace("\"description\"", "\"Description\"", StringComparison.Ordinal);
        DeserializeManifest(differentlyCasedManifest, reader).Description
            .ShouldBe(reader == "DefaultSerializer" ? null : "After author");

        string differentlyCasedInvalidMembers = CreateAuthorManifest("""{"Name":42,"EMAIL":{},"Url":[]}""");
        if (reader == "DefaultSerializer")
        {
            DeserializeManifest(differentlyCasedInvalidMembers, reader).Author.ShouldBeNull();
        }
        else
        {
            Should.Throw<JsonException>(() => DeserializeManifest(differentlyCasedInvalidMembers, reader))
                .Path.ShouldBe("$.author");
        }
    }

    [Theory]
    [MemberData(nameof(AuthorDuplicateMembers))]
    public void Deserialize_AuthorDuplicateMembers_UsesLastRecognizedValue(
        string authorJson,
        string? expected,
        string reader)
    {
        PackageManifest manifest = DeserializeManifest(CreateAuthorManifest(authorJson), reader);

        manifest.Author.ShouldBe(expected);
        manifest.Version.ShouldBe("1.0.0");
        manifest.Description.ShouldBe("After author");
    }

    public static IEnumerable<object?[]> AuthorDuplicateMembers()
    {
        (string AuthorJson, string? CaseSensitive, string? CaseInsensitive)[] cases =
        [
            ("""{"name":"First","name":"Last"}""", "Last", "Last"),
            ("""{"email":"first@test","email":"last@test"}""", "<last@test>", "<last@test>"),
            ("""{"url":"first","url":"last"}""", "(last)", "(last)"),
            ("""{"name":"First","email":"first@test","url":"first","url":"last","name":"Last","email":"last@test"}""",
                "Last <last@test> (last)", "Last <last@test> (last)"),
            ("""{"name":"First","name":null}""", null, null),
            ("""{"name":"First","name":""}""", null, null),
            ("""{"email":"first@test","email":null}""", null, null),
            ("""{"email":"first@test","email":""}""", null, null),
            ("""{"url":"first","url":null}""", null, null),
            ("""{"url":"first","url":""}""", null, null),
            ("""{"name":null,"name":"Last","email":"","email":"last@test","url":null,"url":"last"}""",
                "Last <last@test> (last)", "Last <last@test> (last)"),
            ("""{"name":"First","Name":"Last","email":"first@test","EMAIL":"last@test","url":"first","URL":"last"}""",
                "First <first@test> (first)", "Last <last@test> (last)"),
            ("""{"Name":"First","name":"Last"}""", "Last", "Last"),
            ("""{"name":"First","Name":null,"email":"first@test","EMAIL":"","url":"first","URL":null}""",
                "First <first@test> (first)", null),
            ("""{"name":"First","Name":"","email":"first@test","EMAIL":null,"url":"first","URL":""}""",
                "First <first@test> (first)", null),
            ("""{"name":"First","Name":null,"name":"Last"}""", "Last", "Last"),
        ];

        foreach ((string authorJson, string? caseSensitive, string? caseInsensitive) in cases)
        {
            foreach (string reader in s_manifestReaders)
            {
                yield return [authorJson, reader == "DefaultSerializer" ? caseSensitive : caseInsensitive, reader];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidAuthorShapes))]
    public void Deserialize_InvalidAuthorShapes_ThrowsJsonException(string authorJson, string reader)
    {
        JsonException exception = Should.Throw<JsonException>(
            () => DeserializeManifest(CreateAuthorManifest(authorJson), reader));

        exception.Path.ShouldBe("$.author");
    }

    public static IEnumerable<object?[]> InvalidAuthorShapes()
    {
        string[] invalidAuthors = ["42", "1.5", "true", "false", "[]", "[null]"];
        string[] members = ["name", "email", "url"];
        string[] invalidMembers = ["42", "true", "false", "{}", "[]"];
        foreach (string reader in s_manifestReaders)
        {
            foreach (string authorJson in invalidAuthors)
            {
                yield return [authorJson, reader];
            }

            foreach (string member in members)
            {
                foreach (string value in invalidMembers)
                {
                    yield return [$"{{\"{member}\":{value}}}", reader];
                    yield return [$"{{\"{member}\":{value},\"{member}\":\"Last\"}}", reader];
                    yield return [$"{{\"{member}\":{value},\"{member}\":null}}", reader];
                    yield return [$"{{\"{member}\":{value},\"{member}\":\"\"}}", reader];
                    yield return [$"{{\"{member}\":\"First\",\"{member}\":{value}}}", reader];
                }

                if (reader != "DefaultSerializer")
                {
                    string differentlyCasedMember = member.ToUpperInvariant();
                    yield return [$"{{\"{differentlyCasedMember}\":42,\"{member}\":\"Last\"}}", reader];
                    yield return [$"{{\"{member}\":true,\"{differentlyCasedMember}\":null}}", reader];
                }
            }
        }
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_ObjectAuthorMissingRequiredFields_StillThrows(string reader)
    {
        string[] invalidManifests =
        [
            """{"version":"1.0.0","author":{"name":"Example Author"}}""",
            """{"name":"test.package","author":{"name":"Example Author"}}""",
            """{"name":"test.package","version":"1.0.0","author":{"name":"Example Author"}""",
        ];

        foreach (string json in invalidManifests)
        {
            Should.Throw<JsonException>(() => DeserializeManifest(json, reader));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialize_ProjectedAuthor_EmitsString(bool defaultSerializer)
    {
        PackageManifest manifest = PackageManifest.Deserialize(ObjectAuthorManifestJson);

        string serialized = defaultSerializer ? JsonSerializer.Serialize(manifest) : manifest.Serialize();

        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement author = document.RootElement.GetProperty("author");
        author.ValueKind.ShouldBe(JsonValueKind.String);
        author.GetString().ShouldBe(ProjectedAuthor);
        PackageManifest.Deserialize(serialized).Author.ShouldBe(manifest.Author);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  Éxample 作者 <a+b@example.test> (https://example.test/✓)!\t  ")]
    [InlineData(ProjectedAuthor)]
    public void Serialize_StringAuthor_PreservesValue(string value)
    {
        PackageManifest manifest = PackageManifest.Deserialize(CreateAuthorManifest(JsonSerializer.Serialize(value)));

        string[] serializedForms = [manifest.Serialize(), JsonSerializer.Serialize(manifest)];
        foreach (string serialized in serializedForms)
        {
            using JsonDocument document = JsonDocument.Parse(serialized);
            JsonElement author = document.RootElement.GetProperty("author");
            author.ValueKind.ShouldBe(JsonValueKind.String);
            author.GetString().ShouldBe(value);
            PackageManifest.Deserialize(serialized).Author.ShouldBe(value);
            JsonSerializer.Deserialize<PackageManifest>(serialized)!.Author.ShouldBe(value);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"name":"","email":null,"url":""}""")]
    public void Serialize_NullAuthor_UsesExistingNullPolicy(string? authorJson)
    {
        PackageManifest manifest = PackageManifest.Deserialize(CreateAuthorManifest(authorJson));
        manifest.Author.ShouldBeNull();

        using JsonDocument helperDocument = JsonDocument.Parse(manifest.Serialize());
        helperDocument.RootElement.TryGetProperty("author", out _).ShouldBeFalse();

        using JsonDocument defaultDocument = JsonDocument.Parse(JsonSerializer.Serialize(manifest));
        defaultDocument.RootElement.GetProperty("author").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Deserialize_FullManifest_AllFieldsPopulated()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Name.ShouldBe("hl7.fhir.r4.core");
        manifest.Version.ShouldBe("4.0.1");
        manifest.Description.ShouldBe("FHIR R4 Core definitions");
        manifest.License.ShouldBe("CC0-1.0");
        manifest.Author.ShouldBe("HL7 International");
        manifest.Homepage.ShouldBe("https://hl7.org/fhir/R4/");
        manifest.Canonical.ShouldBe("http://hl7.org/fhir");
        manifest.FhirVersions!.ShouldContain("4.0.1");
        manifest.Type.ShouldBe("fhir.core");
        manifest.Title.ShouldBe("FHIR R4");
        manifest.Keywords!.ShouldContain("fhir");
    }

    [Fact]
    public void Deserialize_MinimalManifest_OnlyRequiredFields()
    {
        PackageManifest manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.Name.ShouldBe("minimal.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Description.ShouldBeNull();
        manifest.Dependencies.ShouldBeNull();
        manifest.FhirVersions.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_WithDependencies_ParsedCorrectly()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Dependencies.ShouldNotBeNull();
        manifest.Dependencies.Keys.ShouldContain("hl7.fhir.r4.expansions");
        manifest.Dependencies!["hl7.fhir.r4.expansions"].ShouldBe("4.0.1");
    }

    [Fact]
    public void Deserialize_CaseInsensitive_HandlesVariations()
    {
        string json = """
            {
                "Name": "case.test",
                "Version": "2.0.0",
                "Description": "Case test"
            }
            """;

        PackageManifest manifest = PackageManifest.Deserialize(json);

        manifest.Name.ShouldBe("case.test");
        manifest.Version.ShouldBe("2.0.0");
    }

    [Fact]
    public void InferredFhirRelease_FromFhirVersions_Correct()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.InferredFhirRelease.ShouldBe(FhirRelease.R4);
    }

    [Fact]
    public void InferredFhirRelease_FromDependencies_WhenNoFhirVersions()
    {
        string json = """
            {
                "name": "test.package",
                "version": "1.0.0",
                "dependencies": {
                    "hl7.fhir.r5.core": "5.0.0"
                }
            }
            """;

        PackageManifest manifest = PackageManifest.Deserialize(json);

        manifest.InferredFhirRelease.ShouldBe(FhirRelease.R5);
    }

    [Fact]
    public void InferredFhirRelease_NoInfo_ReturnsNull()
    {
        PackageManifest manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.InferredFhirRelease.ShouldBeNull();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        string serialized = manifest.Serialize();
        PackageManifest deserialized = PackageManifest.Deserialize(serialized);

        deserialized.Name.ShouldBe(manifest.Name);
        deserialized.Version.ShouldBe(manifest.Version);
        deserialized.Description.ShouldBe(manifest.Description);
        deserialized.License.ShouldBe(manifest.License);
        deserialized.Author.ShouldBe(manifest.Author);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        Func<PackageManifest> act = () => PackageManifest.Deserialize("not valid json");

        Should.Throw<JsonException>(() => act());
    }

    [Fact]
    public void SemVer_ParsesCorrectly()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.SemVer.ShouldNotBeNull();
        manifest.SemVer!.Major.ShouldBe(4);
        manifest.SemVer.Minor.ShouldBe(0);
        manifest.SemVer.Build.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deserialize_FileStream_RetriesReadsButNotMalformedJson(bool malformed)
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-replay-{Guid.NewGuid():N}.json");
        try
        {
            string json = malformed ? """{"name":"bad","version":}""" : MinimalManifestJson;
            File.WriteAllText(path, "skip" + json);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
            primitives.UseSystemFiles();
            primitives.Enqueue<int>("ReadBorrowed", call =>
            {
                call.Stream!.Read(call.Buffer.Span[..2]).ShouldBe(2);
                return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "ReadBorrowed", path);
            });
            FileSystemContext context = primitives.CreateContext(retries: 1);

            if (malformed)
            {
                JsonException exception = Should.Throw<JsonException>(() => PackageManifest.Deserialize(caller, context));
                FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
            }
            else
            {
                PackageManifest.Deserialize(caller, context).Name.ShouldBe("minimal.package");
            }

            caller.CanRead.ShouldBeTrue();
            caller.Position.ShouldBe(caller.Length);
            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("ReadBorrowed").ShouldBe(3); // Failed block, recovered bytes, EOF; no parser restart.
            primitives.Count("SetPosition").ShouldBe(1);
            primitives.Calls.Single(call => call.Operation == "SetPosition").Offset.ShouldBe(4);
            primitives.Count("CloseFile").ShouldBe(0);
            primitives.Waits.ShouldBe([10]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_PublicFileStreamPreservesCurrentPositionAndOwnership()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-public-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "skip" + MinimalManifestJson);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;

            PackageManifest manifest = PackageManifest.Deserialize(caller);

            manifest.Name.ShouldBe("minimal.package");
            caller.Position.ShouldBe(caller.Length);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_ObjectAuthorFileStream_PreservesPositionAndOwnership()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-object-author-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "skip" + ObjectAuthorManifestJson);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;

            PackageManifest manifest = PackageManifest.Deserialize(caller);

            manifest.Name.ShouldBe("test.package");
            manifest.Version.ShouldBe("1.0.0");
            manifest.Author.ShouldBe(ProjectedAuthor);
            caller.Position.ShouldBe(caller.Length);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_ExhaustedReadKeepsPhysicalFailureRatherThanReparsing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-exhausted-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, MinimalManifestJson);
            using FileStream caller = File.OpenRead(path);
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
            primitives.UseSystemFiles();
            IOException failure = new IOException("required manifest read");
            primitives.Set<int>("ReadBorrowed", call => ScriptedFileSystemPrimitives.Fail<int>(failure, "read", call.Path));

            Should.Throw<IOException>(() => PackageManifest.Deserialize(caller, primitives.CreateContext(retries: 2)))
                .ShouldBeSameAs(failure);

            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("ReadBorrowed").ShouldBe(3);
            primitives.Count("SetPosition").ShouldBe(2);
            primitives.Waits.ShouldBe([10, 10]);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_PureAndProtectedPathsDoNotResolveEnvironment()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-context-{Guid.NewGuid():N}.json");
        string? original = Environment.GetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable);
        try
        {
            File.WriteAllText(path, MinimalManifestJson);
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
            primitives.UseSystemFiles();
            using Stream protectedStream = primitives.CreateContext().FileSystem.OpenProtectedRegularRead(
                path, TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, "invalid");

            PackageManifest.Deserialize(MinimalManifestJson).Name.ShouldBe("minimal.package");
            using MemoryStream memory = new MemoryStream(Encoding.UTF8.GetBytes(MinimalManifestJson));
            PackageManifest.Deserialize(memory).Name.ShouldBe("minimal.package");
            PackageManifest.Deserialize(protectedStream).Name.ShouldBe("minimal.package");
            Should.Throw<JsonException>(() => PackageManifest.Deserialize("malformed"));

            primitives.Count("PrepareBorrowedRead").ShouldBe(0);
            primitives.Waits.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, original);
            File.Delete(path);
        }
    }

    private static string CreateAuthorManifest(string? authorJson)
    {
        string authorProperty = authorJson is null ? string.Empty : $"\"author\": {authorJson},";
        return $$"""
            {
                "name": "test.package",
                {{authorProperty}}
                "version": "1.0.0",
                "description": "After author",
                "dependencies": {"dependency.package": "2.0.0"}
            }
            """;
    }

    private static PackageManifest DeserializeManifest(string json, string reader)
    {
        switch (reader)
        {
            case "String":
                return PackageManifest.Deserialize(json);
            case "Stream":
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return PackageManifest.Deserialize(stream);
                }
            case "DefaultSerializer":
                return JsonSerializer.Deserialize<PackageManifest>(json)
                    ?? throw new JsonException("Deserialization returned null.");
            default:
                throw new ArgumentOutOfRangeException(nameof(reader));
        }
    }
}
