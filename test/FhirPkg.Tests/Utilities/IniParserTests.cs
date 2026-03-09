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
        var result = IniParser.Parse(PackagesIniContent);

        result.ShouldContainKey("packages");
        result.ShouldContainKey("metadata");

        result["packages"].ShouldContainKey("hl7.fhir.r4.core#4.0.1");
        result["packages"]["hl7.fhir.r4.core#4.0.1"].ShouldBe("installed");

        result["metadata"]["version"].ShouldBe("1");
        result["metadata"]["created"].ShouldBe("2024-01-15");
    }

    [Fact]
    public void Parse_EmptyFile_ReturnsEmptySection()
    {
        var result = IniParser.Parse("");

        result.ShouldNotBeNull();
        // Should have at least the default empty-string section
        result.ShouldContainKey("");
    }

    [Fact]
    public void Parse_CommentsIgnored()
    {
        var content = """
            ; This is a comment
            # This is also a comment
            [section]
            key = value
            """;

        var result = IniParser.Parse(content);

        result["section"].ShouldContainKey("key");
        result["section"]["key"].ShouldBe("value");
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var original = IniParser.Parse(PackagesIniContent);
        var serialized = IniParser.Serialize(original);
        var reparsed = IniParser.Parse(serialized);

        reparsed["packages"]["hl7.fhir.r4.core#4.0.1"].ShouldBe("installed");
        reparsed["metadata"]["version"].ShouldBe("1");
    }

    [Fact]
    public void Parse_KeyWithoutSection_PlacedInDefaultSection()
    {
        var content = """
            globalkey = globalvalue
            [section]
            key = value
            """;

        var result = IniParser.Parse(content);

        result.ShouldContainKey("");
        result[""]["globalkey"].ShouldBe("globalvalue");
    }

    [Fact]
    public void Parse_BareKey_HasEmptyValue()
    {
        var content = """
            [section]
            barekey
            """;

        var result = IniParser.Parse(content);

        result["section"].ShouldContainKey("barekey");
        result["section"]["barekey"].ShouldBe(string.Empty);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        var act = () => IniParser.Parse(null!);

        Should.Throw<ArgumentNullException>(() => act());
    }
}
