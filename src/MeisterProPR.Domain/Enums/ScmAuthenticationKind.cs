// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Domain.Enums;

/// <summary>Credential models supported by SCM provider connections.</summary>
public enum ScmAuthenticationKind
{
    /// <summary>OAuth client credentials authentication.</summary>
    [JsonStringEnumMemberName("oauthClientCredentials")]
    OAuthClientCredentials = 0,

    /// <summary>Personal access token authentication.</summary>
    [JsonStringEnumMemberName("personalAccessToken")]
    PersonalAccessToken = 1,

    /// <summary>App installation authentication.</summary>
    [JsonStringEnumMemberName("appInstallation")]
    AppInstallation = 2,
}
