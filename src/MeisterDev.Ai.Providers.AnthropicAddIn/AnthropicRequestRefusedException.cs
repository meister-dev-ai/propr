// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;

namespace MeisterDev.Ai.Providers.AnthropicAddIn;

/// <summary>A call that Anthropic answered with a failure status, with what Anthropic wrote about the refusal.</summary>
/// <remarks>
///     The status is attached as on every other <see cref="HttpRequestException" />, so the shared classifier
///     decides on a retry the same way. What Anthropic wrote is kept apart from the exception message: the message
///     holds the whole body when the body is not JSON, and <see cref="AnthropicProviderDriver" /> puts the excerpt
///     on the verdict without the rest of the body.
/// </remarks>
/// <param name="message">The exception message.</param>
/// <param name="status">The status Anthropic answered with.</param>
/// <param name="providerMessage">
///     The excerpt of the message field of the response body, or <see langword="null" /> when the body had none.
/// </param>
internal sealed class AnthropicRequestRefusedException(string message, HttpStatusCode status, string? providerMessage)
    : HttpRequestException(message, null, status)
{
    /// <summary>What Anthropic wrote about the refusal, or <see langword="null" /> when the body held no message.</summary>
    internal string? ProviderMessage { get; } = providerMessage;
}
