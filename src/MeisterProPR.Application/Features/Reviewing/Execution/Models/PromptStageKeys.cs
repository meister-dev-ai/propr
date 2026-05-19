// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable keys for prompt-driven workflow stages supported by the offline prompt experiment harness.
/// </summary>
public static class PromptStageKeys
{
    /// <summary>Global system prompt stage key.</summary>
    public const string GlobalSystem = "global_system";

    /// <summary>Per-file context system prompt stage key.</summary>
    public const string PerFileContextSystem = "per_file_context_system";

    /// <summary>Per-file user prompt stage key.</summary>
    public const string PerFileUser = "per_file_user";

    /// <summary>Agentic file planning system prompt stage key.</summary>
    public const string AgenticFilePlanningSystem = "agentic_file_planning_system";

    /// <summary>Agentic file planning user prompt stage key.</summary>
    public const string AgenticFilePlanningUser = "agentic_file_planning_user";

    /// <summary>Agentic file investigation system prompt stage key.</summary>
    public const string AgenticFileInvestigationSystem = "agentic_file_investigation_system";

    /// <summary>Agentic file investigation user prompt stage key.</summary>
    public const string AgenticFileInvestigationUser = "agentic_file_investigation_user";

    /// <summary>Synthesis system prompt stage key.</summary>
    public const string SynthesisSystem = "synthesis_system";

    /// <summary>Synthesis user prompt stage key.</summary>
    public const string SynthesisUser = "synthesis_user";

    /// <summary>PR verification system prompt stage key.</summary>
    public const string PrVerificationSystem = "pr_verification_system";

    /// <summary>PR verification user prompt stage key.</summary>
    public const string PrVerificationUser = "pr_verification_user";
}
