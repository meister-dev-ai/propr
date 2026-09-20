// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using MeisterDev.Ai.Providers.Conformance;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.ExampleAddIn;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     What the kit does with a family that gets one of these properties wrong.
/// </summary>
/// <remarks>
///     Every family this repository can reach passes every check, which on its own is as consistent with checks
///     that measure nothing as with families that are correct. These are the other half: one family per check
///     that breaks exactly what the check states, so a check that stopped measuring shows up here as a case that
///     passes when it should not.
/// </remarks>
public sealed class ConformanceKitTests
{
    /// <summary>A family that is correct in every way, to break one property of at a time.</summary>
    private static IAiProviderDriver Conforming => new ExampleProviderDriver();

    private static DriverConformanceCheck Check(string name)
    {
        return DriverConformance.Checks.Single(check => check.Name == name);
    }

    // The contract every check owes the host: it answers, whatever the family did. A check that let a family's
    // exception out would take a family off the host with nothing saying which check it was, and at startup it
    // would be indistinguishable from the assembly failing to load.
    [Fact]
    public void AFamilyThatThrowsIsReportedAgainstTheCheckItThrewIn()
    {
        var report = DriverConformance.Run(new ThrowingDriver());

        Assert.False(report.Passed);
        var failure = report.Failures[0];
        Assert.Contains("family-identity", failure.Check, StringComparison.Ordinal);
        Assert.Contains(nameof(NotSupportedException), failure.Detail!, StringComparison.Ordinal);
    }

    // The inputs are the one thing the checks cannot derive, so a family that leaves one empty is told which one
    // rather than measured against a blank.
    [Fact]
    public void AFamilyLeavingAConformanceInputEmptyIsToldWhichOne()
    {
        var subject = new ConformanceSubject(Conforming)
        {
            Inputs = new ProviderConformanceInputs(string.Empty),
        };

        var result = Check("conformance-inputs").Run(subject);

        Assert.True(result.IsFailure);
        Assert.Contains(nameof(ProviderConformanceInputs.CredentialAuthMode), result.Detail!, StringComparison.Ordinal);
    }

