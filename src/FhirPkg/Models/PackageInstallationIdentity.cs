// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace FhirPkg.Models;

/// <summary>
/// Associates an installation reference with the exact package identity
/// represented by its resolved manifest.
/// </summary>
public sealed record PackageInstallationIdentity
{
    /// <summary>
    /// Gets the reference that must be passed to the installer. Mutable aliases
    /// such as <c>current</c> and <c>dev</c> are preserved here.
    /// </summary>
    public required PackageReference InstallationReference { get; init; }

    /// <summary>
    /// Gets the exact name and version reported by the resolved package
    /// manifest.
    /// </summary>
    public required PackageReference ResolvedReference { get; init; }
}
