// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Utilities;

public class IniParserTests
{
    private const string PackagesIniContent = """
        [packages]
        hl7.fhir.r4.core#4.0.1 = installed
        hl7.fhir.us.core#6.1.0 = installed

        [metadata]
        version = 1
        created = 2024-01-15
        """;

    [Fact]
    public void Parse_PackagesIni_AllSections()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(PackagesIniContent);

        result.Keys.ShouldContain("packages");
        result.Keys.ShouldContain("metadata");

        result["packages"].Keys.ShouldContain("hl7.fhir.r4.core#4.0.1");
        result["packages"]["hl7.fhir.r4.core#4.0.1"].ShouldBe("installed");

        result["metadata"]["version"].ShouldBe("1");
        result["metadata"]["created"].ShouldBe("2024-01-15");
    }

    [Fact]
    public void Parse_EmptyFile_ReturnsEmptySection()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse("");

        result.ShouldNotBeNull();
        // Should have at least the default empty-string section
        result.Keys.ShouldContain("");
    }

    [Fact]
    public void Parse_CommentsIgnored()
    {
        string content = """
            ; This is a comment
            # This is also a comment
            [section]
            key = value
            """;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(content);

        result["section"].Keys.ShouldContain("key");
        result["section"]["key"].ShouldBe("value");
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> original = IniParser.Parse(PackagesIniContent);
        string serialized = IniParser.Serialize(original);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> reparsed = IniParser.Parse(serialized);

        reparsed["packages"]["hl7.fhir.r4.core#4.0.1"].ShouldBe("installed");
        reparsed["metadata"]["version"].ShouldBe("1");
    }

    [Fact]
    public void Parse_KeyWithoutSection_PlacedInDefaultSection()
    {
        string content = """
            globalkey = globalvalue
            [section]
            key = value
            """;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(content);

        result.Keys.ShouldContain("");
        result[""]["globalkey"].ShouldBe("globalvalue");
    }

    [Fact]
    public void Parse_BareKey_HasEmptyValue()
    {
        string content = """
            [section]
            barekey
            """;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(content);

        result["section"].Keys.ShouldContain("barekey");
        result["section"]["barekey"].ShouldBe(string.Empty);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Func<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> act = () => IniParser.Parse(null!);

        Should.Throw<ArgumentNullException>(() => act());
    }
}
