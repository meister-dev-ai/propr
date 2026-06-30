// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Controllers;
using MeisterProPR.Api.Features.Clients.Controllers;
using MeisterProPR.Api.Validators;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Tests.Validators;

/// <summary>Unit tests for ClientsController request validators.</summary>
public sealed class ClientsValidatorTests
{
    private static readonly CreateClientRequestValidator CreateClientValidator = new();
    private static readonly PatchClientRequestValidator PatchClientValidator = new();
    private static readonly CreateClientProviderConnectionRequestValidator CreateProviderConnectionValidator = new();
    private static readonly PatchClientProviderConnectionRequestValidator PatchProviderConnectionValidator = new();

    [Fact]
    public void CreateClient_ValidRequest_Passes()
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest("My Client", Guid.NewGuid()));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_EmptyDisplayName_FailsOnDisplayName(string displayName)
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest(displayName, Guid.NewGuid()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateClientRequest.DisplayName));
    }

    [Fact]
    public void CreateClient_EmptyTenantId_FailsOnTenantId()
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest("My Client", Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateClientRequest.TenantId));
    }

    [Theory]
    [InlineData(ReviewStrategy.PrWideAgentic)]
    [InlineData(ReviewStrategy.AgenticFileByFile)]
    public void CreateClient_DisabledDefaultReviewStrategy_Fails(ReviewStrategy strategy)
    {
        var result = CreateClientValidator.Validate(
            new CreateClientRequest("My Client", Guid.NewGuid())
            {
                DefaultReviewStrategy = strategy,
            });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateClientRequest.DefaultReviewStrategy));
    }

    [Fact]
    public void CreateProviderConnection_GitHubPatRequest_Passes()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.GitHub,
                "https://github.com",
                ScmAuthenticationKind.PersonalAccessToken,
                null,
                null,
                null,
                "GitHub Cloud",
                "secret"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateProviderConnection_GitHubAppRequest_Passes()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.GitHub,
                "https://github.com",
                ScmAuthenticationKind.AppInstallation,
                null,
                null,
                null,
                "GitHub App",
                "private-key",
                GitHubAppId: 123456,
                GitHubAppInstallationId: 789012));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, ScmAuthenticationKind.AppInstallation)]
    [InlineData(ScmProvider.GitHub, ScmAuthenticationKind.OAuthClientCredentials)]
    public void CreateProviderConnection_UnsupportedAuthenticationKind_Fails(
        ScmProvider providerFamily,
        ScmAuthenticationKind authenticationKind)
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                providerFamily,
                providerFamily == ScmProvider.AzureDevOps ? "https://dev.azure.com" : "https://github.com",
                authenticationKind,
                null,
                null,
                null,
                "Connection",
                "secret"));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(CreateClientProviderConnectionRequest.AuthenticationKind));
    }

    [Theory]
    [InlineData(
        null,
        "11111111-1111-1111-1111-111111111111",
        nameof(CreateClientProviderConnectionRequest.OAuthTenantId))]
    [InlineData("contoso.onmicrosoft.com", null, nameof(CreateClientProviderConnectionRequest.OAuthClientId))]
    public void CreateProviderConnection_AzureDevOpsOAuthRequest_MissingOAuthField_Fails(
        string? tenantId,
        string? clientId,
        string expectedProperty)
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.AzureDevOps,
                "https://dev.azure.com",
                ScmAuthenticationKind.OAuthClientCredentials,
                null,
                tenantId,
                clientId,
                "Azure DevOps",
                "secret"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == expectedProperty);
    }

    [Fact]
    public void PatchProviderConnection_AzureDevOpsOAuthTenantIdTooLong_Fails()
    {
        var result = PatchProviderConnectionValidator.Validate(new PatchClientProviderConnectionRequest(OAuthTenantId: new string('a', 257)));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(PatchClientProviderConnectionRequest.OAuthTenantId));
    }

    [Fact]
    public void CreateProviderConnection_AzureDevOpsServerPatRequest_Passes()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.AzureDevOps,
                "https://ado-server.example.com/tfs",
                ScmAuthenticationKind.PersonalAccessToken,
                null,
                null,
                null,
                "Azure DevOps Server",
                "server-pat"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateProviderConnection_AzureDevOpsServerWindowsAccountRequest_Passes()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.AzureDevOps,
                "https://ado-server.example.com/tfs",
                ScmAuthenticationKind.WindowsUserAccount,
                @"CONTOSO\\ado-user",
                null,
                null,
                "Azure DevOps Server",
                "server-password"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateProviderConnection_AzureDevOpsServerWindowsAccountWithoutUserName_Fails()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.AzureDevOps,
                "https://ado-server.example.com/tfs",
                ScmAuthenticationKind.WindowsUserAccount,
                null,
                null,
                null,
                "Azure DevOps Server",
                "server-password"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateClientProviderConnectionRequest.UserName));
    }

    [Fact]
    public void CreateProviderConnection_PrivateNetworkHttpHostPatForAzureDevOpsServer_Fails()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.AzureDevOps,
                "http://127.0.0.1",
                ScmAuthenticationKind.PersonalAccessToken,
                null,
                null,
                null,
                "Azure DevOps Server",
                "server-pat"));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.ErrorMessage.Contains(
                "personal access token and Windows user-account authentication require an HTTPS host URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateProviderConnection_AzureDevOpsServerWindowsAccountOnHttpHost_Fails()
    {
        var result = CreateProviderConnectionValidator.Validate(
            new CreateClientProviderConnectionRequest(
                ScmProvider.AzureDevOps,
                "http://127.0.0.1",
                ScmAuthenticationKind.WindowsUserAccount,
                @"CONTOSO\\ado-user",
                null,
                null,
                "Azure DevOps Server",
                "server-password"));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.ErrorMessage.Contains("requires an HTTPS host URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PatchProviderConnection_PrivateNetworkHttpHostOnly_Passes()
    {
        var result = PatchProviderConnectionValidator.Validate(new PatchClientProviderConnectionRequest("http://127.0.0.1"));

        Assert.True(result.IsValid);
    }

    // The provider-specific "credential auth requires an HTTPS host" rule is enforced by the controller
    // (which knows the existing connection's provider), not the request-only patch validator, so a patch
    // with PAT on an http host is provider-agnostic here. Controller coverage lives in the controller tests.

    [Fact]
    public void PatchProviderConnection_WindowsAccountAuthenticationWithoutUserName_Fails()
    {
        var result = PatchProviderConnectionValidator.Validate(
            new PatchClientProviderConnectionRequest(AuthenticationKind: ScmAuthenticationKind.WindowsUserAccount));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("UserName", StringComparison.Ordinal));
    }

    // T035 — PatchClientRequest.CustomSystemMessage validation

    [Fact]
    public void PatchClient_NullCustomSystemMessage_Passes()
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_EmptyCustomSystemMessage_Passes()
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(CustomSystemMessage: ""));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_CustomSystemMessage20000Chars_Passes()
    {
        var result =
            PatchClientValidator.Validate(new PatchClientRequest(CustomSystemMessage: new string('a', 20_000)));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_CustomSystemMessage20001Chars_FailsOnCustomSystemMessage()
    {
        var result =
            PatchClientValidator.Validate(new PatchClientRequest(CustomSystemMessage: new string('a', 20_001)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.CustomSystemMessage));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchClient_ScmCommentPostingEnabledBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(ScmCommentPostingEnabled: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchClient_EnableProRvBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(EnableProRV: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchClient_EnableEvidenceBackedVerificationBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(EnableEvidenceBackedVerification: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(ReviewStrategy.PrWideAgentic)]
    [InlineData(ReviewStrategy.AgenticFileByFile)]
    public void PatchClient_DisabledDefaultReviewStrategy_Fails(ReviewStrategy strategy)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(DefaultReviewStrategy: strategy));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.DefaultReviewStrategy));
    }
}
