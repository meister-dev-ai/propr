using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Domain.Tests.Entities;

/// <summary>
///     Tests for the <see cref="FindingDismissal" /> entity (US3, T012).
/// </summary>
public class FindingDismissalTests
{
    // T012 — Constructor sets all properties
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var id = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var entity = new FindingDismissal(id, clientId, "use idisposable pattern", "Accepted pattern", "Use IDisposable pattern here");

        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.Equal(id, entity.Id);
        Assert.Equal(clientId, entity.ClientId);
        Assert.Equal("use idisposable pattern", entity.PatternText);
        Assert.Equal("Accepted pattern", entity.Label);
        Assert.Equal("Use IDisposable pattern here", entity.OriginalMessage);
        Assert.InRange(entity.CreatedAt, before, after);
    }

    // T012 — Constructor with null label stores null
    [Fact]
    public void Constructor_NullLabel_StoresNull()
    {
        var entity = new FindingDismissal(Guid.NewGuid(), Guid.NewGuid(), "pattern", null, "original message");

        Assert.Null(entity.Label);
    }

    // T012 — UpdateLabel mutates the label
    [Fact]
    public void UpdateLabel_ChangesLabelValue()
    {
        var entity = new FindingDismissal(Guid.NewGuid(), Guid.NewGuid(), "pattern", "Old label", "original message");

        entity.UpdateLabel("New label");

        Assert.Equal("New label", entity.Label);
    }

    // T012 — UpdateLabel to null clears the label
    [Fact]
    public void UpdateLabel_Null_ClearsLabel()
    {
        var entity = new FindingDismissal(Guid.NewGuid(), Guid.NewGuid(), "pattern", "Old label", "original message");

        entity.UpdateLabel(null);

        Assert.Null(entity.Label);
    }

    // T012 — CreatedAt is set at construction (UTC)
    [Fact]
    public void Constructor_CreatedAtIsSetAtConstruction()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var entity = new FindingDismissal(Guid.NewGuid(), Guid.NewGuid(), "pattern", null, "original message");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.InRange(entity.CreatedAt, before, after);
    }
}
