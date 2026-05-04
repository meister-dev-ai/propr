// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Domain.Enums;

/// <summary>Supported source-control provider families.</summary>
public enum ScmProvider
{
    /// <summary>Azure DevOps provider.</summary>
    [JsonStringEnumMemberName("azureDevOps")]
    AzureDevOps = 0,

    /// <summary>GitHub provider.</summary>
    [JsonStringEnumMemberName("github")] GitHub = 1,

    /// <summary>GitLab provider.</summary>
    [JsonStringEnumMemberName("gitLab")] GitLab = 2,

    /// <summary>Forgejo provider.</summary>
    [JsonStringEnumMemberName("forgejo")] Forgejo = 3,
}
