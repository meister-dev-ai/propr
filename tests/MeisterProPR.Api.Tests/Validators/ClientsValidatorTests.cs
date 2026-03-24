using MeisterProPR.Api.Controllers;
using MeisterProPR.Api.Validators;

namespace MeisterProPR.Api.Tests.Validators;

/// <summary>Unit tests for ClientsController request validators.</summary>
public sealed class ClientsValidatorTests
{
    private static readonly CreateClientRequestValidator CreateClientValidator = new();
    private static readonly CreateCrawlConfigRequestValidator CreateCrawlConfigValidator = new();
    private static readonly SetAdoCredentialsRequestValidator SetAdoCredentialsValidator = new();
    private static readonly SetReviewerIdentityRequestValidator SetReviewerIdentityValidator = new();
    private static readonly PatchClientRequestValidator PatchClientValidator = new();

    [Fact]
    public void CreateClient_ValidRequest_Passes()
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest("valid-key-16chars", "My Client"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("123456789012345")]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_ShortOrEmptyKey_FailsOnKey(string key)
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest(key, "Name"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateClientRequest.Key));
    }

    [Fact]
    public void CreateClient_KeyExactlyMinLength_Passes()
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest("exactly-16-chars", "Name"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_EmptyDisplayName_FailsOnDisplayName(string displayName)
    {
        var result = CreateClientValidator.Validate(new CreateClientRequest("valid-key-16chars", displayName));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateClientRequest.DisplayName));
    }

    [Fact]
    public void CreateCrawlConfig_ValidRequest_Passes()
    {
        var result = CreateCrawlConfigValidator.Validate(new CreateCrawlConfigRequest("https://dev.azure.com/org", "proj"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateCrawlConfig_EmptyOrganizationUrl_FailsOnOrganizationUrl(string orgUrl)
    {
        var result = CreateCrawlConfigValidator.Validate(new CreateCrawlConfigRequest(orgUrl, "proj"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateCrawlConfigRequest.OrganizationUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateCrawlConfig_EmptyProjectId_FailsOnProjectId(string projectId)
    {
        var result = CreateCrawlConfigValidator.Validate(new CreateCrawlConfigRequest("https://dev.azure.com/org", projectId));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateCrawlConfigRequest.ProjectId));
    }

    [Fact]
    public void CreateCrawlConfig_IntervalAtMinimum_Passes()
    {
        var result = CreateCrawlConfigValidator.Validate(new CreateCrawlConfigRequest("https://dev.azure.com/org", "proj", 10));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateCrawlConfig_IntervalBelowMinimum_FailsOnCrawlIntervalSeconds()
    {
        var result = CreateCrawlConfigValidator.Validate(new CreateCrawlConfigRequest("https://dev.azure.com/org", "proj", 9));
        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(CreateCrawlConfigRequest.CrawlIntervalSeconds));
    }

    [Fact]
    public void SetAdoCredentials_ValidRequest_Passes()
    {
        var result = SetAdoCredentialsValidator.Validate(new SetAdoCredentialsRequest("tenant", "clientId", "secret"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", "clientId", "secret", nameof(SetAdoCredentialsRequest.TenantId))]
    [InlineData("   ", "clientId", "secret", nameof(SetAdoCredentialsRequest.TenantId))]
    [InlineData("tenant", "", "secret", nameof(SetAdoCredentialsRequest.ClientId))]
    [InlineData("tenant", "   ", "secret", nameof(SetAdoCredentialsRequest.ClientId))]
    [InlineData("tenant", "clientId", "", nameof(SetAdoCredentialsRequest.Secret))]
    [InlineData("tenant", "clientId", "   ", nameof(SetAdoCredentialsRequest.Secret))]
    public void SetAdoCredentials_MissingField_FailsOnThatField(string tenantId, string clientId, string secret, string expectedProperty)
    {
        var result = SetAdoCredentialsValidator.Validate(new SetAdoCredentialsRequest(tenantId, clientId, secret));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == expectedProperty);
    }

    [Fact]
    public void SetReviewerIdentity_ValidGuid_Passes()
    {
        var result = SetReviewerIdentityValidator.Validate(new SetReviewerIdentityRequest(Guid.NewGuid()));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void SetReviewerIdentity_EmptyGuid_FailsOnReviewerId()
    {
        var result = SetReviewerIdentityValidator.Validate(new SetReviewerIdentityRequest(Guid.Empty));
        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(SetReviewerIdentityRequest.ReviewerId));
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
        var result = PatchClientValidator.Validate(new PatchClientRequest(CustomSystemMessage: new string('a', 20_000)));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void PatchClient_CustomSystemMessage20001Chars_FailsOnCustomSystemMessage()
    {
        var result = PatchClientValidator.Validate(new PatchClientRequest(CustomSystemMessage: new string('a', 20_001)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PatchClientRequest.CustomSystemMessage));
    }
}
