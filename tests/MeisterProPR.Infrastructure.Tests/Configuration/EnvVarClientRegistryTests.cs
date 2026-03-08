using MeisterProPR.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Infrastructure.Tests.Configuration;

public class EnvVarClientRegistryTests : IDisposable
{
    private const string EnvVar = "MEISTER_CLIENT_KEYS";
    private readonly string? _originalValue = Environment.GetEnvironmentVariable(EnvVar);

    public void Dispose()
    {
        // Restore original env var value
        Environment.SetEnvironmentVariable(EnvVar, this._originalValue);
    }

    private static EnvVarClientRegistry CreateRegistry()
    {
        return new EnvVarClientRegistry(new ConfigurationBuilder().AddEnvironmentVariables().Build());
    }

    [Fact]
    public void Constructor_EmptyMeisterClientKeys_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "");

        Assert.Throws<InvalidOperationException>(CreateRegistry);
    }

    [Fact]
    public void Constructor_KeysWithSpaces_AreTrimmed()
    {
        Environment.SetEnvironmentVariable(EnvVar, " key1 , key2 ");
        var registry = CreateRegistry();

        Assert.True(registry.IsValidKey("key1"));
        Assert.True(registry.IsValidKey("key2"));
    }

    [Fact]
    public void Constructor_MultipleKeys_AllRegistered()
    {
        Environment.SetEnvironmentVariable(EnvVar, "key1,key2,key3");
        var registry = CreateRegistry();

        Assert.True(registry.IsValidKey("key1"));
        Assert.True(registry.IsValidKey("key2"));
        Assert.True(registry.IsValidKey("key3"));
    }

    [Fact]
    public void Constructor_NullMeisterClientKeys_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);

        Assert.Throws<InvalidOperationException>(CreateRegistry);
    }

    [Fact]
    public void Constructor_WhitespaceOnlyKeys_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "   ");

        Assert.Throws<InvalidOperationException>(CreateRegistry);
    }

    [Fact]
    public void IsValidKey_IsCaseSensitive()
    {
        Environment.SetEnvironmentVariable(EnvVar, "MyKey");
        var registry = CreateRegistry();

        Assert.True(registry.IsValidKey("MyKey"));
        Assert.False(registry.IsValidKey("mykey"));
        Assert.False(registry.IsValidKey("MYKEY"));
    }

    [Fact]
    public void IsValidKey_UnknownKey_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable(EnvVar, "test-key-123");
        var registry = CreateRegistry();

        Assert.False(registry.IsValidKey("unknown-key"));
    }

    [Fact]
    public void IsValidKey_ValidKeyFromEnvVar_ReturnsTrue()
    {
        Environment.SetEnvironmentVariable(EnvVar, "test-key-123,another-key");
        var registry = CreateRegistry();

        Assert.True(registry.IsValidKey("test-key-123"));
        Assert.True(registry.IsValidKey("another-key"));
    }
}