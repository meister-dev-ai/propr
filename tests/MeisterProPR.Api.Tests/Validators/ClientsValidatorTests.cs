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
        var result = CreateClientValidator.Validate(new CreateClientRequest("My Client"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_EmptyDisplayName_FailsOnDisplayName(string displayName)
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest(displayName));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateClientRequest.DisplayName));
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
                "GitHub Cloud",
                "secret"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, ScmAuthenticationKind.AppInstallation)]
    [InlineData(ScmProvider.GitHub, ScmAuthenticationKind.AppInstallation)]
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
}
