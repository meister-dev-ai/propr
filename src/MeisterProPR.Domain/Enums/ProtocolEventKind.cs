namespace MeisterProPR.Domain.Enums;

/// <summary>Discriminates the kind of event recorded in a <see cref="MeisterProPR.Domain.Entities.ProtocolEvent" />.</summary>
public enum ProtocolEventKind
{
    /// <summary>A single call to the AI model (one <c>GetResponseAsync</c> invocation).</summary>
    AiCall = 0,

    /// <summary>A single tool invocation requested by the AI during the review loop.</summary>
    ToolCall = 1,
}
