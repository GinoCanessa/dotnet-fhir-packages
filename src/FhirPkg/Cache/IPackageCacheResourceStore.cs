// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Cache;

internal interface IPackageCacheResourceStore
{
    Task<TResult?> ReadFileAsync<TResult>(
        PackageReference reference,
        string relativePath,
        Func<string, TResult?> tryGetCached,
        Func<string, string, TResult?> materialize,
        CancellationToken cancellationToken = default)
        where TResult : class;
}
