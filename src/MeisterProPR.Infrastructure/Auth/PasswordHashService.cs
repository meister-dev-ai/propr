namespace MeisterProPR.Infrastructure.Auth;

using BCrypt.Net;
using MeisterProPR.Application.Interfaces;

/// <summary>BCrypt-backed password hashing service.</summary>
public sealed class PasswordHashService : IPasswordHashService
{
    public string Hash(string password) =>
        BCrypt.HashPassword(password);

    public bool Verify(string password, string hash) =>
        BCrypt.Verify(password, hash);
}
