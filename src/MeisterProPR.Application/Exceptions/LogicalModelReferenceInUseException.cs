// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when a connection or configured model cannot be deleted/removed because one or more logical models still
///     map to it. The message names the referring logical-model role names so the operator knows what to repoint first.
/// </summary>
public sealed class LogicalModelReferenceInUseException : Exception
{
    /// <inheritdoc />
    public LogicalModelReferenceInUseException(IReadOnlyList<string> referringRoles)
        : base($"Cannot delete: still referenced by logical model(s) {string.Join(", ", referringRoles)}. Repoint or remove them first.")
    {
        this.ReferringRoles = referringRoles;
    }

    /// <summary>The role names of the logical models still referencing the connection/model.</summary>
    public IReadOnlyList<string> ReferringRoles { get; }
}
