// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown at configuration time when a logical model's mapping is invalid for its declared capability — the
///     referenced connection or configured model is missing, or the model does not support the role's capability (or
///     lacks the metadata that capability needs). The message names the role and the concrete problem so the operator
///     can fix it before the mapping is ever used at review time.
/// </summary>
public sealed class LogicalModelReferenceInvalidException : Exception
{
    /// <inheritdoc />
    public LogicalModelReferenceInvalidException(string roleName, string reason)
        : base($"Logical model '{roleName}' cannot be saved: {reason}")
    {
        this.RoleName = roleName;
    }

    /// <summary>The role name whose mapping is invalid.</summary>
    public string RoleName { get; }
}
