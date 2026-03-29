namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when a prompt override with the same client/scope/prompt-key combination already exists.
/// </summary>
public sealed class DuplicatePromptOverrideException : Exception
{
    /// <inheritdoc />
    public DuplicatePromptOverrideException()
        : base("A prompt override with this scope and key already exists.")
    {
    }
}
