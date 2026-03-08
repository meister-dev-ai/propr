using System.Net;
using System.Net.Http.Headers;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed class AdoTokenValidator(IHttpClientFactory httpClientFactory) : IAdoTokenValidator
{
    public async Task<bool> IsValidAsync(string adoToken, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("AdoTokenValidator");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://app.vssps.visualstudio.com/_apis/connectionData?api-version=7.1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adoToken);
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.OK;
    }
}