// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs.AzureDevOps;

/// <summary>
///     Transport shape for a provider-aware canonical source reference.
/// </summary>
public sealed record CanonicalSourceReferenceDto(string Provider, string Value);
