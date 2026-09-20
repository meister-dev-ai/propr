// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     What a driver has to say about a credential field before a host can collect one.
/// </summary>
/// <remarks>
///     The name is the key the value is stored and read back under, and the label is what an operator sees where
///     it is entered. A declaration missing either produces a form with an unlabelled box and a credential
///     stored under a key nothing reads, so it is refused where the field is declared and not where it is used.
/// </remarks>
public sealed class ProviderCredentialFieldTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFieldWithoutAName_IsRefused(string? name)
    {
        var failure = Assert.Throws<ArgumentException>(() => new ProviderCredentialField(name!, "Access key ID"));

        Assert.Equal("Name", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFieldWithoutALabel_IsRefused(string? label)
    {
        var failure = Assert.Throws<ArgumentException>(() => new ProviderCredentialField("accessKeyId", label!));

        Assert.Equal("Label", failure.ParamName);
    }

    // The constructor is not the only way the value is set. An object initializer and a `with` expression both
    // write over what it produced, so a check that ran only there would leave a field named by whitespace — the
    // key a credential is stored and read back under.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFieldRenamedByAnObjectInitializer_IsRefused(string? name)
    {
        var failure = Assert.Throws<ArgumentException>(() => new ProviderCredentialField("accessKeyId", "Access key ID") { Name = name! });

        Assert.Equal("Name", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFieldRenamedByAWithExpression_IsRefused(string? name)
    {
        var declared = new ProviderCredentialField("accessKeyId", "Access key ID");

        var failure = Assert.Throws<ArgumentException>(() => declared with { Name = name! });

        Assert.Equal("Name", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFieldRelabelledByAWithExpression_IsRefused(string? label)
    {
        var declared = new ProviderCredentialField("accessKeyId", "Access key ID");

        var failure = Assert.Throws<ArgumentException>(() => declared with { Label = label! });

        Assert.Equal("Label", failure.ParamName);
    }

    // A `with` expression copies every member before it applies the change, so the copy has to survive the same
    // accessor that refuses a blank.
    [Fact]
    public void AFieldCopiedByAWithExpressionKeepsWhatItWasNotAskedToChange()
    {
        var declared = new ProviderCredentialField("accessKeyId", "Access key ID", IsRequired: false, Hint: "From the IAM console.");

        var relabelled = declared with { Label = "Access key" };

        Assert.Equal("accessKeyId", relabelled.Name);
        Assert.Equal("Access key", relabelled.Label);
        Assert.False(relabelled.IsRequired);
        Assert.Equal("From the IAM console.", relabelled.Hint);
    }

    [Fact]
    public void ADeclaredFieldKeepsWhatItWasGiven()
    {
        var field = new ProviderCredentialField("sessionToken", "Session token", IsSecret: true, IsRequired: false, Hint: "Temporary credentials only.");

        Assert.Equal("sessionToken", field.Name);
        Assert.Equal("Session token", field.Label);
        Assert.False(field.IsRequired);
        Assert.Equal("Temporary credentials only.", field.Hint);
    }
}
