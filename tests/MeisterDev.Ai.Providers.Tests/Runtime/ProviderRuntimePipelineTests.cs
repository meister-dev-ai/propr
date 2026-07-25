// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Runtime;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.Ai.Providers.Tests.Runtime;

public sealed class ProviderRuntimePipelineTests
{
    private static readonly ProviderEndpoint Endpoint =
        new(AiProviderKind.OpenAi, "https://api.openai.com/v1", AiAuthMode.ApiKey, "secret");

    private static readonly ProviderModelDescriptor Model =
        new(Guid.NewGuid(), "gpt-x", [AiProtocolMode.Auto]);

    // The whole point of the stage enum is that registration order must not decide call order. Registering
    // deliberately backwards has to yield the same chain as registering in order.
    [Fact]
    public void RegistrationOrder_DoesNotAffectCallOrder()
    {
        var calls = new List<ProviderRuntimeStage>();
        var pipeline = new ProviderRuntimePipeline(
        [
            Recording(ProviderRuntimeStage.Normalization, calls),
            Recording(ProviderRuntimeStage.Retry, calls),
            Recording(ProviderRuntimeStage.Budget, calls),
            Recording(ProviderRuntimeStage.Observability, calls),
        ]);

        var composed = pipeline.Compose(Substitute.For<IChatClient>(), Endpoint, Model);
        composed.GetService(typeof(string));

        Assert.Equal(
            [
                ProviderRuntimeStage.Retry,
                ProviderRuntimeStage.Observability,
                ProviderRuntimeStage.Budget,
                ProviderRuntimeStage.Normalization,
            ],
            calls);
    }

    [Fact]
    public void NoDecorators_ReturnsTheDriverClientUnchanged()
    {
        var client = Substitute.For<IChatClient>();

        Assert.Same(client, new ProviderRuntimePipeline([]).Compose(client, Endpoint, Model));
    }

    [Fact]
    public void ADecoratorThatDoesNotApply_LeavesTheChainUntouched()
    {
        var client = Substitute.For<IChatClient>();
        var passive = Substitute.For<IProviderChatClientDecorator>();
        passive.Stage.Returns(ProviderRuntimeStage.Normalization);
        passive.Decorate(Arg.Any<IChatClient>(), Arg.Any<ProviderEndpoint>(), Arg.Any<ProviderModelDescriptor>())
            .Returns(callInfo => callInfo.Arg<IChatClient>());

        Assert.Same(client, new ProviderRuntimePipeline([passive]).Compose(client, Endpoint, Model));
    }

    // A decorator that records when it is entered, by delegating GetService down the chain.
    private static IProviderChatClientDecorator Recording(ProviderRuntimeStage stage, List<ProviderRuntimeStage> calls)
    {
        var decorator = Substitute.For<IProviderChatClientDecorator>();
        decorator.Stage.Returns(stage);
        decorator.Decorate(Arg.Any<IChatClient>(), Arg.Any<ProviderEndpoint>(), Arg.Any<ProviderModelDescriptor>())
            .Returns(callInfo =>
            {
                var inner = callInfo.Arg<IChatClient>();
                var wrapper = Substitute.For<IChatClient>();
                wrapper.When(client => client.GetService(Arg.Any<Type>(), Arg.Any<object?>()))
                    .Do(_ =>
                    {
                        calls.Add(stage);
                        inner.GetService(typeof(string));
                    });
                return wrapper;
            });
        return decorator;
    }
}
