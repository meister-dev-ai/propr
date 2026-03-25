namespace MeisterProPR.Infrastructure;

/// <summary>Truncation limits applied when persisting protocol event text content.</summary>
internal static class ProtocolLimits
{
    /// <summary>Maximum number of characters stored for input text samples and output summaries.</summary>
    public const int TextSampleMaxLength = 50_000;
}
