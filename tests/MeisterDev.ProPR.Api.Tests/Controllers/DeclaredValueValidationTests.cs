// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     How the installation's rules and a provider family's own rules combine over a submitted declared value.
/// </summary>
public sealed class DeclaredValueValidationTests
{
    [Fact]
    public void TheInstallationsRefusalStandsWhateverTheFamilyAnswers()
    {
        var driver = DriverThatAccepts();

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "http://169.254.169.254/latest")),
            EgressUrlPolicy.Locked);

        Assert.Equal("endpoint", Assert.Single(refusals).Key);
    }

    // A family knows things the host does not: which hosts are its vendor's, which paths its endpoint serves.
    // Its message reaches the operator against the field it names.
    [Fact]
    public void AFamilyCanRefuseAValueTheInstallationWouldAccept()
    {
        var driver = DriverRefusing("endpoint", "The endpoint must be an example.com host.");

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "https://api.elsewhere.com/v1")),
            EgressUrlPolicy.Locked);

        var refusal = Assert.Single(refusals);
        Assert.Equal("endpoint", refusal.Key);
        Assert.Equal("The endpoint must be an example.com host.", refusal.Value);
    }

    // The installation's check runs first, so the family sees a submission only after the addresses in it have
    // been through the floor, and a family that accepts one cannot put it back.
    [Fact]
    public void TheInstallationsCheckRunsBeforeTheFamilysValidator()
    {
        var seen = new List<IReadOnlyDictionary<string, string>>();
        var driver = DriverThatAccepts();
        driver
            .ValidateDeclaredValues(Arg.Do<IReadOnlyDictionary<string, string>>(values => seen.Add(values)))
            .Returns(new Dictionary<string, string>());

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "http://169.254.169.254/latest")),
            EgressUrlPolicy.Locked);

        Assert.Single(refusals);
        Assert.Single(seen);
    }

    // A family's rule about a credential's shape is its own to state, so the values it is handed include the
    // secret-marked ones the operator just entered.
    [Fact]
    public void TheFamilySeesTheSecretMarkedValuesTheOperatorEntered()
    {
        var driver = DriverRefusing("clientSecret", "The client secret is 32 characters.");

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            new DeclaredValueSubmission(
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["clientSecret"] = "short" },
                []),
            EgressUrlPolicy.Locked);

        Assert.Equal("clientSecret", Assert.Single(refusals).Key);
    }

    // A family handed the operator's credential to validate is one quoting position away from returning it in
    // the message. The message reaches a form, a response body and any log line that renders it, so the host
    // takes the value back out rather than trusting the family not to have put it there.
    [Fact]
    public void AFamilyQuotingTheSubmittedSecretBackDoesNotPutItInTheRefusal()
    {
        var driver = DriverRefusing("clientSecret", "The secret 'sk-live-0123456789' is not a project key.");

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            new DeclaredValueSubmission(
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["clientSecret"] = "sk-live-0123456789" },
                []),
            EgressUrlPolicy.Locked);

        Assert.DoesNotContain("sk-live-0123456789", Assert.Single(refusals).Value, StringComparison.Ordinal);
    }

    // A value the host does not hold in this submission cannot be searched for, and a non-secret value is
    // usually what the message is about, so neither is altered.
    [Fact]
    public void ARefusalAboutANonSecretValueIsLeftAsTheFamilyWroteIt()
    {
        var driver = DriverRefusing("endpoint", "The host 'api.elsewhere.com' is not one this family serves.");

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            new DeclaredValueSubmission(
                new Dictionary<string, string> { ["endpoint"] = "https://api.elsewhere.com/v1" },
                new Dictionary<string, string> { ["clientSecret"] = "sk-live-0123456789" },
                []),
            EgressUrlPolicy.Locked);

        Assert.Equal(
            "The host 'api.elsewhere.com' is not one this family serves.",
            Assert.Single(refusals).Value);
    }

    [Fact]
    public void ARefusalLongerThanTheHostKeepsIsCutShortAndSaysSo()
    {
        var driver = DriverRefusing("endpoint", new string('x', ProviderHostLimits.MaximumMessageLength * 2));

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "https://api.example.com/v1")),
            EgressUrlPolicy.Locked);

        var refusal = Assert.Single(refusals);
        Assert.Equal(ProviderHostLimits.MaximumMessageLength, refusal.Value.Length);
        Assert.EndsWith("…", refusal.Value, StringComparison.Ordinal);
    }

    // The field name is the key the form binds by, so it is passed through whatever its length. Cutting it
    // produced a key no input carries, and a refusal under one attaches to nothing and is never shown. A name
    // this long is a declaration problem and belongs to declaration validation, not to a refusal that has
    // already been produced for it.
    [Fact]
    public void AFieldNameIsPassedThroughSoTheRefusalAttachesToItsInput()
    {
        var longName = new string('f', ProviderHostLimits.MaximumFieldTextLength * 2);
        var driver = DriverRefusing(
            longName,
            "Refused.",
            new ProviderDeclaredField(longName, "A very long name", ProviderFieldKind.String));

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "https://api.example.com/v1")),
            EgressUrlPolicy.Locked);

        Assert.Equal(longName, Assert.Single(refusals).Key);
    }

    // A refusal reaches the form as a model-state key, and the form can only place one against a field it
    // renders. One naming something the family never declared has nowhere to go: the operator is told a value is
    // wrong with no input beside the message saying which.
    [Fact]
    public void ARefusalNamingAFieldTheFamilyDidNotDeclareIsNotReported()
    {
        var driver = DriverRefusing("neverDeclared", "Refused.");

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "https://api.example.com/v1")),
            EgressUrlPolicy.Locked);

        Assert.Empty(refusals);
    }

    // What comes back is a family's own output, so the count is bounded like the strings are: a family answering
    // with one refusal per row of a provider response would otherwise fill the response with them.
    [Fact]
    public void AFamilyAnsweringWithUnboundedRefusalsIsCutOff()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        var fields = Enumerable.Range(0, 500)
            .Select(index => new ProviderDeclaredField($"field{index}", $"Field {index}", ProviderFieldKind.String))
            .ToList();
        driver.Declaration.Returns(Declaration([.. fields]));
        driver.ValidateDeclaredValues(Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(fields.ToDictionary(field => field.Name, _ => "Refused.", StringComparer.Ordinal));

        var refusals = DeclaredValueValidation.Refusals(
            driver,
            Submitted(("endpoint", "https://api.example.com/v1")),
            EgressUrlPolicy.Locked);

        Assert.InRange(refusals.Count, 1, 100);
    }

    private static DeclaredValueSubmission Submitted(params (string Name, string Value)[] values)
    {
        return new DeclaredValueSubmission(
            values.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal),
            new Dictionary<string, string>(),
            []);
    }

    private static IAiProviderDriver DriverThatAccepts()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(Declaration());
        driver.ValidateDeclaredValues(Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(new Dictionary<string, string>());
        return driver;
    }

    private static IAiProviderDriver DriverRefusing(
        string fieldName,
        string message,
        params ProviderDeclaredField[] alsoDeclared)
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(Declaration(alsoDeclared));
        driver.ValidateDeclaredValues(Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(new Dictionary<string, string> { [fieldName] = message });
        return driver;
    }

    private static ProviderDeclaration Declaration(params ProviderDeclaredField[] alsoDeclared)
    {
        return new ProviderDeclaration
        {
            Key = "tests/validation",
            Label = "Validation test family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            Fields =
            [
                new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.Url),
                new ProviderDeclaredField("clientSecret", "Client secret", ProviderFieldKind.Secret),
                .. alsoDeclared,
            ],
            AuthModes = [new ProviderDeclaredAuthMode("tests/validation:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, "tests/validation:ChatCompletions"]),
            ConformanceInputs = new ProviderConformanceInputs("tests/validation:ApiKey"),
        };
    }
}
