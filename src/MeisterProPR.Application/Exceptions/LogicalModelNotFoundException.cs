// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when a named logical model cannot be resolved for a client — neither a client override nor a tenant
///     catalog entry with that name exists.
/// </summary>
public sealed class LogicalModelNotFoundException : Exception
{
    /// <inheritdoc />
    public LogicalModelNotFoundException(string roleName)
        : base($"No logical model named '{roleName}' is configured for this client.")
    {
        this.RoleName = roleName;
    }

    /// <summary>The role name that could not be resolved.</summary>
    public string RoleName { get; }
}
