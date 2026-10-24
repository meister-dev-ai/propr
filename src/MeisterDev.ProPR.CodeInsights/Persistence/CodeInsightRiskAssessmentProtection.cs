// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

internal static class CodeInsightRiskAssessmentProtection
{
    private const string Purpose = "code-insight-finding-risk-assessment";

    internal static string? Protect(FindingRiskAssessment? assessment, ISecretProtectionCodec codec) =>
        assessment is null ? null : codec.Protect(JsonSerializer.Serialize(assessment), Purpose);

    internal static FindingRiskAssessment? Read(string? encrypted, ISecretProtectionCodec codec) =>
        encrypted is null ? null : JsonSerializer.Deserialize<FindingRiskAssessment>(codec.Unprotect(encrypted, Purpose));
}
