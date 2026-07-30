// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;

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

/// <summary>
///     The installation's fixed core finding-type taxonomy: the source of truth for the vocabulary the
///     classifier assigns from and the only vocabulary that is comparable across clients and over time.
/// </summary>
/// <remarks>
///     <para>
///         This lives in code rather than in a table on purpose. It is the cross-client comparison axis, and
///         a per-installation editable table would drift until no two installations' numbers meant the same
///         thing. Per-client vocabulary exists too, but as separately modelled custom tags that never roll up
///         across clients.
///     </para>
///     <para>
///         The set is derived from the established code-review and defect taxonomies rather than invented:
///         each type is defensible from at least two independent published schemes, and the behaviour-changing
///         split mirrors the evolvability-versus-functional division that code-review research found accounts
///         for most of what reviewers actually raise. There is deliberately no "other" or "miscellaneous"
///         type: it would become the bucket everything uncertain lands in and would destroy the comparison
///         axis it is meant to serve.
///     </para>
///     <para>
///         Changing this set is a deliberate act. Adding a type means bumping <see cref="Version" /> so
///         assignments record which vocabulary judged them; removing or renaming one is a breaking change to
///         every trend already computed and needs a migration decision, not an edit.
///     </para>
/// </remarks>
public static class CodeInsightCoreTaxonomy
{
    /// <summary>Wrong control flow, comparison, computation, or algorithm.</summary>
    public const string LogicError = "logic-error";

    /// <summary>Missing or wrong validation, null, bounds, or type checking; an unhandled input or error case.</summary>
    public const string DataValidation = "data-validation";

    /// <summary>Leaks, lifetime, disposal, and initialisation or assignment mistakes.</summary>
    public const string ResourceHandling = "resource-handling";

    /// <summary>Races, deadlock, missing or wrong synchronisation, ordering and timing.</summary>
    public const string Concurrency = "concurrency";

    /// <summary>Misuse of an interface or library; wrong parameters or call semantics; a broken contract.</summary>
    public const string ApiContract = "api-contract";

    /// <summary>An exploitable weakness.</summary>
    public const string Security = "security";

    /// <summary>Avoidable cost in time, memory, allocations, I/O, or query volume.</summary>
    public const string Performance = "performance";

    /// <summary>Swallowed or over-broad exceptions; missing or misleading diagnostics; missing audit trail.</summary>
    public const string ErrorHandlingObservability = "error-handling-observability";

    /// <summary>A structural or design problem: responsibility placement, coupling, duplication, complexity.</summary>
    public const string DesignStructure = "design-structure";

    /// <summary>Identifier naming, conventions, expressiveness, formatting, and layout.</summary>
    public const string NamingClarity = "naming-clarity";

    /// <summary>Missing or incorrect comments and documentation; missing or inadequate tests.</summary>
    public const string DocumentationTests = "documentation-tests";

    /// <summary>
    ///     Version of the core vocabulary. Stamped onto every assignment so a later change to the set stays
    ///     auditable and old assignments remain interpretable against the vocabulary that produced them.
    /// </summary>
    public const int Version = 1;

