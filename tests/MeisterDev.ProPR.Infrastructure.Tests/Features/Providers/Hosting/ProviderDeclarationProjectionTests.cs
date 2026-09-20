// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     How a family's name, the names of the shapes it offers, and what it says about the connection form reach
///     a console.
/// </summary>
/// <remarks>
///     A console that kept its own catalogue of these could only name what it shipped knowing about, so the
///     answer is resolved here. Two sources, in order: what the family states, and the member name read at its
///     word boundaries where it states nothing.
/// </remarks>
public sealed class ProviderDeclarationProjectionTests
{
    private const string GenerateContent = "vendor/family:GoogleGenerateContent";

    private const string SigV4 = "vendor/family:SigV4";

    private const string AzureIdentity = "vendor/family:AzureIdentity";

    [Fact]
    public void AShapeAFamilyDoesNotNameIsReadOffItsMemberName()
    {
        var declaration = Declaration() with
        {
            ProtocolModes = new ProviderDeclaredProtocolModes(
            [
                ProviderDeclaredProtocolModes.Auto,
                "vendor/family:AnthropicMessages",
                GenerateContent,
            ]),
        };

        var shapes = ProviderDeclarationProjection.ProtocolModes(declaration);

        Assert.Equal(
            ["Auto", "Anthropic Messages", "Google Generate Content"],
            shapes.Select(shape => shape.Label));
    }

    [Fact]
    public void AShapeAFamilyNamesIsReportedAsTheFamilyNamedIt()
    {
        var declaration = Declaration() with
        {
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, GenerateContent])
            {
                Labels = new Dictionary<string, string>
                {
                    [GenerateContent] = "Google generateContent",
                },
            },
        };

        var shapes = ProviderDeclarationProjection.ProtocolModes(declaration);

        Assert.Equal("Auto", shapes.Single(shape => shape.Value == ProviderDeclaredProtocolModes.Auto).Label);
        Assert.Equal(
            "Google generateContent",
            shapes.Single(shape => shape.Value == GenerateContent).Label);
    }

    [Fact]
    public void ACredentialShapeIsNamedByTheFamilyOrReadOffItsMemberName()
    {
        var declaration = Declaration() with
        {
            AuthModes =
            [
                new ProviderDeclaredAuthMode(SigV4, []) { Label = "AWS Signature v4" },
                new ProviderDeclaredAuthMode(AzureIdentity, []),
            ],
        };

        var modes = ProviderDeclarationProjection.AuthModes(declaration);

        Assert.Equal("AWS Signature v4", modes.Single(mode => mode.Value == SigV4).Label);
        Assert.Equal("Azure Identity", modes.Single(mode => mode.Value == AzureIdentity).Label);
    }

    // A shape the family still reads but no longer offers is reported like every other one, flagged. A console
    // builds its picker from the shapes that are not flagged and keeps the one the profile it is editing already
    // holds, which it can only do if that shape reaches it named.
    [Fact]
    public void ACredentialShapeAFamilyNoLongerOffersIsReportedFlagged()
    {
        var packed = "vendor/family:ApiKey";
        var declaration = Declaration() with
        {
            AuthModes =
            [
                new ProviderDeclaredAuthMode(packed, [AiCredentialFieldSupport.ApiKey]) { Superseded = true },
                new ProviderDeclaredAuthMode(SigV4, []) { Label = "AWS Signature v4" },
            ],
        };

        var modes = ProviderDeclarationProjection.AuthModes(declaration);

        Assert.True(modes.Single(mode => mode.Value == packed).IsSuperseded);
        Assert.Equal("Api Key", modes.Single(mode => mode.Value == packed).Label);
        Assert.False(modes.Single(mode => mode.Value == SigV4).IsSuperseded);
    }

    // A run of capitals is one word until a capital starts another, so an initialism keeps its shape instead of
    // taking a space after every letter.
    [Theory]
    [InlineData("ApiKey", "Api Key")]
    [InlineData("XApiKey", "X Api Key")]
    [InlineData("SigV4", "Sig V4")]
    [InlineData("BedrockConverse", "Bedrock Converse")]
    [InlineData("Auto", "Auto")]
    [InlineData("", "")]
    public void AMemberNameIsReadAtItsWordBoundaries(string memberName, string expected)
    {
        Assert.Equal(expected, ProviderDeclarationProjection.FromMemberName(memberName));
    }

    [Fact]
    public void AFamilyThatSaysNothingAboutTheConnectionFormReportsNothing()
    {
        Assert.Null(ProviderDeclarationProjection.ConnectionForm(Declaration()));
    }

    // A family states the boxes it has an opinion about. The rest stay unstated, so a console shows its own
    // family-neutral text for them rather than another family's example.
    [Fact]
    public void OnlyTheBoxesAFamilyStatesAreReported()
    {
        var declaration = Declaration() with
        {
            ConnectionForm = new ProviderConnectionForm(
                BaseUrlPlaceholder: "https://api.example.com/v1",
                BaseUrlHint: "The vendor endpoint."),
        };

        var form = ProviderDeclarationProjection.ConnectionForm(declaration);

        Assert.NotNull(form);
        Assert.Equal("https://api.example.com/v1", form.BaseUrlPlaceholder);
        Assert.Equal("The vendor endpoint.", form.BaseUrlHint);
        Assert.Null(form.NamePlaceholder);
        Assert.Null(form.RequiredQueryParam);
        Assert.Null(form.QueryParamPlaceholder);
    }

    // Every one of these strings was written by the add-in, so the host applies the same cap and scrub it
    // applies to a declared field's label. A family is free to name a secret field as the value it echoes.
    [Fact]
    public void AFamilyCannotEchoACredentialThroughAName()
    {
        var declaration = Declaration() with
        {
            Label = "Family holding sk-live-42",
            AuthModes = [new ProviderDeclaredAuthMode("vendor/family:ApiKey", []) { Label = "Key sk-live-42" }],
            ConnectionForm = new ProviderConnectionForm(BaseUrlHint: "Use sk-live-42 here"),
        };

        string[] secrets = ["sk-live-42"];

        Assert.DoesNotContain("sk-live-42", ProviderDeclarationProjection.Label(declaration, secrets));
        Assert.DoesNotContain("sk-live-42", ProviderDeclarationProjection.AuthModes(declaration, secrets)[0].Label);
        Assert.DoesNotContain(
            "sk-live-42",
            ProviderDeclarationProjection.ConnectionForm(declaration, secrets)!.BaseUrlHint!);
    }

    [Fact]
    public void AFamilyThatDeclaresABlankNameIsShownUnderItsKey()
    {
        var declaration = Declaration() with { Label = "   " };

        Assert.Equal("vendor/family", ProviderDeclarationProjection.Label(declaration));
    }

    private static ProviderDeclaration Declaration()
    {
        return new ProviderDeclaration
        {
            Key = "vendor/family",
            Label = "A family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("vendor/family:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs("vendor/family:ApiKey"),
        };
    }
}
