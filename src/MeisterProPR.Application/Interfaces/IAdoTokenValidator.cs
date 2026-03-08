namespace MeisterProPR.Application.Interfaces;

/// <summary>Used solely for identity verification (FR-015). Never for ADO API operations.</summary>
public interface IAdoTokenValidator
{
    /// <summary>
    ///     Validates the given ADO token for basic identity checks.
    /// </summary>
    Task<bool> IsValidAsync(string adoToken, CancellationToken ct = default);
}