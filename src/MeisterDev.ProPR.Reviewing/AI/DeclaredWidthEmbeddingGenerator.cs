// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Refuses an embedding whose width is not the width the connection declares for the model.
/// </summary>
/// <remarks>
///     <para>
///         The declared width is what the memory columns were provisioned for, and it is also what the resolver
///         checks a purpose against. Nothing checks what the provider actually returned: a deployment answers
///         with its own native width whether or not the driver was able to ask for another, so a declaration that
///         does not match the deployment first shows up as a rejected insert naming a column, far from the
///         connection an operator would go and correct.
///     </para>
///     <para>
///         Outside the retry stage, because a width that does not match the declaration is a configuration fault
///         and the next attempt would return the same width. Only the length is read; the vector is passed
///         through untouched.
///     </para>
/// </remarks>
/// <param name="innerGenerator">The generator whose answers are checked.</param>
/// <param name="remoteModelId">The model as the provider knows it, named in the refusal.</param>
/// <param name="declaredDimensions">The width the connection declares for that model.</param>
public sealed class DeclaredWidthEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
    string remoteModelId,
    int declaredDimensions) : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    /// <inheritdoc />
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var generated = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);

        foreach (var embedding in generated)
        {
            if (embedding.Vector.Length != declaredDimensions)
            {
                throw new InvalidOperationException(
                    $"The embedding model '{remoteModelId}' returned {embedding.Vector.Length} dimensions, but the "
                    + $"connection declares {declaredDimensions}. Correct the declared dimensions on the model, or "
                    + "bind the purpose to a deployment that returns the declared width.");
            }
        }

        return generated;
    }
}
