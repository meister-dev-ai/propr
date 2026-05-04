// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Indirect reference from portable configuration to a protected runtime value.
/// </summary>
public sealed record ProtectedValueReference(
    string ReferenceName,
    string ConfigurationKey,
    string? Purpose = null);
