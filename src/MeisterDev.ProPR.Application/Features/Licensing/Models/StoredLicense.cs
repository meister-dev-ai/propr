// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Diagnostics.CodeAnalysis;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What the installation has on file: the stored license document and when it was activated.
///     <para>
///         The document is returned as it was stored, without being verified. Whether it comes from a signer
///         this build accepts is decided by the code that reads it, so a row that has stopped verifying is
///         still returned rather than reported as absent.
///     </para>
/// </summary>
public sealed record StoredLicense
{
    /// <summary>
    ///     The stored license document, or <see langword="null" /> when the stored value could not be read
    ///     back out of its protected form. That is a separate outcome from a document that fails to verify:
    ///     the value stored at rest no longer matches what the installation's data-protection keys can open,
    ///     which points at the key material or the row rather than at the license.
    /// </summary>
    public string? CompactLicense { get; init; }

    /// <summary>When the document was stored.</summary>
    public required DateTimeOffset ActivatedAt { get; init; }

    /// <summary>Who stored it, when the activation was made by a signed-in user.</summary>
    public Guid? ActivatedByUserId { get; init; }

    /// <summary>Whether the stored value could be read back.</summary>
    [MemberNotNullWhen(true, nameof(CompactLicense))]
    public bool IsReadable => this.CompactLicense is not null;
}
