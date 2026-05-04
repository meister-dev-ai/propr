// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Describes the state of one readiness criterion used to evaluate a provider connection.</summary>
public sealed record ProviderReadinessCriterionResult
{
    /// <summary>Initializes a new instance of the <see cref="ProviderReadinessCriterionResult" /> class.</summary>
    /// <param name="criterionKey">The key of the readiness criterion.</param>
    /// <param name="scope">The scope of the criterion.</param>
    /// <param name="status">The status of the criterion.</param>
    /// <param name="summary">A summary description of the criterion result.</param>
    public ProviderReadinessCriterionResult(string criterionKey, string scope, string status, string summary)
    {
        if (string.IsNullOrWhiteSpace(criterionKey))
        {
            throw new ArgumentException("Criterion key is required.", nameof(criterionKey));
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Criterion scope is required.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Criterion status is required.", nameof(status));
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Criterion summary is required.", nameof(summary));
        }

        this.CriterionKey = criterionKey;
        this.Scope = scope;
        this.Status = status;
        this.Summary = summary;
    }

    /// <summary>Gets the key of the readiness criterion.</summary>
    public string CriterionKey { get; }

    /// <summary>Gets the scope of the criterion.</summary>
    public string Scope { get; }

    /// <summary>Gets the status of the criterion.</summary>
    public string Status { get; }

    /// <summary>Gets a summary description of the criterion result.</summary>
    public string Summary { get; }
}
