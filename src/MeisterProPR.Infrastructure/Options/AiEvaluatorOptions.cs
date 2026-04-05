// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Infrastructure.Options;

/// <summary>
///     Configuration options for the instruction relevance evaluator AI model.
///     Bound from environment variables; validated on application startup.
/// </summary>
public sealed class AiEvaluatorOptions
{
    /// <summary>Azure OpenAI endpoint URL for the evaluator model. Bound to <c>AI_EVALUATOR_ENDPOINT</c>.</summary>
    [Required(ErrorMessage = "AI_EVALUATOR_ENDPOINT is required.")]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Model deployment name for the evaluator (e.g. <c>gpt-4o-mini</c>). Bound to <c>AI_EVALUATOR_DEPLOYMENT</c>.</summary>
    [Required(ErrorMessage = "AI_EVALUATOR_DEPLOYMENT is required.")]
    public string Deployment { get; set; } = string.Empty;
}
