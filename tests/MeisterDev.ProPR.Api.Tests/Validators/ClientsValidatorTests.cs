// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Tests.Validators;

/// <summary>Unit tests for ClientsController request validators.</summary>
public sealed class ClientsValidatorTests
{
    private static readonly CreateClientRequestValidator CreateClientValidator = new();
    private static readonly PatchClientRequestValidator PatchClientValidator = new();

    /// <summary>The posture of an installation whose operator opted in to private egress.</summary>
    private static readonly EgressUrlPolicy PrivateEgressPermitted = new(AllowPrivateEgress: true, AllowInsecureScheme: false);

    private static readonly CreateClientProviderConnectionRequestValidator CreateProviderConnectionValidator =
        new(PrivateEgressPermitted);

    private static readonly PatchClientProviderConnectionRequestValidator PatchProviderConnectionValidator =
        new(PrivateEgressPermitted);

    private static readonly CreateClientProviderConnectionRequestValidator StrictCreateProviderConnectionValidator =
        new(EgressUrlPolicy.Locked);

    private static readonly PatchClientProviderConnectionRequestValidator StrictPatchProviderConnectionValidator =
        new(EgressUrlPolicy.Locked);

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

    [Theory]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://172.16.4.2")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://[fd00::1]")]
    [InlineData("https://[fe80::1]")]
    public void CreateProviderConnection_PrivateHostWithoutTheOptIn_FailsAndNamesTheOptIn(string hostBaseUrl)
    {
        var result = StrictCreateProviderConnectionValidator.Validate(ForgejoRequest(hostBaseUrl));

        Assert.False(result.IsValid);
        var error = Assert.Single(
            result.Errors,
            candidate => candidate.PropertyName == nameof(CreateClientProviderConnectionRequest.HostBaseUrl));
        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, error.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://10.0.0.5")]
    [InlineData("http://10.0.0.5")]
    [InlineData("https://169.254.169.254")]
    [InlineData("http://100.64.0.1")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://localhost")]
    [InlineData("http://[fd00::1]")]
    public void CreateProviderConnection_PrivateHostWithTheOptIn_Passes(string hostBaseUrl)
    {
        var result = CreateProviderConnectionValidator.Validate(ForgejoRequest(hostBaseUrl));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateProviderConnection_PublicHostOverHttp_FailsUnderBothPostures()
    {
        var request = ForgejoRequest("http://forgejo.example.com");

        Assert.False(CreateProviderConnectionValidator.Validate(request).IsValid);
        Assert.False(StrictCreateProviderConnectionValidator.Validate(request).IsValid);
    }

    [Fact]
    public void CreateProviderConnection_PublicHostOverHttps_PassesUnderBothPostures()
    {
        var request = ForgejoRequest("https://forgejo.example.com");

        Assert.True(CreateProviderConnectionValidator.Validate(request).IsValid);
        Assert.True(StrictCreateProviderConnectionValidator.Validate(request).IsValid);
    }

    [Fact]
    public void CreateProviderConnection_HostCarryingACredential_Fails()
    {
        var result = CreateProviderConnectionValidator.Validate(ForgejoRequest("https://user:token@forgejo.example.com"));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(CreateClientProviderConnectionRequest.HostBaseUrl));
    }

    [Fact]
    public void PatchProviderConnection_PrivateHostWithoutTheOptIn_FailsAndNamesTheOptIn()
    {
        var result = StrictPatchProviderConnectionValidator.Validate(new PatchClientProviderConnectionRequest("http://127.0.0.1"));

        Assert.False(result.IsValid);
        var error = Assert.Single(
            result.Errors,
            candidate => candidate.PropertyName == nameof(PatchClientProviderConnectionRequest.HostBaseUrl));
        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, error.ErrorMessage, StringComparison.Ordinal);
    }

    // What the opt-in relaxes for a patch, and what it does not: a private host is reached, over https or
    // plain http, and a public host still has to use https.
    [Theory]
    [InlineData("https://192.168.4.10", true)]
    [InlineData("http://192.168.4.10", true)]
    [InlineData("http://forgejo.example.com", false)]
    public void PatchProviderConnection_UnderThePermittedPosture(string hostBaseUrl, bool expected)
    {
        var result = PatchProviderConnectionValidator.Validate(new PatchClientProviderConnectionRequest(hostBaseUrl));

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void PatchProviderConnection_PrivateHostOverHttp_NeedsTheOptIn()
    {
        var result = StrictPatchProviderConnectionValidator.Validate(new PatchClientProviderConnectionRequest("http://192.168.4.10"));

        Assert.False(result.IsValid);
        var error = Assert.Single(
            result.Errors,
            candidate => candidate.PropertyName == nameof(PatchClientProviderConnectionRequest.HostBaseUrl));
        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, error.ErrorMessage, StringComparison.Ordinal);
    }

    // "https:forgejo.example.com" is an absolute URI with an empty host: nothing classifies it, and provider
    // code meets it as a base address it cannot build a request from.
    [Theory]
    [InlineData("https:forgejo.example.com")]
    [InlineData("https:/forgejo.example.com")]
    [InlineData("https://:443")]
    public void ProviderConnection_AnAbsoluteUrlWithNoHost_Fails(string hostBaseUrl)
    {
        var created = CreateProviderConnectionValidator.Validate(ForgejoRequest(hostBaseUrl));
        var patched = PatchProviderConnectionValidator.Validate(new PatchClientProviderConnectionRequest(hostBaseUrl));

        Assert.False(created.IsValid);
        Assert.False(patched.IsValid);

        // Named on both, so it is the address rule that refused the value and not an unrelated check.
        Assert.Contains(
            created.Errors,
            error => error.PropertyName == nameof(CreateClientProviderConnectionRequest.HostBaseUrl));
        Assert.Contains(
            patched.Errors,
            error => error.PropertyName == nameof(PatchClientProviderConnectionRequest.HostBaseUrl));
    }

    private static CreateClientProviderConnectionRequest ForgejoRequest(string hostBaseUrl)
    {
        return new CreateClientProviderConnectionRequest(
            ScmProvider.Forgejo,
            hostBaseUrl,
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            null,
            "Forgejo",
            "forgejo-token-value");
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
    public void PatchClient_EnableEvidenceBackedVerificationBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(EnableEvidenceBackedVerification: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchClient_EnableMultiPassUnionBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(EnableMultiPassUnion: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchClient_ReviewEveryIncrementEnabledBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(ReviewEveryIncrementEnabled: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchClient_WithholdOutOfScopeFindingsBoolean_Passes(bool value)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(WithholdOutOfScopeFindings: value));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(ReviewReasoningEffort.None)]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.Medium)]
    [InlineData(ReviewReasoningEffort.High)]
    public void PatchClient_BaselineReasoningEffortDefinedValue_Passes(ReviewReasoningEffort effort)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(BaselineReasoningEffort: effort));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_BaselineReasoningEffortUndefinedValue_Fails()
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(BaselineReasoningEffort: (ReviewReasoningEffort)99));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void PatchClient_ValidReviewPassList_Passes()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid()),
                    new ReviewPassEntry(1, Guid.NewGuid(), ReasoningEffort: ReviewReasoningEffort.High),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_NameBasedReviewPass_Passes()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.Empty, LogicalModelName: "deep"),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_ReviewPassWithBothModelIdAndLogicalName_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), LogicalModelName: "deep"),
                ]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void PatchClient_ReviewPassWithNeitherModelIdNorLogicalName_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.Empty),
                ]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void PatchClient_ReviewPassListWithUndefinedReasoningEffort_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), ReasoningEffort: (ReviewReasoningEffort)42),
                ]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void PatchClient_EmptyReviewPassList_Passes()
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(ReviewPasses: []));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_ReviewPassListWithTooManyEntries_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid()),
                    new ReviewPassEntry(1, Guid.NewGuid()),
                    new ReviewPassEntry(2, Guid.NewGuid()),
                    new ReviewPassEntry(3, Guid.NewGuid()),
                    new ReviewPassEntry(4, Guid.NewGuid()),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithGappedOrdinals_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid()),
                    new ReviewPassEntry(2, Guid.NewGuid()),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithDuplicateOrdinals_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid()),
                    new ReviewPassEntry(0, Guid.NewGuid()),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithEmptyModelId_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.Empty),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithDuplicateModelIds_Fails()
    {
        var sharedModelId = Guid.NewGuid();
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, sharedModelId),
                    new ReviewPassEntry(1, sharedModelId),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithSecurityLens_Passes()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), "security"),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_ReviewPassListWithProRvLens_Passes()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), "prorv"),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_SameModelUnderDifferentLenses_Passes()
    {
        // The dogfood shape: a plain resample pass plus a security-lens pass on the same model. Distinctness keys on
        // the (model, lens) pair, so this is allowed even though the model id repeats.
        var sharedModelId = Guid.NewGuid();
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, sharedModelId),
                    new ReviewPassEntry(1, sharedModelId, "security"),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_SameModelSameLensTwice_Fails()
    {
        var sharedModelId = Guid.NewGuid();
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, sharedModelId, "security"),
                    new ReviewPassEntry(1, sharedModelId, "security"),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithUnknownLens_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), "not-a-lens"),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_ReviewPassListWithPrWideScopeAndShadow_Passes()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), Scope: "pr_wide", Shadow: true),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_ReviewPassListWithUnknownScope_Fails()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, Guid.NewGuid(), Scope: "not-a-scope"),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Fact]
    public void PatchClient_SameModelDifferentScope_Passes()
    {
        // Distinctness keys on the (model, lens, scope, shadow) tuple, so the same model at a per-file scope plus a
        // pr_wide scope is allowed even though the model id repeats.
        var sharedModelId = Guid.NewGuid();
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, sharedModelId),
                    new ReviewPassEntry(1, sharedModelId, Scope: "pr_wide"),
                ]));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_SameModelLensScopeShadowTwice_Fails()
    {
        var sharedModelId = Guid.NewGuid();
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(
                ReviewPasses:
                [
                    new ReviewPassEntry(0, sharedModelId, Scope: "pr_wide", Shadow: true),
                    new ReviewPassEntry(1, sharedModelId, Scope: "pr_wide", Shadow: true),
                ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.ReviewPasses));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    [InlineData("")]
    public void PatchClient_AcceptedOutputLanguage_Passes(string outputLanguage)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(OutputLanguage: outputLanguage));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("German please")]
    [InlineData("de_DE")]
    [InlineData("x")]
    public void PatchClient_OutputLanguageThatIsNotALanguageTag_FailsOnOutputLanguage(string outputLanguage)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(OutputLanguage: outputLanguage));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(PatchClientRequest.OutputLanguage));
    }

    [Fact]
    public void PatchClient_AdmissionPolicyWithPositiveBounds_Passes()
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(AdmissionPolicy: new ReviewAdmissionPolicyDto(MaxChangedFiles: 150, MaxChangedLines: 20_000)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_AdmissionPolicyWithOnlyClearedBounds_Passes()
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(AdmissionPolicy: new ReviewAdmissionPolicyDto()));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PatchClient_AdmissionBoundAtOrBelowZero_FailsOnAdmissionPolicy(int bound)
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(AdmissionPolicy: new ReviewAdmissionPolicyDto(MaxRepositoryMegabytes: bound)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.AdmissionPolicy));
    }

    [Theory]
    // The ends of the storable range: zero, the largest magnitude the numeric(18,6) column holds, and a value
    // at exactly the scale it keeps.
    [InlineData("0")]
    [InlineData("999999999999.999999")]
    [InlineData("1.234567")]
    public void PatchClient_CapAtTheEdgesOfTheStorableRange_Passes(string cap)
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(BudgetConfig: new BudgetConfigDto(MonthlyHardCapUsd: decimal.Parse(cap, CultureInfo.InvariantCulture))));

        Assert.True(result.IsValid);
    }

    [Theory]
    // Beyond the magnitude the numeric(18,6) cap column holds, and finer than the scale it keeps.
    [InlineData("1000000000000")]
    [InlineData("0.0000001")]
    // Below zero: spend is never negative, so a cap under it would stop every review of the client.
    [InlineData("-0.000001")]
    [InlineData("-999999999999.999999")]
    public void PatchClient_CapOutsideTheStorableRange_FailsOnBudgetConfig(string cap)
    {
        var result = PatchClientValidator.Validate(
            new PatchClientRequest(BudgetConfig: new BudgetConfigDto(MonthlyHardCapUsd: decimal.Parse(cap, CultureInfo.InvariantCulture))));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.BudgetConfig));
    }
}
