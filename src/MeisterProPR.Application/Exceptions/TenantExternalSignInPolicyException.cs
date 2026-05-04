// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>Thrown when tenant external sign-in is rejected by tenant policy rather than transport failure.</summary>
public sealed class TenantExternalSignInPolicyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TenantExternalSignInPolicyException" /> class.</summary>
    public TenantExternalSignInPolicyException(string failureCode, string message)
        : base(message)
    {
        this.FailureCode = failureCode;
    }

    /// <summary>Gets the stable code describing the tenant policy rejection.</summary>
    public string FailureCode { get; }
}
