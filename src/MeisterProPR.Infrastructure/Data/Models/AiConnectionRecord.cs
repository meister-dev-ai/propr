using MeisterProPR.Infrastructure.Data.Models;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a per-client AI connection configuration.</summary>
public sealed class AiConnectionRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = string.Empty;
    public string[] Models { get; set; } = [];
    public bool IsActive { get; set; }
    public string? ActiveModel { get; set; }
    public string? ApiKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Optional model category tag. Nullable; maps to a <c>smallint</c> column.
    ///     Per-client unique when non-null (enforced via partial unique index).
    /// </summary>
    public short? ModelCategory { get; set; }

    public ClientRecord? Client { get; set; }
}
