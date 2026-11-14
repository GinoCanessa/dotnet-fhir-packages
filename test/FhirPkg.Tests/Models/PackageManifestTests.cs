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

    [Theory]
    [MemberData(nameof(RepositoryForms))]
    public void Deserialize_RepositoryForms_PreservesProjection(
        string? repositoryJson,
        string? type,
        string? url,
        string? directory,
        string reader)
    {
        PackageManifest manifest = DeserializeManifest(CreateMetadataManifest("repository", repositoryJson), reader);

        manifest.Repository.ShouldBe(repositoryJson is null or "null"
            ? null
            : new NpmRepository(type, url, directory));
        manifest.Description.ShouldBe("After metadata");
        manifest.Dependencies!["dependency.package"].ShouldBe("2.0.0");
    }

    public static IEnumerable<object?[]> RepositoryForms()
    {
        (string? Json, string? Type, string? Url, string? Directory)[] cases =
        [
            (null, null, null, null),
            ("null", null, null, null),
            ("\"github:example/repo\"", null, "github:example/repo", null),
            ("\"https://example.test/repo.git\"", null, "https://example.test/repo.git", null),
            ("\"\"", null, "", null),
            ("\"  Éxample/作者\\t  \"", null, "  Éxample/作者\t  ", null),
            ("""{"type":"git","url":"https://example.test/repo.git","directory":"src"}""",
                "git", "https://example.test/repo.git", "src"),
            ("{}", null, null, null),
            ("""{"type":null,"url":null,"directory":null}""", null, null, null),
            ("""{"type":"","url":"","directory":""}""", "", "", ""),
            ("""{"directory":" src ","url":" invalid URL ","type":" git "}""", " git ", " invalid URL ", " src "),
            ("""{"url":"github:example/repo","unknown":{"type":42,"nested":[true,null]},"other":[]}""",
                null, "github:example/repo", null),
        ];
        foreach ((string? json, string? type, string? url, string? directory) in cases)
        {
            foreach (string reader in s_manifestReaders)
            {
                yield return [json, type, url, directory, reader];
            }
        }
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_RepositoryMembers_RespectsCasingAndEveryOccurrence(string reader)
    {
        foreach (string member in new[] { "type", "url", "directory" })
        {
            string upper = member.ToUpperInvariant();
            (string Json, string? Expected)[] cases =
            [
                ($$"""{"{{member}}":"first","{{member}}":"last"}""", "last"),
                ($$"""{"{{member}}":"first","{{member}}":null}""", null),
                ($$"""{"{{member}}":"first","{{member}}":""}""", ""),
                ($$"""{"{{member}}":null,"{{member}}":"last"}""", "last"),
                ($$"""{"{{member}}":"","{{member}}":"last"}""", "last"),
                ($$"""{"{{member}}":"first","{{upper}}":"last"}""",
                    reader == "DefaultSerializer" ? "first" : "last"),
                ($$"""{"{{upper}}":"first","{{member}}":"last"}""", "last"),
                ($$"""{"{{member}}":"first","{{upper}}":null}""",
                    reader == "DefaultSerializer" ? "first" : null),
                ($$"""{"{{member}}":"first","{{upper}}":""}""",
                    reader == "DefaultSerializer" ? "first" : ""),
                ($$"""{"{{upper}}":42,"{{member}}":"last"}""", "last"),
            ];
            foreach ((string json, string? expected) in cases)
            {
                if (json.Contains(":42", StringComparison.Ordinal) && reader != "DefaultSerializer")
                {
                    Should.Throw<JsonException>(() => DeserializeManifest(
                        CreateMetadataManifest("repository", json), reader)).Path.ShouldBe("$.repository");
                    continue;
                }

                NpmRepository repository = DeserializeManifest(
                    CreateMetadataManifest("repository", json), reader).Repository!;
                GetRepositoryMember(repository, member).ShouldBe(expected);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deserialize_RepositoryAndDateCasing_UsesExplicitOptions(bool caseInsensitive)
    {
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = caseInsensitive };
        const string json = """
            {"name":"test.package","version":"1.0.0","Repository":{"TYPE":"git","URL":"raw","DIRECTORY":""},"Date":20240203}
            """;
        PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(json, options)!;

        manifest.Repository.ShouldBe(caseInsensitive ? new NpmRepository("git", "raw", "") : null);
        manifest.Date.ShouldBe(caseInsensitive ? "20240203" : null);
        const string invalid = """
            {"name":"test.package","version":"1.0.0","repository":{"URL":42,"url":null},"DATE":true}
            """;
        if (caseInsensitive)
        {
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<PackageManifest>(invalid, options))
                .Path.ShouldBe("$.repository");
        }
        else
        {
            JsonSerializer.Deserialize<PackageManifest>(invalid, options)!.Repository
                .ShouldBe(new NpmRepository(null, null, null));
        }

        string dateOnly = """{"name":"test.package","version":"1.0.0","date":"first","DATE":true}""";
        if (caseInsensitive)
        {
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<PackageManifest>(dateOnly, options))
                .Path.ShouldBe("$.DATE");
        }
        else
        {
            JsonSerializer.Deserialize<PackageManifest>(dateOnly, options)!.Date.ShouldBe("first");
        }
    }

    [Theory]
    [MemberData(nameof(CompactDateForms))]
    public void Deserialize_CompactDateForms_PreservesLexicalText(string? dateJson, string? expected, string reader)
    {
        PackageManifest manifest = DeserializeManifest(CreateMetadataManifest("date", dateJson), reader);

        manifest.Date.ShouldBe(expected);
        manifest.Description.ShouldBe("After metadata");
        manifest.Dependencies!["dependency.package"].ShouldBe("2.0.0");
    }

    public static IEnumerable<object?[]> CompactDateForms()
    {
        (string? Json, string? Expected)[] cases =
        [
            (null, null),
            ("null", null),
            ("20240203", "20240203"),
            ("20240203123456", "20240203123456"),
            ("99999999", "99999999"),
            ("99999999999999", "99999999999999"),
            ("\"20240203\"", "20240203"),
            ("\"20240203123456\"", "20240203123456"),
            ("\"00000000\"", "00000000"),
            ("\"00000000000000\"", "00000000000000"),
            ("\"2024-02-03T12:34:56+05:30\"", "2024-02-03T12:34:56+05:30"),
            ("\"not a date\"", "not a date"),
            ("\"\"", ""),
            ("\"  日期\\t  \"", "  日期\t  "),
        ];
        foreach ((string? json, string? expected) in cases)
        {
            foreach (string reader in s_manifestReaders)
            {
                yield return [json, expected, reader];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRepositoryOrDateForms))]
    public void Deserialize_InvalidRepositoryOrDate_ThrowsJsonException(string field, string value, string reader)
    {
        JsonException exception = Should.Throw<JsonException>(
            () => DeserializeManifest(CreateMetadataManifest(field, value), reader));

        exception.Path.ShouldBe($"$.{field}");
    }

    public static IEnumerable<object?[]> InvalidRepositoryOrDateForms()
    {
        foreach (string reader in s_manifestReaders)
        {
            foreach (string value in new[] { "42", "1.5", "true", "false", "[]", "[null]" })
            {
                yield return ["repository", value, reader];
            }

            foreach (string member in new[] { "type", "url", "directory" })
            {
                foreach (string value in new[] { "42", "true", "false", "{}", "[]" })
                {
                    foreach (string members in new[]
                    {
                        $"\"{member}\":{value}",
                        $"\"{member}\":{value},\"{member}\":\"last\"",
                        $"\"{member}\":{value},\"{member}\":null",
                        $"\"{member}\":{value},\"{member}\":\"\"",
                        $"\"{member}\":\"first\",\"{member}\":{value}",
                    })
                    {
                        yield return ["repository", "{" + members + "}", reader];
                    }
                }
            }

            foreach (string value in new[]
            {
                "-20240203", "-20240203123456", "-1234567", "-1234567890123",
                "202402.3", "123456789012.3", "20240203.0", "20240203123456.0",
                "20240203e0", "20240203123456E0", "2.0240203e7", "2024e003", "1234567890e+03", "2024020312e+003",
                "0", "1234567", "123456789", "1234567890123", "123456789012345",
                "true", "false", "[]", "[null]", "{}",
            })
            {
                yield return ["date", value, reader];
            }
        }
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_DuplicateRepositoryOrDate_ValidatesEveryOccurrence(string reader)
    {
        foreach ((string field, string valid, string invalid) in new[]
        {
            ("repository", "\"github:example/repo\"", "true"),
            ("repository", """{"url":"raw"}""", """{"url":42}"""),
            ("date", "20240203123456", "20240203e0"),
        })
        {
            foreach (string later in new[] { valid, "null", "\"\"" })
            {
                foreach (string properties in new[]
                {
                    $"\"{field}\":{invalid},\"{field}\":{later}",
                    $"\"{field}\":{later},\"{field}\":{invalid}",
                })
                {
                    Should.Throw<JsonException>(() => DeserializeManifest(
                        CreateMetadataProperties(properties), reader)).Path.ShouldBe($"$.{field}");
                }
            }

            foreach ((string first, string last) in new[]
            {
                (valid, "null"), ("null", valid), (valid, "\"\""), ("\"\"", valid), (valid, valid),
            })
            {
                PackageManifest manifest = DeserializeManifest(CreateMetadataProperties(
                    $"\"{field}\":{first},\"{field}\":{last}"), reader);
                PackageManifest expected = DeserializeManifest(CreateMetadataManifest(field, last), reader);
                manifest.Repository.ShouldBe(expected.Repository);
                manifest.Date.ShouldBe(expected.Date);
            }

            foreach (string properties in new[]
            {
                $"\"{field.ToUpperInvariant()}\":{invalid},\"{field}\":null",
                $"\"{field}\":null,\"{field.ToUpperInvariant()}\":{invalid}",
            })
            {
                if (reader == "DefaultSerializer")
                    GetInventoryValue(DeserializeManifest(CreateMetadataProperties(properties), reader), field).ShouldBeNull();
                else
                    Should.Throw<JsonException>(() => DeserializeManifest(CreateMetadataProperties(properties), reader));
            }

            foreach (string clearing in new[] { "null", "\"\"" })
            {
                foreach (string properties in new[]
                {
                    $"\"{field}\":{valid},\"{field.ToUpperInvariant()}\":{clearing}",
                    $"\"{field.ToUpperInvariant()}\":{valid},\"{field}\":{clearing}",
                })
                {
                    PackageManifest actual = DeserializeManifest(CreateMetadataProperties(properties), reader);
                    string expectedLast = reader == "DefaultSerializer"
                        && properties.StartsWith($"\"{field}\":", StringComparison.Ordinal) ? valid : clearing;
                    PackageManifest expected = DeserializeManifest(CreateMetadataManifest(field, expectedLast), reader);
                    actual.Repository.ShouldBe(expected.Repository);
                    actual.Date.ShouldBe(expected.Date);
                }
            }
        }
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_RepositoryOrDateMalformedJsonAndMissingRequiredFields_Throws(string reader)
    {
        foreach (string json in new[]
        {
            """{"version":"1.0.0","repository":"github:example/repo","date":20240203}""",
            """{"name":"test.package","repository":{},"date":20240203}""",
            """{"name":"test.package","version":"1.0.0","repository":{"url":}}""",
            """{"name":"test.package","version":"1.0.0","date":20240203""",
            """{"name":"test.package","version":"1.0.0","date":+20240203}""",
            """{"name":"test.package","version":"1.0.0","date":020240203}""",
            """{"name":"test.package","version":"1.0.0","date":２０２４０２０３}""",
        })
        {
            Should.Throw<JsonException>(() => DeserializeManifest(json, reader));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialize_RepositoryAndDate_UsesExistingPublicTypes(bool defaultSerializer)
    {
        foreach (string repository in new[]
        {
            "\"github:example/repo\"",
            """{"type":"git","url":" raw ","directory":""}""",
            """{"type":null,"url":"","directory":null}""",
            "null",
        })
        {
            foreach (string date in new[] { "20240203", "20240203123456", "\"ordinary date\"", "null" })
            {
                PackageManifest manifest = DeserializeManifest(CreateMetadataProperties(
                    $"\"repository\":{repository},\"date\":{date}"), "String");
                NpmRepository? publicRepository = manifest.Repository;
                string? publicDate = manifest.Date;
                string json = defaultSerializer ? JsonSerializer.Serialize(manifest) : manifest.Serialize();
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (publicRepository is null)
                {
                    root.TryGetProperty("repository", out JsonElement value).ShouldBe(defaultSerializer);
                    if (defaultSerializer)
                        value.ValueKind.ShouldBe(JsonValueKind.Null);
                }
                else
                {
                    JsonElement value = root.GetProperty("repository");
                    value.ValueKind.ShouldBe(JsonValueKind.Object);
                    using JsonDocument expected = JsonDocument.Parse(JsonSerializer.Serialize(
                        publicRepository,
                        new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = defaultSerializer
                                ? System.Text.Json.Serialization.JsonIgnoreCondition.Never
                                : System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                        }));
                    JsonElement.DeepEquals(value, expected.RootElement).ShouldBeTrue();
                    value.GetProperty("url").GetString().ShouldBe(publicRepository.Url);
                }

                if (publicDate is null)
                {
                    root.TryGetProperty("date", out JsonElement value).ShouldBe(defaultSerializer);
                    if (defaultSerializer)
                        value.ValueKind.ShouldBe(JsonValueKind.Null);
                }
                else
                {
                    root.GetProperty("date").ValueKind.ShouldBe(JsonValueKind.String);
                    root.GetProperty("date").GetString().ShouldBe(publicDate);
                }

                foreach (string reader in s_manifestReaders)
                {
                    PackageManifest reopened = DeserializeManifest(json, reader);
                    reopened.Repository.ShouldBe(publicRepository);
                    reopened.Date.ShouldBe(publicDate);
                }
            }
        }
    }

    // Raw specimens below are independently authored local controls. FullManifestJson,
    // AuthorForms and the retained inference tests provide repository-example provenance,
    // not independently observed Firely/live-registry/corpus evidence (all unverified).
    [Theory]
    [MemberData(nameof(OptionalFieldInventory))]
    public void Deserialize_OptionalFieldInventory_PreservesBaselineContracts(
        string row,
        string control,
        string? value,
        string? expectedJson,
        bool rejected,
        string reader)
    {
        string json = CreateInventoryManifest(row, value);
        if (rejected)
        {
            Should.Throw<JsonException>(() => DeserializeManifest(json, reader), $"{row}/{control}/{reader}");
            return;
        }

        PackageManifest manifest = DeserializeManifest(json, reader);
        manifest.Name.ShouldBe("test.package");
        manifest.Version.ShouldBe("1.0.0");
        object? actual = GetInventoryValue(manifest, row);
        using JsonDocument actualDocument = JsonDocument.Parse(JsonSerializer.Serialize(actual));
        using JsonDocument expectedDocument = JsonDocument.Parse(expectedJson ?? "null");
        JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement)
            .ShouldBeTrue($"{row}/{control}/{reader}");
        if (row == "fhirVersions")
        {
            manifest.InferredFhirRelease.ShouldBe(actual is null ? null : FhirRelease.R4);
        }
    }

    public static IEnumerable<object?[]> OptionalFieldInventory()
    {
        (string Row, string Valid, string Expected, string[] Invalid)[] rows =
        [
            ("description", "\"description\"", "\"description\"", ["42", "true", "{}", "[]"]),
            ("license", "\"CC0-1.0\"", "\"CC0-1.0\"", ["42", "true", "{}", "[]"]),
            ("homepage", "\"https://example.test/\"", "\"https://example.test/\"", ["42", "true", "{}", "[]"]),
            ("canonical", "\"https://example.test/fhir\"", "\"https://example.test/fhir\"", ["42", "true", "{}", "[]"]),
            ("type", "\"ig\"", "\"ig\"", ["42", "true", "{}", "[]"]),
            ("title", "\"Example IG\"", "\"Example IG\"", ["42", "true", "{}", "[]"]),
            ("jurisdiction", "\"US\"", "\"US\"", ["42", "true", "{}", "[]"]),
            ("author", """{"name":"Example","email":"e@test","url":"raw"}""", "\"Example <e@test> (raw)\"",
                ["42", "true", "[]", "[null]"]),
            ("author.name", "\"Example\"", "\"Example\"", ["42", "true", "{}", "[]"]),
            ("author.email", "\"e@test\"", "\"<e@test>\"", ["42", "true", "{}", "[]"]),
            ("author.url", "\"raw\"", "\"(raw)\"", ["42", "true", "{}", "[]"]),
            ("repository", """{"type":"git","url":"raw","directory":"src"}""",
                """{"type":"git","url":"raw","directory":"src"}""", ["42", "true", "[]", "[null]"]),
            ("repository.type", "\"git\"", "\"git\"", ["42", "true", "{}", "[]"]),
            ("repository.url", "\"raw\"", "\"raw\"", ["42", "true", "{}", "[]"]),
            ("repository.directory", "\"src\"", "\"src\"", ["42", "true", "{}", "[]"]),
            ("date", "20240203123456", "\"20240203123456\"", ["42", "true", "{}", "[]"]),
            ("dependencies", """{"dependency.package":"2.0.0"}""", """{"dependency.package":"2.0.0"}""",
                ["42", "true", "[]", "\"x\"", """{"bad":42}""", """{"bad":true}""", """{"bad":{}}""", """{"bad":[]}"""]),
            ("devDependencies", """{"dev.package":"2.0.0"}""", """{"dev.package":"2.0.0"}""",
                ["42", "true", "[]", "\"x\"", """{"bad":42}""", """{"bad":true}""", """{"bad":{}}""", """{"bad":[]}"""]),
            ("dist-tags", """{"latest":"1.0.0"}""", """{"latest":"1.0.0"}""",
                ["42", "true", "[]", "\"x\"", """{"bad":42}""", """{"bad":true}""", """{"bad":{}}""", """{"bad":[]}"""]),
            ("keywords", """["fhir","ig"]""", """["fhir","ig"]""",
                ["42", "true", "{}", "\"x\"", "[42]", "[true]", "[{}]", "[[]]"]),
            ("fhirVersions", """["4.0.1"]""", """["4.0.1"]""",
                ["42", "true", "{}", "\"x\"", "[42]", "[true]", "[{}]", "[[]]"]),
            ("dist", """{"shasum":"sha1","tarball":"https://example.test/p.tgz","integrity":"sha256-value"}""",
                """{"shasum":"sha1","tarball":"https://example.test/p.tgz","integrity":"sha256-value"}""",
                ["42", "true", "[]", "\"x\""]),
            ("dist.shasum", "\"sha1\"", "\"sha1\"", ["42", "true", "{}", "[]"]),
            ("dist.tarball", "\"https://example.test/p.tgz\"", "\"https://example.test/p.tgz\"", ["42", "true", "{}", "[]"]),
            ("dist.integrity", "\"sha256-value\"", "\"sha256-value\"", ["42", "true", "{}", "[]"]),
            ("unknown", """{"opaque":[1,true,null,{"nested":"unchanged"}]}""", "null", []),
        ];
        foreach ((string row, string valid, string expected, string[] invalid) in rows)
        {
            foreach (string reader in s_manifestReaders)
            {
                yield return [row, "absent", null, null, false, reader];
                yield return [row, "null", "null", null, false, reader];
                yield return [row, "known-valid", valid, expected, false, reader];
                foreach (string shape in invalid)
                {
                    yield return [row, "malformed-shape:" + shape, shape, null, true, reader];
                }

                if (row is "dependencies" or "devDependencies" or "dist-tags")
                {
                    yield return [row, "null-dictionary-value", """{"nullable":null}""",
                        """{"nullable":null}""", false, reader];
                    yield return [row, "empty-dictionary", "{}", "{}", false, reader];
                }
                else if (row is "keywords" or "fhirVersions")
                {
                    string list = row == "fhirVersions" ? """["4.0.1",null]""" : """["fhir",null]""";
                    yield return [row, "null-list-element", list, list, false, reader];
                }
                else if (row == "unknown")
                {
                    foreach (string ignored in new[] { "42", "true", "[]", "\"raw\"" })
                        yield return [row, "ignored:" + ignored, ignored, null, false, reader];
                }
            }
        }
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Stream")]
    [InlineData("DefaultSerializer")]
    public void Deserialize_NullCollectionValues_PreservesBaselineInferenceContracts(string reader)
    {
        PackageManifest manifest = DeserializeManifest("""
            {"name":"test.package","version":"1.0.0","fhirVersions":[null],
             "dependencies":{"hl7.fhir.r5.core":null},"keywords":[null]}
            """, reader);

        manifest.FhirVersions![0].ShouldBeNull();
        manifest.Keywords![0].ShouldBeNull();
        manifest.Dependencies!["hl7.fhir.r5.core"].ShouldBeNull();
        // The baseline reader accepts a null first element, but inference rejects it.
        Should.Throw<ArgumentNullException>(() => manifest.InferredFhirRelease).ParamName.ShouldBe("version");
        PackageManifest dependenciesOnly = manifest with { FhirVersions = null };
        dependenciesOnly.InferredFhirRelease.ShouldBe(FhirRelease.R5);
    }

    private static object? GetInventoryValue(PackageManifest manifest, string row) => row switch
    {
        "description" => manifest.Description,
        "license" => manifest.License,
        "homepage" => manifest.Homepage,
        "canonical" => manifest.Canonical,
        "type" => manifest.Type,
        "title" => manifest.Title,
        "jurisdiction" => manifest.Jurisdiction,
        "author" or "author.name" or "author.email" or "author.url" => manifest.Author,
        "repository" => manifest.Repository,
        "repository.type" => manifest.Repository?.Type,
        "repository.url" => manifest.Repository?.Url,
        "repository.directory" => manifest.Repository?.Directory,
        "date" => manifest.Date,
        "dependencies" => manifest.Dependencies,
        "devDependencies" => manifest.DevDependencies,
        "dist-tags" => manifest.DistTags,
        "keywords" => manifest.Keywords,
        "fhirVersions" => manifest.FhirVersions,
        "dist" => manifest.Distribution,
        "dist.shasum" => manifest.Distribution?.ShaSum,
        "dist.tarball" => manifest.Distribution?.TarballUrl,
        "dist.integrity" => manifest.Distribution?.Integrity,
        "unknown" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    private static string? GetRepositoryMember(NpmRepository repository, string member) => member switch
    {
        "type" => repository.Type,
        "url" => repository.Url,
        "directory" => repository.Directory,
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    private static string CreateInventoryManifest(string row, string? value)
    {
        if (value is null)
            return """{"name":"test.package","version":"1.0.0"}""";
        string[] parts = row.Split('.');
        string property = parts.Length == 1
            ? $"\"{row}\":{value}"
            : $"\"{parts[0]}\":{{\"{parts[1]}\":{value}}}";
        return "{\"name\":\"test.package\",\"version\":\"1.0.0\"," + property + "}";
    }

    private static string CreateMetadataManifest(string field, string? value) =>
        CreateMetadataProperties(value is null ? null : $"\"{field}\":{value}");

    private static string CreateMetadataProperties(string? properties) =>
        "{\"name\":\"test.package\",\"version\":\"1.0.0\"," +
        (properties is null ? string.Empty : properties + ",") +
        "\"description\":\"After metadata\",\"dependencies\":{\"dependency.package\":\"2.0.0\"}}";

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
