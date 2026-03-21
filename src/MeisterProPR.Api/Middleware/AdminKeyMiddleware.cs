namespace MeisterProPR.Api.Middleware;

/// <summary>Validates the <c>X-Admin-Key</c> header for admin-only endpoints.</summary>
public sealed class AdminKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private const string AdminKeyHeader = "X-Admin-Key";

    /// <inheritdoc cref="IMiddleware.InvokeAsync" />
    public async Task InvokeAsync(HttpContext context)
    {
        var adminKey = configuration["MEISTER_ADMIN_KEY"];

        // Only enforce on routes that opted in via the [RequireAdminKey] attribute or policy
        // For simplicity: enforce on all /clients (admin ops) and /jobs routes
        var path = context.Request.Path.Value ?? string.Empty;
        var isAdminRoute = path.StartsWith("/clients", StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith("/jobs", StringComparison.OrdinalIgnoreCase);

        // Routes that are NOT admin-only even though under /clients: crawl-configurations sub-resource
        // handled by ClientKeyMiddleware via [RequireClientKey] (ownership check in controller)
        // We only block /clients top-level admin endpoints here.
        // But AdminKeyMiddleware is applied to all; controllers verify appropriately.
        // For simplicity, let controllers decide — middleware just validates and sets a flag.

        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            var providedKey = context.Request.Headers[AdminKeyHeader].FirstOrDefault();
            // Store whether admin key was provided and valid
            context.Items["IsAdmin"] = !string.IsNullOrWhiteSpace(providedKey) &&
                                       providedKey == adminKey;
        }
        else
        {
            // No admin key configured — admin endpoints are effectively disabled
            context.Items["IsAdmin"] = false;
        }

        await next(context);
    }
}
