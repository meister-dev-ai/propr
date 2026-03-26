namespace MeisterProPR.Application.Interfaces;

/// <summary>Hashes and verifies passwords using BCrypt.</summary>
public interface IPasswordHashService
{
    /// <summary>Returns a BCrypt hash of <paramref name="password"/>.</summary>
    string Hash(string password);

    /// <summary>Returns true if <paramref name="password"/> matches <paramref name="hash"/>.</summary>
    bool Verify(string password, string hash);
}
