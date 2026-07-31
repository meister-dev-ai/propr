// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Taxonomy;

/// <summary>
///     A core finding type as the taxonomy surface returns it: read-only vocabulary, identical for every
///     client of the installation.
/// </summary>
/// <param name="Slug">Stable identifier.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Definition">One-sentence definition, shared with the classifier.</param>
/// <param name="Characteristic">The product-quality characteristic this type contributes to.</param>
/// <param name="BehaviourChanging">Whether the type describes a defect in behaviour rather than in evolvability.</param>
public sealed record CodeInsightCoreTagDto(
    string Slug,
    string DisplayName,
    string Definition,
    CodeInsightQualityCharacteristic Characteristic,
    bool BehaviourChanging);

/// <summary>A client's custom finding type as the taxonomy surface returns it.</summary>
/// <param name="Id">Stable identity; assignments reference this.</param>
/// <param name="Slug">Lower-kebab-case identifier, unique within the client.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Definition">One-sentence definition, shared with the classifier.</param>
/// <param name="RetiredAt">When the tag was retired, or <c>null</c> while it is active.</param>
/// <param name="CreatedAt">When the tag was created.</param>
/// <param name="UpdatedAt">When the tag was last changed.</param>
public sealed record CodeInsightCustomTagDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string Definition,
    DateTimeOffset? RetiredAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The full vocabulary available to one client: the fixed core set plus that client's custom tags.</summary>
/// <param name="Version">Version of the core vocabulary these core tags come from.</param>
/// <param name="CoreTags">The fixed core types, comparable across clients.</param>
/// <param name="CustomTags">The client's own types, including retired ones so history stays readable.</param>
public sealed record CodeInsightTaxonomyDto(
    int Version,
    IReadOnlyList<CodeInsightCoreTagDto> CoreTags,
    IReadOnlyList<CodeInsightCustomTagDto> CustomTags);

/// <summary>A request to create or update a client's custom finding type.</summary>
/// <param name="Slug">Lower-kebab-case identifier; must not collide with a core type or another of the client's tags.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Definition">One-sentence definition the classifier will use as the label description.</param>
public sealed record CodeInsightCustomTagWriteRequest(
    string Slug,
    string DisplayName,
    string Definition);

/// <summary>Why a custom-tag write was rejected. Nothing was persisted for any value other than <see cref="None" />.</summary>
public enum CodeInsightCustomTagWriteError
{
    /// <summary>The write succeeded.</summary>
    None = 0,

    /// <summary>The slug, display name, or definition was missing, too long, or not lower-kebab-case.</summary>
    Invalid = 1,

    /// <summary>The slug names a core type. Shadowing one would make a cross-client roll-up ambiguous.</summary>
    ShadowsCoreTag = 2,

    /// <summary>The client already has a tag with that slug, active or retired.</summary>
    SlugAlreadyUsed = 3,

    /// <summary>The tag does not exist for this client.</summary>
    NotFound = 4,
}

/// <summary>Outcome of a custom-tag write.</summary>
/// <param name="Error">Why it was rejected, or <see cref="CodeInsightCustomTagWriteError.None" /> on success.</param>
/// <param name="Tag">The written tag on success; <c>null</c> otherwise.</param>
/// <param name="Message">Operator-facing explanation when it was rejected.</param>
public sealed record CodeInsightCustomTagWriteResult(
    CodeInsightCustomTagWriteError Error,
    CodeInsightCustomTagDto? Tag,
    string? Message)
{
    /// <summary>Whether the write succeeded.</summary>
    public bool Succeeded => this.Error == CodeInsightCustomTagWriteError.None;

    /// <summary>Returns a successful outcome carrying the written tag.</summary>
    public static CodeInsightCustomTagWriteResult Success(CodeInsightCustomTagDto tag)
    {
        return new CodeInsightCustomTagWriteResult(CodeInsightCustomTagWriteError.None, tag, null);
    }

    /// <summary>Returns a rejected outcome. Nothing was persisted.</summary>
    public static CodeInsightCustomTagWriteResult Rejected(CodeInsightCustomTagWriteError error, string message)
    {
        return new CodeInsightCustomTagWriteResult(error, null, message);
    }
}