    // A declaration referring to a field name that resolves to nothing is not reported anywhere at runtime: the
    // host looks the name up exactly and carries on with its own default, so the family reads as not working.
    [Fact]
    public void AFamilyDerivingItsWindowFromAFieldItDoesNotDeclareIsToldSo()
    {
        var result = Check("declared-field-references")
            .Run(new ConformanceSubject(new UnresolvedWindowFieldDriver()));

        Assert.True(result.IsFailure);
        Assert.Contains("notDeclared", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyShowingAFieldOnAConditionNamingNothingIsToldSo()
    {
        var result = Check("declared-field-references")
            .Run(new ConformanceSubject(new UnresolvedVisibilityFieldDriver()));

        Assert.True(result.IsFailure);
        Assert.Contains("absentDecider", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyWhoseUsageMappingReturnsItsVendorsCountsUnchangedFailsTheArithmeticCheck()
    {
        var result = Check("usage-arithmetic").Run(Usage(new MappingDriver(ProviderTokenUsage.FromUsageDetails)));

        Assert.True(result.IsFailure);
        Assert.Equal("usage-arithmetic", result.Check);
        Assert.Contains("cache-read", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyWhoseUsageMappingNormalizesThoseCountsPassesIt()
    {
        var result = Check("usage-arithmetic").Run(Usage(Conforming));

        Assert.False(result.IsFailure, result.Detail);
        Assert.Equal(ConformanceOutcome.Passed, result.Outcome);
    }

    // Reasoning tokens are inside the output total the same way, and a family that reported them beside it would
    // bill the reasoning twice.
    [Fact]
    public void AFamilyWhoseUsageMappingReportsReasoningOutsideTheOutputTotalFailsIt()
    {
        var result = Check("usage-arithmetic").Run(Usage(new MappingDriver(_ => new ProviderTokenUsage(21_200, 300, 20_000, 1_200, ReasoningTokens: 900))));

        Assert.True(result.IsFailure);
        Assert.Contains("reasoning", result.Detail!, StringComparison.Ordinal);
    }

    // A family that records no payload has a mapping nobody measured, and a call of it could be billed at
    // anything. The check fails on the absence of evidence instead of passing on it.
    [Fact]
    public void AFamilyWithNoRecordedPayloadToReplayFailsTheCheck()
    {
        var result = Check("usage-arithmetic").Run(
            new ConformanceSubject(Conforming)
            {
                Inputs = new ProviderConformanceInputs(ExampleProviderDriver.ApiKeyAuth),
            });

        Assert.True(result.IsFailure);
        Assert.Contains("records no usage payload", result.Detail!, StringComparison.Ordinal);
    }

    // And a family that declares different credential fields from the ones its driver answers with offers boxes
    // the driver never reads, which comparing the shapes the fields are keyed by cannot see.
    [Fact]
    public void AFamilyAnsweringWithDifferentCredentialFieldsFromTheOnesItDeclaresFailsTheVocabularyCheck()
    {
        var result = Check("vocabulary-matches-the-declaration")
            .Run(new ConformanceSubject(new RenamedCredentialFieldDriver()));

        Assert.True(result.IsFailure);
        Assert.Contains("credential fields", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyOfferingACredentialShapeItCannotOwnFailsTheVocabularyCheck()
    {
        var result = Check("vocabulary-matches-the-declaration")
            .Run(new ConformanceSubject(new UnclaimableAuthModeDriver()));

        Assert.True(result.IsFailure);
        Assert.Contains("not its own to declare", result.Detail!, StringComparison.Ordinal);
        Assert.Contains(UnclaimableAuthModeDriver.Unqualified, result.Detail!, StringComparison.Ordinal);
    }

    private static ConformanceSubject Usage(IAiProviderDriver driver)
    {
        return new ConformanceSubject(driver)
        {
            // A payload shaped the way a vendor that reports its prompt exclusive of the cache buckets reports
            // one: almost all of the prompt was served from the cache, and the count the vendor calls "input" is
            // the remainder. A family returning those counts unchanged bills the whole prompt at nothing.
            Inputs = new ProviderConformanceInputs(ExampleProviderDriver.ApiKeyAuth, RecordedPayload),
        };
    }

    private const string RecordedPayload =
        """{"inputTokenCount":4,"outputTokenCount":300,"cachedInputTokenCount":20000,"additionalCounts":{"example_cache_creation_tokens":1200}}""";

    /// <summary>A family whose usage mapping is whatever the case under test needs it to be.</summary>
    private sealed class MappingDriver(Func<UsageDetails?, ProviderTokenUsage> mapping) : ForwardingDriver
    {
        public override ProviderTokenUsage ReadUsage(UsageDetails? usage)
        {
            return mapping(usage);
        }
    }

    private static async Task Waits(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellation);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A credential-bearing type that relies on the rendering the compiler generates for a record.</summary>
    private sealed record GeneratedRenderingCredential(string ApiKey, string Endpoint);

    /// <summary>A credential-bearing type whose own rendering prints the credential.</summary>
    private sealed class HandWrittenRenderingCredential(string apiKey)
    {
        public string ApiKey { get; } = apiKey;

        public override string ToString()
        {
            return $"Credential {{ ApiKey = {this.ApiKey} }}";
        }
    }

    /// <summary>A folder that deletes itself, standing in for the folder an add-in is deployed as.</summary>
    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "propr-conformance", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public string Put(string fileName)
        {
            var path = System.IO.Path.Combine(this.Path, fileName);
            File.WriteAllBytes(path, [0x4D, 0x5A]);

            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A family that throws where the first check reads it.</summary>
    private sealed class ThrowingDriver : ForwardingDriver
    {
        public override ProviderDeclaration Declaration =>
            throw new NotSupportedException("this family answers for nothing");
    }

    /// <summary>A family that refuses the first shape it does not declare and builds a client for another.</summary>
    private sealed class PartiallyRefusingProtocolDriver : ForwardingDriver
    {
        public override IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            if (ProviderVocabulary.ValuesEqual(protocolMode, ProviderDeclaredProtocolModes.Embeddings))
            {
                return base.CreateChatClient(endpoint, model, ExampleProviderDriver.ChatCompletionsProtocol);
            }

            throw new InvalidOperationException($"This family does not speak '{protocolMode}'.");
        }
    }

    /// <summary>A family whose invocation window is derived from a field it never declared.</summary>
    private sealed class UnresolvedWindowFieldDriver : ForwardingDriver
    {
        public override ProviderDeclaration Declaration => base.Declaration with
        {
            InvocationWindow = new ProviderInvocationWindow(TimeSpan.FromMinutes(10), "notDeclared"),
        };
    }

    /// <summary>A family showing a field on a condition that names no declared field.</summary>
    private sealed class UnresolvedVisibilityFieldDriver : ForwardingDriver
    {
        public override ProviderDeclaration Declaration
        {
            get
            {
                var declared = base.Declaration;

                return declared with
                {
                    Fields =
                    [
                        .. declared.Fields,
                        new ProviderDeclaredField("conditional", "Conditional", ProviderFieldKind.String)
                        {
                            VisibleWhen = new ProviderFieldVisibility("absentDecider", "yes"),
                        },
                    ],
                };
            }
        }
    }

    /// <summary>A family that builds the client its calls go out on.</summary>
    private sealed class SelfBuiltClientDriver : ForwardingDriver
    {
        public static HttpClient Reach()
        {
            return new HttpClient();
        }
    }

    /// <summary>A family whose chat client opens the socket instead, one hop from the driver.</summary>
    private sealed class SelfBuiltSocketClientDriver : ForwardingDriver
    {
        public override IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            return new SocketOpeningChatClient();
        }
    }

    /// <summary>A family that reaches the network over datagrams instead.</summary>
    private sealed class SelfBuiltDatagramDriver : ForwardingDriver
    {
        public static UdpClient Reach()
        {
            return new UdpClient();
        }
    }

    /// <summary>A family whose driver answers with a credential field its declaration does not name.</summary>
    private sealed class RenamedCredentialFieldDriver : ForwardingDriver
    {
        public override IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
            this.Declaration.CredentialFields.ToDictionary(
                shape => shape.Key,
                shape => (IReadOnlyList<ProviderCredentialField>)
                    [.. shape.Value.Select(declared => declared with { Name = $"{declared.Name}-renamed" })]);
    }

    /// <summary>A family offering a credential shape that is not qualified by its own key.</summary>
    private sealed class UnclaimableAuthModeDriver : ForwardingDriver
    {
        /// <summary>A mode name with no qualifier, which names a shape no family can be said to own.</summary>
        internal const string Unqualified = "ApiKey";

        public override ProviderDeclaration Declaration =>
            base.Declaration with
            {
                AuthModes =
                [
                    new ProviderDeclaredAuthMode(Unqualified, [new ProviderCredentialField("apiKey", "API key")]),
                ],
            };
    }

    /// <summary>A family whose credential comes from the environment, so its shape declares no field.</summary>
    private sealed class AmbientIdentityDriver : ForwardingDriver
    {
        /// <summary>The shape this family declares for a credential that comes from the environment.</summary>
        internal const string AmbientShape = ExampleProviderDriver.FamilyKey + ":AzureIdentity";

        public override ProviderDeclaration Declaration =>
            base.Declaration with
            {
                AuthModes = [new ProviderDeclaredAuthMode(AmbientShape, [])],
            };
    }

    /// <summary>A family that contributes a handler of its own, which the contract takes.</summary>
    private sealed class OuterHandlerDriver : ForwardingDriver
    {
        public static DelegatingHandler Sign()
        {
            return new SigningHandler();
        }
    }

    /// <summary>A family whose declared reach is whatever a test needs it to be.</summary>
    /// <param name="patterns">The hosts it declares.</param>
    private sealed class ReachingDriver(params string[] patterns) : ForwardingDriver
    {
        public override ProviderDeclaration Declaration =>
            base.Declaration with { ReachedHostPatterns = patterns };
    }

    private sealed class SigningHandler : DelegatingHandler;

    /// <summary>The chat client that opens its own socket, reached from the driver that builds it.</summary>
    private sealed class SocketOpeningChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "nothing")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     A family that is correct in every way, so a subclass can be wrong in exactly one.
    /// </summary>
    /// <remarks>
    ///     It forwards to the example add-in rather than reimplementing a family, so what a subclass changes is
    ///     the only difference between it and a family that passes.
    /// </remarks>
    private abstract class ForwardingDriver : IAiProviderDriver
    {
        private readonly ExampleProviderDriver _inner = new();

        public virtual ProviderDeclaration Declaration => this._inner.Declaration;


        public virtual IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
            this._inner.CredentialFields;

        public string? ValidateProbeTarget(AiProbeTarget target)
        {
            return this._inner.ValidateProbeTarget(target);
        }

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
        {
            return this._inner.DiscoverModelsAsync(endpoint, ct);
        }

        public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
        {
            return this._inner.VerifyAsync(endpoint, ct);
        }

        public virtual IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            return this._inner.CreateChatClient(endpoint, model, protocolMode);
        }

        public virtual ProviderTokenUsage ReadUsage(UsageDetails? usage)
        {
            return this._inner.ReadUsage(usage);
        }

        public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            return this._inner.GetChatRuntimeCapabilities(endpoint, model, protocolMode);
        }

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode,
            int dimensions)
        {
            return this._inner.CreateEmbeddingGenerator(endpoint, model, protocolMode, dimensions);
        }
    }
}
