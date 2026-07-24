// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when a logical model with the same name already exists within its scope (a tenant catalog, or one
///     client's overrides).
/// </summary>
public sealed class DuplicateLogicalModelException : Exception
{
    /// <inheritdoc />
    public DuplicateLogicalModelException()
        : base("A logical model with this name already exists in this scope.")
    {
    }

    /// <inheritdoc />
    public DuplicateLogicalModelException(string name)
        : base($"A logical model named '{name}' already exists in this scope.")
    {
    }
}
