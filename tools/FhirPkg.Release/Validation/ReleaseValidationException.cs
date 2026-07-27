// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Release.Validation;

internal sealed class ReleaseValidationException : Exception
{
    public ReleaseValidationException(string message)
        : base(message)
    {
    }

    public ReleaseValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
