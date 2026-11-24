// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Occurrence likelihood of a finding's scenario, independent of correctness confidence.</summary>
/// <remarks>
///     The value descriptions follow the rubric finding-risk-v2, which rates likelihood by the trigger the code shows. The
///     rubric finding-risk-v1 rated it by how routine the conditions of the scenario were.
/// </remarks>
public enum FindingLikelihood
{
    /// <summary>No occurrence assessment is available.</summary>
    Unknown = 0,

    /// <summary>
    ///     The scenario needs a fault of another system, a malformed or hand-built request that the clients of the reviewed
    ///     product do not send, or a timing coincidence between concurrent operations.
    /// </summary>
    Unlikely = 1,

    /// <summary>A supported input, configuration, or sequence of steps that is not the default reaches the scenario.</summary>
    Possible = 2,

    /// <summary>Ordinary use of the changed feature with its default configuration reaches the scenario.</summary>
    Likely = 3,

    /// <summary>No meaningful occurrence scenario applies.</summary>
    NotApplicable = 4,
}
