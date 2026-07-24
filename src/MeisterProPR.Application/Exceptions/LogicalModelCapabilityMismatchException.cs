// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when a logical model is resolved for the wrong capability — e.g. a chat consumer names an embedding-typed
///     role, or vice-versa.
/// </summary>
public sealed class LogicalModelCapabilityMismatchException : Exception
{
    /// <inheritdoc />
    public LogicalModelCapabilityMismatchException(string roleName, AiOperationKind expected, AiOperationKind actual)
        : base($"Logical model '{roleName}' is a {actual} model but was resolved as {expected}.")
    {
        this.RoleName = roleName;
        this.Expected = expected;
        this.Actual = actual;
    }

    /// <summary>The role name that was resolved.</summary>
    public string RoleName { get; }

    /// <summary>The capability the caller required.</summary>
    public AiOperationKind Expected { get; }

    /// <summary>The capability the logical model actually declares.</summary>
    public AiOperationKind Actual { get; }
}
