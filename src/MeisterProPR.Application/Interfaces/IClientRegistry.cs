namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Registry of known clients and their keys.
/// </summary>
public interface IClientRegistry
{
    /// <summary>Returns true if the provided client key is registered and valid.</summary>
    bool IsValidKey(string clientKey);
}