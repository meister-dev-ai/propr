// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Driver registries for tests about declared configuration: one family that declares fields, and the shipped
///     case where none does.
/// </summary>
/// <remarks>
///     The families compiled into this build declare no configuration fields, because their address, credential
///     and verification state are columns of their own. Exercising the declared path therefore needs a family
///     that declares some, which these build.
/// </remarks>
internal static class DeclaringProviderFamilies
{
    /// <summary>A registry that answers for no family, which a build with no matching driver looks like.</summary>
    public static IAiProviderDriverRegistry None()
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        registry.IsRegistered(Arg.Any<string>()).Returns(false);
        registry.RegisteredKinds.Returns([]);
        return registry;
    }

    /// <summary>
    ///     A registry whose one family declares <paramref name="key" /> and supersedes
    ///     <paramref name="supersededKeys" />, which is how a shipped family that has moved onto a key declares
    ///     itself.
    /// </summary>
    /// <param name="key">The identity key the family declares.</param>
    /// <param name="supersededKeys">The spellings its stored rows may still hold.</param>
    public static IAiProviderDriverRegistry Superseding(string key, params string[] supersededKeys)
    {
        return Declaring(
            key,
            DeclarationWith(key) with
            {
                LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
                    supersededKeys,
                    [
                        ProviderVocabulary.Compose(key, "ApiKey"),
                        ProviderVocabulary.Compose(key, "AzureIdentity"),
                    ],
                    [
                        ProviderDeclaredProtocolModes.Auto,
                        ProviderVocabulary.Compose(key, "Responses"),
                        ProviderVocabulary.Compose(key, "ChatCompletions"),
                        ProviderDeclaredProtocolModes.Embeddings,
                    ]),
            });
    }

    /// <summary>A registry whose one family declares <paramref name="fields" />.</summary>
    /// <param name="providerKind">The identity key the family declares.</param>
    /// <param name="fields">The fields it declares.</param>
    public static IAiProviderDriverRegistry Declaring(string providerKind, params ProviderDeclaredField[] fields)
    {
        return Declaring(providerKind, DeclarationWith(providerKind, fields));
    }

    /// <summary>A registry whose one family carries <paramref name="declaration" />.</summary>
    /// <param name="providerKind">The identity key the family declares.</param>
    /// <param name="declaration">The declaration to answer with.</param>
    public static IAiProviderDriverRegistry Declaring(string providerKind, ProviderDeclaration declaration)
    {
        return Declaring(providerKind, DriverFor(declaration));
    }

    /// <summary>A registry whose one family is served by <paramref name="driver" />.</summary>
    /// <param name="providerKind">The identity key the driver answers for.</param>
    /// <param name="driver">The driver to answer with.</param>
    public static IAiProviderDriverRegistry Declaring(string providerKind, IAiProviderDriver driver)
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        registry.IsRegistered(Arg.Any<string>())
            .Returns(call => ProviderVocabulary.KeysEqual(call.Arg<string>(), providerKind));
        registry.RegisteredKinds.Returns([providerKind]);
        registry.GetRequired(providerKind).Returns(driver);
        return registry;
    }

    /// <summary>A registry serving every declaration given, each under its own key.</summary>
    /// <remarks>
    ///     Needed where a test moves a row from one family to another: with a single family registered, the
    ///     destination resolves as unknown and the read path falls back to the value as stored, so the assertion
    ///     holds without the destination's vocabulary being consulted.
    /// </remarks>
    /// <param name="declarations">The families to serve.</param>
    public static IAiProviderDriverRegistry DeclaringAll(params ProviderDeclaration[] declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var registry = Substitute.For<IAiProviderDriverRegistry>();
        registry.IsRegistered(Arg.Any<string>())
            .Returns(call => declarations.Any(declared => ProviderVocabulary.KeysEqual(call.Arg<string>(), declared.Key)));
        registry.RegisteredKinds.Returns([.. declarations.Select(declared => declared.Key)]);

        foreach (var declaration in declarations)
        {
            // Built before Returns is called. Configuring one substitute inside another's Returns argument
            // leaves NSubstitute with the inner substitute as its last call and it throws instead.
            var driver = DriverFor(declaration);
            registry.GetRequired(declaration.Key).Returns(driver);
        }

        return registry;
    }

    /// <summary>A driver that answers with <paramref name="declaration" /> and nothing else.</summary>
    /// <param name="declaration">What it declares.</param>
    public static IAiProviderDriver DriverFor(ProviderDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(declaration);
        driver.CredentialFields.Returns(declaration.CredentialFields);
        driver.SupportedAuthModes.Returns(declaration.SupportedAuthModes);
        driver.SupportedProtocolModes.Returns(declaration.ProtocolModes.Supported);
        return driver;
    }

    /// <summary>A complete declaration carrying <paramref name="fields" />.</summary>
    /// <param name="providerKind">The identity key the family declares.</param>
    /// <param name="fields">The configuration and action-input fields it declares.</param>
    public static ProviderDeclaration DeclarationWith(string providerKind, params ProviderDeclaredField[] fields)
    {
        return new ProviderDeclaration
        {
            Key = providerKind,
            Label = $"{providerKind} (test)",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            Fields = fields,
            AuthModes =
            [
                new ProviderDeclaredAuthMode(
                    ProviderVocabulary.Compose(providerKind, "ApiKey"),
                    [AiCredentialFieldSupport.ApiKey]),
                new ProviderDeclaredAuthMode(
                    ProviderVocabulary.Compose(providerKind, "AzureIdentity"),
                    AiCredentialFieldSupport.None),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes(
            [
                ProviderDeclaredProtocolModes.Auto,
                ProviderVocabulary.Compose(providerKind, "Responses"),
                ProviderVocabulary.Compose(providerKind, "ChatCompletions"),
                ProviderDeclaredProtocolModes.Embeddings,
            ]),
            ConformanceInputs = new ProviderConformanceInputs(ProviderVocabulary.Compose(providerKind, "ApiKey")),
        };
    }
}
