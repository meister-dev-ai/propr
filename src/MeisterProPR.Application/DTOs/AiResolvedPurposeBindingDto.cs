// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Runtime metadata for one resolved AI purpose binding.
/// </summary>
public sealed record AiResolvedPurposeBindingDto(
    AiConnectionDto Connection,
    AiConfiguredModelDto Model,
    AiPurposeBindingDto Binding);
