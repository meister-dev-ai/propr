namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Result of an AI evaluation of a single pull-request comment thread.
/// </summary>
/// <param name="IsResolved">
///     <c>true</c> when the AI concludes the original issue has been addressed;
///     <c>false</c> when the thread should remain open.
/// </param>
/// <param name="ReplyText">
///     Optional reply text for the AI to post in the thread.
///     Used when <see cref="IsResolved" /> is <c>true</c> and the client's
///     <c>CommentResolutionBehavior</c> is <c>WithReply</c>, or for conversational responses.
///     <c>null</c> when no reply is needed.
/// </param>
/// <param name="InputTokens">
///     Input token count reported by the AI provider for this evaluation, or <see langword="null" />
///     when usage data is unavailable. Used for protocol recording and cost attribution.
/// </param>
/// <param name="OutputTokens">
///     Output token count reported by the AI provider for this evaluation, or <see langword="null" />.
/// </param>
public sealed record ThreadResolutionResult(
    bool IsResolved,
    string? ReplyText,
    long? InputTokens = null,
    long? OutputTokens = null);
