// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     A client-defined finding type, on top of the fixed core taxonomy. Custom tags are per-client
///     vocabulary: they roll up within their own client and never appear in a cross-client aggregate,
///     because two clients' custom tags with the same name are not the same thing.
/// </summary>
/// <remarks>
///     A tag is retired rather than deleted. Assignments reference <see cref="Id" />, so removing the row
///     would orphan every historical finding that carried it, and renaming <see cref="DisplayName" /> is
///     safe for the same reason: the name is display text, never identity.
/// </remarks>
public sealed class CodeInsightCustomTag
{
    /// <summary>Stable identity. Assignments reference this, never the slug or the display name.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client: scopes the tag and cascades its deletion.</summary>
    public Guid ClientId { get; init; }

    /// <summary>
    ///     Lower-kebab-case identifier, unique within the client and never colliding with a core type's
    ///     slug. It exists so an operator can recognise the tag; identity is still <see cref="Id" />.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human-readable name for the admin surface and the views.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     One-sentence definition. Handed to the classifier as the label description, so what an operator
    ///     writes here is what the model classifies against.
    /// </summary>
    public string Definition { get; set; } = string.Empty;

    /// <summary>
    ///     When the tag was retired, or <see langword="null" /> while it is active. A retired tag is no
    ///     longer assigned to new findings but still resolves for every finding that already carries it.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>UTC timestamp when the tag was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp when the tag was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether the tag may still be assigned to new findings.</summary>
    public bool IsActive => this.RetiredAt is null;
}
