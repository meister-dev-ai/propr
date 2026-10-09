// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;


namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Provides predicates for normalized thread resolution intent.</summary>
public static class ThreadResolutionStatusInterpreter
{
    /// <summary>True when the intent represents any resolved state (accepted or claimed-fixed).</summary>
    public static bool IsResolved(ThreadResolutionIntent intent) => intent != ThreadResolutionIntent.Active;
}
