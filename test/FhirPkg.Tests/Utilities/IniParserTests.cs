// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Utilities;
using FluentAssertions;
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

        result.Should().ContainKey("packages");
        result.Should().ContainKey("metadata");

        result["packages"].Should().ContainKey("hl7.fhir.r4.core#4.0.1");
        result["packages"]["hl7.fhir.r4.core#4.0.1"].Should().Be("installed");

        result["metadata"]["version"].Should().Be("1");
        result["metadata"]["created"].Should().Be("2024-01-15");
    }

    [Fact]
    public void Parse_EmptyFile_ReturnsEmptySection()
    {
        var result = IniParser.Parse("");

        result.Should().NotBeNull();
        // Should have at least the default empty-string section
        result.Should().ContainKey("");
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

        result["section"].Should().ContainKey("key");
        result["section"]["key"].Should().Be("value");
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var original = IniParser.Parse(PackagesIniContent);
        var serialized = IniParser.Serialize(original);
        var reparsed = IniParser.Parse(serialized);

        reparsed["packages"]["hl7.fhir.r4.core#4.0.1"].Should().Be("installed");
        reparsed["metadata"]["version"].Should().Be("1");
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

        result.Should().ContainKey("");
        result[""]["globalkey"].Should().Be("globalvalue");
    }

    [Fact]
    public void Parse_BareKey_HasEmptyValue()
    {
        var content = """
            [section]
            barekey
            """;

        var result = IniParser.Parse(content);

        result["section"].Should().ContainKey("barekey");
        result["section"]["barekey"].Should().Be(string.Empty);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        var act = () => IniParser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
