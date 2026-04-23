// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Authentication modes supported by AI providers.
/// </summary>
public enum AiAuthMode
{
    /// <summary>Authenticate with a provider-specific API key.</summary>
    ApiKey = 0,

    /// <summary>Authenticate with Azure identity credentials.</summary>
    AzureIdentity = 1,
}
