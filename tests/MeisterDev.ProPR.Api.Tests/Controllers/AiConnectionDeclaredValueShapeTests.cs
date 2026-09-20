// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Controllers;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     What the host collects for the fields a provider family declared, and what it refuses.
/// </summary>
/// <remarks>
///     The declared shape is what the host promises a family: a family reading a whole-number field parses it
///     without checking, and one switching on a choice field has a case per option it declared. Stored unchecked,
///     a value of the wrong shape is refused by the family at the first call of a review instead of on the form
///     the operator was filling in.
/// </remarks>
public sealed class AiConnectionDeclaredValueShapeTests
{
    [Fact]
    public void AWholeNumberFieldRefusesAValueThatIsNotOne()
    {
        var refusal = Assert.Single(Collect(("timeoutSeconds", "soon")).Refusals);

        Assert.Equal("timeoutSeconds", refusal.Key);
        Assert.Contains("whole number", refusal.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeNumberFieldAcceptsOne()
    {
        var submission = Collect(("timeoutSeconds", "120"));

        Assert.Empty(submission.Refusals);
        Assert.Equal("120", submission.Settings["timeoutSeconds"]);
    }

    [Fact]
    public void AYesOrNoFieldRefusesAValueThatIsNeither()
    {
        var refusal = Assert.Single(Collect(("usePkce", "maybe")).Refusals);

        Assert.Equal("usePkce", refusal.Key);
    }

    // A choice field is a closed set the family declared, and a value outside it is one the family has no case
    // for. Offered by the form as a list, it can only arrive from a caller that composed the request itself.
    [Fact]
    public void AChoiceFieldRefusesAnOptionItDoesNotOffer()
    {
        var refusal = Assert.Single(Collect(("region", "antarctica")).Refusals);

        Assert.Equal("region", refusal.Key);
        Assert.Contains("'eu'", refusal.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AChoiceFieldAcceptsAnOptionItOffers()
    {
        var submission = Collect(("region", "eu"));

        Assert.Empty(submission.Refusals);
        Assert.Equal("eu", submission.Settings["region"]);
    }

    // Free text is free text. A family that declared a field as text and then treats it as something narrower
    // has stated nothing the host can check.
    [Fact]
    public void AFreeTextFieldTakesWhateverWasEntered()
    {
        var submission = Collect(("label", "anything at all"));

        Assert.Empty(submission.Refusals);
        Assert.Equal("anything at all", submission.Settings["label"]);
    }

    // A required field the operator left alone is satisfied by the family's default, because the default is what
    // the connection will hold.
    [Fact]
    public void ARequiredFieldWithADefaultIsNotRefusedWhenNothingWasEntered()
    {
        Assert.Empty(Collect().Refusals);
    }

    // A form submits every input it renders, so a field shown empty arrives as a blank rather than as absent.
    // Refused on that, a connection whose family stated a working value could not be saved.
    [Fact]
    public void ARequiredFieldWithADefaultIsNotRefusedWhenItArrivesBlank()
    {
        Assert.Empty(Collect(("port", "  ")).Refusals);
    }

    [Fact]
    public void ARequiredFieldWithNoDefaultIsStillRefusedWhenItArrivesBlank()
    {
        var refusal = Assert.Single(Collect(("mandatory", string.Empty)).Refusals);

        Assert.Equal("mandatory", refusal.Key);
        Assert.Contains("required", refusal.Value, StringComparison.Ordinal);
    }

    // The console renders one input per declared field, so these cases are what reaches the endpoint when the
    // inputs are edited in the browser: a request can carry any key at all, and the declaration is the only
    // thing that decides which keys mean anything.
    [Fact]
    public void AFieldTheFamilyNeverDeclaredIsRefusedAndNotStored()
    {
        var submission = Collect(("injectedByTheBrowser", "anything"));

        var refusal = Assert.Single(submission.Refusals);
        Assert.Equal("injectedByTheBrowser", refusal.Key);
        Assert.Contains("declares no configuration field", refusal.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("injectedByTheBrowser", submission.Settings.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("injectedByTheBrowser", submission.Secrets.Keys, StringComparer.Ordinal);
    }

    // A computed field is derived by the host wherever it is shown. A value posted for one is what an operator
    // would supply after making the input writable, and it has to reach neither the settings nor the envelope.
    [Fact]
    public void AValuePostedForAComputedFieldIsDropped()
    {
        var submission = Collect(("redirectUri", "https://attacker.example.com/callback"));

        Assert.DoesNotContain("redirectUri", submission.Settings.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("redirectUri", submission.Secrets.Keys, StringComparer.Ordinal);
    }

    // A field whose visibility condition is not met is one the form did not ask about, so a value posted for it
    // came from somewhere other than the form.
    [Fact]
    public void AValuePostedForAHiddenFieldIsDropped()
    {
        var submission = Collect(("usePkce", "false"), ("codeChallenge", "supplied-by-hand"));

        Assert.DoesNotContain("codeChallenge", submission.Settings.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("codeChallenge", submission.Secrets.Keys, StringComparer.Ordinal);
    }

    // The same field once its condition is met, so the case above is about the condition and not about the field
    // being unreachable.
    [Fact]
    public void AValuePostedForAFieldWhoseConditionIsMetIsKept()
    {
        var submission = Collect(("usePkce", "true"), ("codeChallenge", "entered-on-the-form"));

        Assert.Equal("entered-on-the-form", Assert.Contains("codeChallenge", submission.Settings));
    }

    /// <summary>
    ///     Collects a submission that satisfies the family's one field with no default, so what each case asserts
    ///     is about the field it names and not about the rest of the form.
    /// </summary>
    /// <param name="values">The values this case submits, written over the satisfying baseline.</param>
    private static DeclaredValueSubmission Collect(params (string Name, string Value)[] values)
    {
        var submitted = new Dictionary<string, string>(StringComparer.Ordinal) { ["mandatory"] = "an account" };
        foreach (var (name, value) in values)
        {
            submitted[name] = value;
        }

        return AiConnectionDeclaredValues.Collect(Declaration(), submitted);
    }

    private static ProviderDeclaration Declaration()
    {
        return new ProviderDeclaration
        {
            Key = "tests/shapes",
            Label = "Shape test family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            Fields =
            [
                new ProviderDeclaredField("timeoutSeconds", "Timeout", ProviderFieldKind.Int),
                new ProviderDeclaredField("usePkce", "Use PKCE", ProviderFieldKind.Bool),
                new ProviderDeclaredField("region", "Region", ProviderFieldKind.Choice)
                {
                    Choices = ["eu", "us"],
                },
                new ProviderDeclaredField("label", "Label", ProviderFieldKind.String),
                new ProviderDeclaredField("port", "Callback port", ProviderFieldKind.Int)
                {
                    IsRequired = true,
                    DefaultValue = "8976",
                },
                new ProviderDeclaredField("mandatory", "Account name", ProviderFieldKind.String)
                {
                    IsRequired = true,
                },
                new ProviderDeclaredField("redirectUri", "Redirect address", ProviderFieldKind.String)
                {
                    IsComputed = true,
                },
                new ProviderDeclaredField("codeChallenge", "Code challenge", ProviderFieldKind.String)
                {
                    VisibleWhen = new ProviderFieldVisibility("usePkce", "true"),
                },
            ],
            AuthModes = [new ProviderDeclaredAuthMode("tests/shapes:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, "tests/shapes:ChatCompletions"]),
            ConformanceInputs = new ProviderConformanceInputs("tests/shapes:ApiKey"),
        };
    }
}
