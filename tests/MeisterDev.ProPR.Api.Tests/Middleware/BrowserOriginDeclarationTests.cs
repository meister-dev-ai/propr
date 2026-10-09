// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Tests.Middleware;

public sealed class BrowserOriginDeclarationTests
{
    [Fact]
    public void OriginDeclarationsAreAvailableWithoutLiveProviderCapabilities()
    {
        var services = new ServiceCollection();
        services.AddScmProviderLocalDeclarations();
        services.AddScoped<IScmProviderRegistry>(_ => throw new InvalidOperationException("Live registry must not resolve."));
        var configuration = new ConfigurationBuilder().Build();
        services.AddBrowserCorsPolicies(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var options = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var policy = options.GetPolicy(options.DefaultPolicyName);
        Assert.NotNull(policy);
        Assert.True(policy.IsOriginAllowed("https://dev.azure.com"));
        Assert.True(policy.IsOriginAllowed("ftp://tenant.visualstudio.com:4321"));
        Assert.True(policy.SupportsCredentials);
        Assert.True(policy.AllowAnyHeader);
        Assert.True(policy.AllowAnyMethod);
    }

    [Fact]
    public void BrowserOriginPolicyConsumesExplicitDeclarations()
    {
        ScmBrowserOriginDeclaration[] declarations =
        [
            new(ScmProvider.Forgejo, ["https://custom.example"], [".native.example"]),
        ];
        var configuration = new ConfigurationBuilder().Build();
        Assert.True(BrowserOriginPolicy.IsAllowedOrigin("https://custom.example", configuration, declarations));
        Assert.True(BrowserOriginPolicy.IsAllowedOrigin("http://tenant.native.example:42", configuration, declarations));
        Assert.False(BrowserOriginPolicy.IsAllowedOrigin("https://dev.azure.com", configuration, declarations));
        Assert.False(BrowserOriginPolicy.IsAllowedOrigin("https://tenant.visualstudio.com", configuration, declarations));
    }

    [Theory]
    [InlineData("https://dev.azure.com", true)]
    [InlineData("https://dev.azure.com/", false)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("ftp://tenant.VISUALSTUDIO.com:1234", true)]
    [InlineData("http://tenant.gallerycdn.vsassets.io:4567", true)]
    [InlineData("https://visualstudio.com", false)]
    [InlineData("https://gallerycdn.vsassets.io", false)]
    [InlineData("https://visualstudio.com.example", false)]
    [InlineData("moz-extension://local-id", false)]
    [InlineData("https://configured.example", true)]
    [InlineData("https://public.example", true)]
    public void ExistingOriginGrammarIsPreserved(string origin, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CORS_ORIGINS"] = " https://configured.example ,https://CONFIGURED.example",
                ["MEISTER_PUBLIC_BASE_URL"] = "https://public.example/path",
            }).Build();
        var services = new ServiceCollection().AddScmProviderLocalDeclarations();
        using var provider = services.BuildServiceProvider();
        var declarations = provider.GetServices<ScmBrowserOriginDeclaration>().ToArray();
        Assert.Equal(allowed, BrowserOriginPolicy.IsAllowedOrigin(origin, configuration, declarations));
    }
}
