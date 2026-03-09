// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace FhirPkg.Models;

/// <summary>
/// A lightweight, immutable reference to a FHIR package consisting of a name and optional version.
/// Supports both FHIR directive syntax (<c>name#version</c>) and NPM directive syntax (<c>name@version</c>).
/// </summary>
/// <param name="Name">The package identifier (e.g. "hl7.fhir.r4.core").</param>
/// <param name="Version">The package version, or <c>null</c> for unversioned references.</param>
/// <param name="Scope">An optional NPM scope (e.g. "@scope").</param>
public readonly record struct PackageReference(string Name, string? Version = null, string? Scope = null)
{
    /// <summary>
    /// Returns the FHIR-style directive string: <c>name#version</c>.
    /// If no version is specified, returns just the name.
    /// </summary>
    public string FhirDirective => Version is not null ? $"{Name}#{Version}" : Name;

    /// <summary>
    /// Returns the NPM-style directive string: <c>name@version</c>.
    /// If no version is specified, returns just the name.
    /// </summary>
    public string NpmDirective => Version is not null ? $"{Name}@{Version}" : Name;

    /// <summary>
    /// Returns the conventional cache directory name, which follows the FHIR directive format.
    /// </summary>
    public string CacheDirectoryName => FhirDirective;

    /// <summary>
    /// Indicates whether this reference includes a version.
    /// </summary>
    public bool HasVersion => Version is not null;

    /// <summary>
    /// Parses a package directive string into a <see cref="PackageReference"/>.
    /// Accepts both FHIR (<c>#</c>) and NPM (<c>@</c>) separators and handles NPM scopes.
    /// </summary>
    /// <param name="directive">
    /// A directive string such as "hl7.fhir.r4.core#4.0.1", "hl7.fhir.r4.core@4.0.1",
    /// or "@scope/package@1.0.0".
    /// </param>
    /// <returns>A parsed <see cref="PackageReference"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="directive"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directive"/> is empty or whitespace.</exception>
    public static PackageReference Parse(string directive)
    {
        ArgumentNullException.ThrowIfNull(directive);

        var trimmed = directive.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Package directive must not be empty.", nameof(directive));

        string? scope = null;
        var input = trimmed;

        // Handle NPM scope: @scope/name@version or @scope/name#version
        if (input.StartsWith('@'))
        {
            var slashIndex = input.IndexOf('/');
            if (slashIndex > 0)
            {
                scope = input[..slashIndex]; // includes the '@'
                input = input[(slashIndex + 1)..];
            }
        }

        // Try FHIR separator first
        var hashIndex = input.IndexOf('#');
        if (hashIndex >= 0)
        {
            var name = input[..hashIndex];
            var version = input[(hashIndex + 1)..];
            var fullName = scope is not null ? $"{scope}/{name}" : name;
            return new PackageReference(fullName, string.IsNullOrEmpty(version) ? null : version, scope);
        }

        // Try NPM separator — find the last '@' that is not the scope prefix
        var atIndex = input.LastIndexOf('@');
        if (atIndex > 0)
        {
            var name = input[..atIndex];
            var version = input[(atIndex + 1)..];
            var fullName = scope is not null ? $"{scope}/{name}" : name;
            return new PackageReference(fullName, string.IsNullOrEmpty(version) ? null : version, scope);
        }

        // No separator — just a package name
        var justName = scope is not null ? $"{scope}/{input}" : input;
        return new PackageReference(justName, null, scope);
    }

    /// <summary>
    /// Implicitly converts a directive string to a <see cref="PackageReference"/> by parsing it.
    /// </summary>
    /// <param name="directive">A package directive string.</param>
    public static implicit operator PackageReference(string directive) => Parse(directive);

    /// <summary>
    /// Implicitly converts a key-value pair (as found in a dependencies map) to a <see cref="PackageReference"/>.
    /// </summary>
    /// <param name="dependency">A key-value pair where the key is the package name and the value is the version.</param>
    public static implicit operator PackageReference(KeyValuePair<string, string> dependency) =>
        new(dependency.Key, dependency.Value);

    /// <summary>
    /// Returns the FHIR directive representation of this reference.
    /// </summary>
    public override string ToString() => FhirDirective;
}
