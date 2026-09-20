// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     Covers the rules the declaration enforces on what a family may declare.
/// </summary>
/// <remarks>
///     The declaration is read before a driver is reached, so a family that declares one thing and answers with
///     another is a family an operator is offered and cannot configure. What each family this product ships
///     declares is asserted over the composed host, which is where every one of them is loaded from a directory.
/// </remarks>
public sealed class ProviderDeclarationTests
{
    private const string VendorKey = "vendor/family";

    // The credential shape of the family these cases declare, qualified by its key as one persists.
    private const string VendorApiKey = VendorKey + ":ApiKey";

    // A wire shape of the same family.
    private const string VendorChatCompletions = VendorKey + ":ChatCompletions";

    // A declaration is a process-wide instance and a provider add-in shares the process, so a collection behind
    // a read-only interface that a caller can cast back to what is under it is a way to edit what another family
    // declares for the life of the host.
    [Fact]
    public void TheDeclarationPartsFamiliesShareCannotBeEditedThroughACast()
    {
        Assert.IsType<ImmutableArray<string>>(AiAuthModeSupport.ApiKeyOnly(VendorKey));
        Assert.IsType<ImmutableArray<string>>(ProviderDeclaredProtocolModes.Reserved);

        Assert.IsType<ImmutableArray<string>>(ProviderLegacyNames.None.Keys);
        Assert.IsType<ImmutableDictionary<string, string>>(ProviderLegacyNames.None.AuthModeSpellings);
        Assert.IsType<ImmutableDictionary<string, string>>(ProviderLegacyNames.None.ProtocolModeSpellings);
    }

    [Fact]
    public void ADeclarationCopiesTheListsItIsGivenRatherThanHoldingThem()
    {
        var patterns = new[] { "api.example.com" };
        var declaration = Declaration("vendor/family") with { ReachedHostPatterns = patterns };

        patterns[0] = "attacker.example";

        Assert.Equal(["api.example.com"], declaration.ReachedHostPatterns);
    }

    [Theory]
    [InlineData("meisterdev/openAi")]
    [InlineData("a/b")]
    [InlineData("vendor-one/family-two")]
    public void AKeyIsAVendorPrefixASeparatorAndAName(string key)
    {
        Assert.True(ProviderDeclaration.IsValidIdentityKey(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("noseparator")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("two/separators/here")]
    [InlineData("vendor/family name")]
    [InlineData("vendor/family:mode")]
    [InlineData("vendor/familý")]
    public void AKeyThatIsNotOneIsRefused(string? key)
    {
        Assert.False(ProviderDeclaration.IsValidIdentityKey(key));
    }

    // The bound is the width of the narrowest column a key alone is stored in, so a longer one is a row that
    // cannot be written rather than a label that renders oddly.
    [Fact]
    public void AKeyLongerThanTheColumnThatStoresItIsRefused()
    {
        var tooLong = "vendor/" + new string('a', ProviderDeclaration.MaximumKeyLength);

        Assert.False(ProviderDeclaration.IsValidIdentityKey(tooLong));
    }

    // The separator's charset excludes the ':' a qualified vocabulary value is joined with, so the split back
    // apart is unambiguous. Refusing a malformed key where it is set is what keeps that true.
    [Fact]
    public void ADeclarationRefusesAMalformedKeyWhereItIsSet()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Declaration("no-separator"));

        Assert.Contains("no-separator", refusal.Message, StringComparison.Ordinal);
    }

    // The key is what indexes a family and what every connection is stored against. Refusing an absent one where
    // it is set is what lets every reader treat it as present, the registry's dictionary included.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ADeclarationRefusesAnAbsentKeyWhereItIsSet(string? key)
    {
        Assert.Throws<ArgumentException>(() => Declaration(key!));
    }

