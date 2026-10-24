// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>One canonical finding type shared by review generation and analytics.</summary>
/// <param name="Slug">Stable persisted identifier, unchanged across clients and review history.</param>
/// <param name="DisplayName">Readable label used by publication and administration.</param>
/// <param name="Definition">Definition used by both review generation and fallback classification.</param>
/// <param name="Characteristic">Product-quality characteristic used for rollups.</param>
/// <param name="BehaviourChanging">Whether the type describes a defect in behavior.</param>
public sealed record FindingCoreTypeDefinition(
    string Slug,
    string DisplayName,
    string Definition,
    CodeInsightQualityCharacteristic Characteristic,
    bool BehaviourChanging);

/// <summary>
///     Canonical core vocabulary for producing findings and comparing them across clients and time.
///     Custom client vocabulary is captured separately and does not change the core set.
/// </summary>
/// <remarks>Changing a persisted slug requires a migration decision and vocabulary version change.</remarks>
public static class FindingTypeTaxonomy
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

    private static readonly IReadOnlyList<FindingCoreTypeDefinition> Definitions =
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

    private static readonly IReadOnlyDictionary<string, FindingCoreTypeDefinition> DefinitionsBySlug =
        Definitions.ToDictionary(definition => definition.Slug, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every core type, in canonical order.</summary>
    public static IReadOnlyList<FindingCoreTypeDefinition> All => Definitions;

    /// <summary>Every core slug, in canonical order.</summary>
    public static IReadOnlyList<string> AllSlugs { get; } = Definitions.Select(definition => definition.Slug).ToList();

    /// <summary>Returns the definition for <paramref name="slug" />, or <see langword="null" /> when it is not a core type.</summary>
    public static FindingCoreTypeDefinition? Find(string? slug)
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

    /// <summary>Returns the concern class for a product-quality characteristic.</summary>
    public static CodeInsightConcernClass ConcernClassOf(CodeInsightQualityCharacteristic characteristic)
    {
        return characteristic switch
        {
            CodeInsightQualityCharacteristic.Maintainability => CodeInsightConcernClass.Evolvability,
            _ => CodeInsightConcernClass.Functional,
        };
    }

    /// <summary>Returns the class of supported core types; functional concerns take precedence when classes differ.</summary>
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