    private static readonly IReadOnlyList<CodeInsightCoreTagDefinition> Definitions =
    [
        new(
            LogicError,
            "Logic error",
            "Wrong control flow, comparison, computation, or algorithm: the code does something other than what was intended.",
            CodeInsightQualityCharacteristic.Reliability,
            true),
        new(
            DataValidation,
            "Data validation",
            "Missing or wrong validation of data or values before use, including null, bounds, and type checking, and unhandled input or error cases.",
            CodeInsightQualityCharacteristic.Reliability,
            true),
        new(
            ResourceHandling,
            "Resource handling",
            "Mistakes with data or resource lifetime: leaks, missing disposal, and wrong initialisation or assignment.",
            CodeInsightQualityCharacteristic.Reliability,
            true),
        new(
            Concurrency,
            "Concurrency",
            "Race conditions, deadlock, missing or incorrect synchronisation, and ordering or timing assumptions between concurrent paths.",
            CodeInsightQualityCharacteristic.Reliability,
            true),
        new(
            ApiContract,
            "API contract",
            "Misuse of an interface, library, or service: wrong parameters or call semantics, or a broken contract between two parts of the system.",
            CodeInsightQualityCharacteristic.Maintainability,
            true),
        new(
            Security,
            "Security",
            "An exploitable weakness: authentication or authorisation gaps, injection, secret exposure, unsafe deserialisation, or misused cryptography.",
            CodeInsightQualityCharacteristic.Security,
            true),
        new(
            Performance,
            "Performance",
            "Avoidable cost in time, memory, allocations, I/O, or query volume, including an inefficient algorithm or data-structure choice.",
            CodeInsightQualityCharacteristic.PerformanceEfficiency,
            true),
        new(
            ErrorHandlingObservability,
            "Error handling and observability",
            "Swallowed or over-broad exception handling, missing or misleading diagnostics, and missing logging or audit trail.",
            CodeInsightQualityCharacteristic.Reliability,
            true),
        new(
            DesignStructure,
            "Design and structure",
            "A structural problem rather than a defect: responsibility placement, coupling, duplication, excess complexity, weak abstraction, or a misapplied pattern.",
            CodeInsightQualityCharacteristic.Maintainability,
            false),
        new(
            NamingClarity,
            "Naming and clarity",
            "Identifier naming, convention adherence, expressiveness, formatting, and layout: readability without any change in behaviour.",
            CodeInsightQualityCharacteristic.Maintainability,
            false),
        new(
            DocumentationTests,
            "Documentation and tests",
            "Missing or incorrect comments and documentation, and missing or inadequate test coverage for the change.",
            CodeInsightQualityCharacteristic.Maintainability,
            false),
    ];

    private static readonly IReadOnlyDictionary<string, CodeInsightCoreTagDefinition> DefinitionsBySlug =
        Definitions.ToDictionary(definition => definition.Slug, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every core type, in canonical order.</summary>
    public static IReadOnlyList<CodeInsightCoreTagDefinition> All => Definitions;

    /// <summary>Every core slug, in canonical order.</summary>
    public static IReadOnlyList<string> AllSlugs { get; } = Definitions.Select(definition => definition.Slug).ToList();

    /// <summary>Returns the definition for <paramref name="slug" />, or <see langword="null" /> when it is not a core type.</summary>
    public static CodeInsightCoreTagDefinition? Find(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        return DefinitionsBySlug.TryGetValue(slug.Trim(), out var definition) ? definition : null;
    }

    /// <summary>Returns whether <paramref name="slug" /> names a core type, ignoring case and surrounding whitespace.</summary>
    public static bool IsCoreSlug(string? slug)
    {
        return Find(slug) is not null;
    }

    /// <summary>
    ///     The concern class a quality characteristic belongs to: whether the code does the right thing, or
    ///     whether it can be lived with.
    /// </summary>
    /// <remarks>
    ///     Stated once, here, so every read groups the same way. Reliability, security and performance are things
    ///     a user or an attacker could meet; maintainability is what the next person to change the code meets.
    /// </remarks>
    public static CodeInsightConcernClass ConcernClassOf(CodeInsightQualityCharacteristic characteristic)
    {
        return characteristic switch
        {
            CodeInsightQualityCharacteristic.Maintainability => CodeInsightConcernClass.Evolvability,
            _ => CodeInsightConcernClass.Functional,
        };
    }

    /// <summary>
    ///     The concern class a finding's core types put it in, or <see langword="null" /> when it carries none and
    ///     so belongs in no class.
    /// </summary>
    /// <remarks>
    ///     A finding can carry core types from both classes, so the rule is stated rather than left to whichever
    ///     tag is read first: functional wins. A finding that is both a logic error and hard to read is a logic
    ///     error, and grouping it under evolvability would hide the more consequential claim.
    /// </remarks>
    public static CodeInsightConcernClass? ConcernClassOf(IEnumerable<string?> coreSlugs)
    {
        ArgumentNullException.ThrowIfNull(coreSlugs);

        CodeInsightConcernClass? resolved = null;
        foreach (var slug in coreSlugs)
        {
            if (Find(slug) is not { } definition)
            {
                continue;
            }

            var candidate = ConcernClassOf(definition.Characteristic);
            if (candidate == CodeInsightConcernClass.Functional)
            {
                return CodeInsightConcernClass.Functional;
            }

            resolved ??= candidate;
        }

        return resolved;
    }
}