    // A credential shape names one set of fields. Two entries for one shape leave the host choosing between
    // them, and the read that builds the field map throws where it names neither the family nor the shape.
    [Fact]
    public void ADeclarationRefusesASecondEntryForOneCredentialShape()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ProviderDeclaration
        {
            Key = "vendor/family",
            Label = "A family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes =
            [
                new ProviderDeclaredAuthMode(VendorApiKey, [AiCredentialFieldSupport.ApiKey]),
                new ProviderDeclaredAuthMode(VendorApiKey, []),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs(VendorApiKey),
        });

        Assert.Contains("vendor/family", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(VendorApiKey, refusal.Message, StringComparison.Ordinal);
    }

    // Reading the field map is where a repeated shape used to surface, so it is exercised rather than assumed
    // unreachable.
    [Fact]
    public void ADeclarationBuildsItsFieldMapWithoutThrowing()
    {
        var declaration = Declaration("vendor/family");

        Assert.Equal([VendorApiKey], declaration.CredentialFields.Keys);
        Assert.Equal([VendorApiKey], declaration.SupportedAuthModes);
    }

    // An action's values are collected for one invocation and never stored, so a connection-scoped field among
    // them is a value the host would write into the column a connection is saved into.
    [Fact]
    public void AnActionRefusesAnInputThatIsNotScopedToTheInvocation()
    {
        var connectionField = new ProviderDeclaredField("port", "Port", ProviderFieldKind.Int);

        var refusal = Assert.Throws<ArgumentException>(() => new ProviderDeclaredAction("connect", "Connect", [connectionField]));

        Assert.Contains("port", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Connect")]
    [InlineData("", "Connect")]
    [InlineData("   ", "Connect")]
    [InlineData("connect", null)]
    [InlineData("connect", "")]
    [InlineData("connect", "   ")]
    public void AnActionRefusesAnAbsentIdOrLabel(string? id, string? label)
    {
        Assert.Throws<ArgumentException>(() => new ProviderDeclaredAction(id!, label!, []));
    }

    [Fact]
    public void AnActionRefusesANullInputList()
    {
        Assert.Throws<ArgumentNullException>(() => new ProviderDeclaredAction("connect", "Connect", null!));
    }

    // The host expires an invocation against this window, so a window of nothing expires every invocation of the
    // family before its flow can start.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnInvocationWindowRefusesADurationThatIsNotPositive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderInvocationWindow(TimeSpan.FromSeconds(seconds)));
    }

    // A blank derived-from name is not "unset": the host would look a field up under an empty key and find none.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnInvocationWindowRefusesABlankDerivedFieldName(string fieldName)
    {
        Assert.Throws<ArgumentException>(() => new ProviderInvocationWindow(TimeSpan.FromMinutes(5), fieldName));
    }

    [Fact]
    public void AnInvocationWindowAcceptsNoDerivedFieldName()
    {
        Assert.Null(new ProviderInvocationWindow(TimeSpan.FromMinutes(5)).DerivedFromFieldName);
    }

    // The checks above run where the value is set, which a `with` expression also is.
    [Fact]
    public void TheseRulesHoldForACopyAsWellAsForAConstruction()
    {
        var window = new ProviderInvocationWindow(TimeSpan.FromMinutes(5));
        var action = new ProviderDeclaredAction("connect", "Connect", []);

        Assert.Throws<ArgumentOutOfRangeException>(() => window with { Maximum = TimeSpan.Zero });
        Assert.Throws<ArgumentException>(() => action with { Id = " " });
    }

    [Fact]
    public void AKeyIsStoredAsDeclared()
    {
        Assert.Equal("Vendor/FamilyName", Declaration("Vendor/FamilyName").Key);
    }

    // A declaration is one instance for the process and every add-in in it reaches the same one. The outer lists
    // are copied; so are the collections nested inside them, or a caster writes through one of those instead.
    [Fact]
    public void ACredentialFieldListIsCopiedRatherThanHeld()
    {
        var supplied = new List<ProviderCredentialField> { AiCredentialFieldSupport.ApiKey };
        var mode = new ProviderDeclaredAuthMode(VendorApiKey, supplied);

        supplied.Clear();

        Assert.Single(mode.CredentialFields);
        Assert.IsNotType<List<ProviderCredentialField>>(mode.CredentialFields);
    }

    [Fact]
    public void ACredentialFieldListSetThroughWithIsCopiedToo()
    {
        var supplied = new List<ProviderCredentialField> { AiCredentialFieldSupport.ApiKey };
        var mode = new ProviderDeclaredAuthMode(VendorApiKey, []) with { CredentialFields = supplied };

        supplied.Clear();

        Assert.Single(mode.CredentialFields);
    }

    [Fact]
    public void ADeclaredChoiceListIsCopiedRatherThanHeld()
    {
        var supplied = new List<string> { "apiKey", "subscription" };
        var field = new ProviderDeclaredField("mode", "Account type", ProviderFieldKind.Choice)
        {
            Choices = supplied,
        };

        supplied.Clear();

        Assert.Equal(2, field.Choices.Count);
        Assert.IsNotType<List<string>>(field.Choices);
    }

    // The default is an empty collection expression, which for IReadOnlyList is an array a caller casts back.
    [Fact]
    public void ADeclaredFieldWithNoChoicesHandsOutNothingWritable()
    {
        var field = new ProviderDeclaredField("region", "Region", ProviderFieldKind.String);

        Assert.False(field.Choices is string[]);
        Assert.Empty(field.Choices);
    }

    [Fact]
    public void ADeclaredProtocolListIsCopiedRatherThanHeld()
    {
        var supplied = new List<string> { ProviderDeclaredProtocolModes.Auto, VendorChatCompletions };
        var modes = new ProviderDeclaredProtocolModes(supplied);

        supplied.Clear();

        Assert.Equal(2, modes.Supported.Count);
        Assert.IsNotType<List<string>>(modes.Supported);
    }

    [Fact]
    public void ARequestShapeRefusedParameterMapIsCopiedRatherThanHeld()
    {
        var supplied = new Dictionary<string, ProviderRequestShapeConstraint>(StringComparer.Ordinal)
        {
            ["max_tokens"] = ProviderRequestShapeConstraint.MaxOutputTokens,
        };
        var defaults = new ProviderRequestShapeDefaults(RefusedParameterNames: supplied);

        supplied.Clear();

        Assert.Single(defaults.RefusedParameters);
        Assert.IsNotType<Dictionary<string, ProviderRequestShapeConstraint>>(defaults.RefusedParameters);
    }

    [Fact]
    public void ADeclaredProtocolLabelMapIsCopiedRatherThanHeld()
    {
        var supplied = new Dictionary<string, string> { [VendorChatCompletions] = "Chat" };
        var modes = new ProviderDeclaredProtocolModes([VendorChatCompletions]) { Labels = supplied };

        supplied.Clear();

        Assert.Equal("Chat", modes.Labels[VendorChatCompletions]);
        Assert.IsNotType<Dictionary<string, string>>(modes.Labels);
    }

    // Both are what a family may state and neither is what it has to, so a family built before they existed
    // still declares a complete declaration.
    [Fact]
    public void AFamilyStatesNoModeLabelAndNoConnectionFormUnlessItWantsTo()
    {
        var declaration = Declaration("vendor/family");

        Assert.Empty(declaration.ProtocolModes.Labels);
        Assert.Null(declaration.AuthModes[0].Label);
        Assert.Null(declaration.ConnectionForm);
    }

    private static ProviderDeclaration Declaration(string key)
    {
        return new ProviderDeclaration
        {
            Key = key,
            Label = "A family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes =
            [
                new ProviderDeclaredAuthMode(
                    ProviderVocabulary.Compose(key, "ApiKey"),
                    [AiCredentialFieldSupport.ApiKey]),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs(ProviderVocabulary.Compose(key, "ApiKey")),
        };
    }
}
