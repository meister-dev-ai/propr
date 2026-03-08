using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Api.Middleware;

/// <summary>
///     Middleware that validates the presence and correctness of the X-Client-Key header for incoming requests.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
/// <param name="clientRegistry">The client registry used to validate client keys.</param>
public sealed class ClientKeyMiddleware(RequestDelegate next, IClientRegistry clientRegistry)
{
    /// <summary>Validates X-Client-Key header and stores it in HttpContext.Items when valid.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // /healthz and /metrics bypass auth
        if (context.Request.Path.StartsWithSegments("/healthz") ||
            context.Request.Path.StartsWithSegments("/metrics"))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Client-Key", out var keyValues) ||
            string.IsNullOrWhiteSpace(keyValues.FirstOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing X-Client-Key header.");
            return;
        }

        var key = keyValues.First()!;
        if (!clientRegistry.IsValidKey(key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid X-Client-Key.");
            return;
        }

        context.Items["ClientKey"] = key;
        await next(context);
    }
}