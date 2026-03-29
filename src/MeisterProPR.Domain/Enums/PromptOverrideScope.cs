namespace MeisterProPR.Domain.Enums;

/// <summary>Scope of a <see cref="Entities.PromptOverride" />.</summary>
public enum PromptOverrideScope
{
    /// <summary>Override applies to all review jobs for the owning client.</summary>
    ClientScope = 0,

    /// <summary>Override applies only to review jobs triggered by the specified crawl configuration.</summary>
    CrawlConfigScope = 1,
}
