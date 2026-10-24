// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.CodeInsights.Taxonomy;

/// <summary>
///     One finding type in the fixed core taxonomy.
/// </summary>
/// <param name="Slug">
///     Stable identifier, lower-kebab-case. This is what assignments store and what cross-client
///     comparison joins on, so it must never change: renaming a slug silently re-partitions history.
/// </param>
/// <param name="DisplayName">Human-readable name for the admin surface and the views.</param>
/// <param name="Definition">
///     One-sentence definition. This doubles as the label description handed to the classifier, so the
///     definition a human reads and the definition the model classifies against cannot drift apart.
/// </param>
/// <param name="Characteristic">The product-quality characteristic this type contributes to.</param>
/// <param name="BehaviourChanging">
///     Whether a finding of this type describes a defect in what the code does, as opposed to how well it
///     can be understood and changed. Roughly three quarters of real review findings are the latter, so the
///     split is worth reporting rather than inferring.
/// </param>
public sealed record CodeInsightCoreTagDefinition(
    string Slug,
    string DisplayName,
    string Definition,
    CodeInsightQualityCharacteristic Characteristic,
    bool BehaviourChanging);

/// <summary>Exposes the shared canonical finding vocabulary through the existing analytics taxonomy API.</summary>
public static class CodeInsightCoreTaxonomy
{
    /// <summary>The canonical LogicError type slug.</summary>
    public const string LogicError = FindingTypeTaxonomy.LogicError;

    /// <summary>The canonical DataValidation type slug.</summary>
    public const string DataValidation = FindingTypeTaxonomy.DataValidation;

    /// <summary>The canonical ResourceHandling type slug.</summary>
    public const string ResourceHandling = FindingTypeTaxonomy.ResourceHandling;

    /// <summary>The canonical Concurrency type slug.</summary>
    public const string Concurrency = FindingTypeTaxonomy.Concurrency;

    /// <summary>The canonical ApiContract type slug.</summary>
    public const string ApiContract = FindingTypeTaxonomy.ApiContract;

    /// <summary>The canonical Security type slug.</summary>
    public const string Security = FindingTypeTaxonomy.Security;

    /// <summary>The canonical Performance type slug.</summary>
    public const string Performance = FindingTypeTaxonomy.Performance;

    /// <summary>The canonical ErrorHandlingObservability type slug.</summary>
    public const string ErrorHandlingObservability = FindingTypeTaxonomy.ErrorHandlingObservability;

    /// <summary>The canonical DesignStructure type slug.</summary>
    public const string DesignStructure = FindingTypeTaxonomy.DesignStructure;

    /// <summary>The canonical NamingClarity type slug.</summary>
    public const string NamingClarity = FindingTypeTaxonomy.NamingClarity;

    /// <summary>The canonical DocumentationTests type slug.</summary>
    public const string DocumentationTests = FindingTypeTaxonomy.DocumentationTests;

    /// <summary>Version of the canonical vocabulary.</summary>
    public const int Version = FindingTypeTaxonomy.Version;

    /// <summary>Every core type in canonical order.</summary>
    public static IReadOnlyList<CodeInsightCoreTagDefinition> All { get; } = FindingTypeTaxonomy.All
        .Select(type => new CodeInsightCoreTagDefinition(type.Slug, type.DisplayName, type.Definition, type.Characteristic, type.BehaviourChanging))
        .ToList().AsReadOnly();

    /// <summary>Every core slug in canonical order.</summary>
    public static IReadOnlyList<string> AllSlugs => FindingTypeTaxonomy.AllSlugs;

    /// <summary>Returns a canonical definition, or null for an unknown slug.</summary>
    public static CodeInsightCoreTagDefinition? Find(string? slug) =>
        All.FirstOrDefault(type => string.Equals(type.Slug, slug?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the slug is a canonical core type.</summary>
    public static bool IsCoreSlug(string? slug) => FindingTypeTaxonomy.IsCoreSlug(slug);

    /// <summary>Returns the concern class for one quality characteristic.</summary>
    public static CodeInsightConcernClass ConcernClassOf(CodeInsightQualityCharacteristic characteristic) => FindingTypeTaxonomy.ConcernClassOf(characteristic);

    /// <summary>Returns the concern class for the supported core types.</summary>
    public static CodeInsightConcernClass? ConcernClassOf(IEnumerable<string?> coreSlugs) => FindingTypeTaxonomy.ConcernClassOf(coreSlugs);
}
