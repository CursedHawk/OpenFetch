using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenFetch.AspNetCore;

/// <summary>Marks a per-dataset query endpoint; the OpenAPI transformers key off this.</summary>
public sealed record OpenFetchDatasetMetadata(string Dataset, string? Projection = null);

public static class OpenFetchEndpoints
{
    /// <summary>
    /// Maps <c>POST {prefix}/{dataset}</c> once per dataset (dataset-specific OpenAPI operations, §10) plus an undocumented
    /// catch-all so unknown and inaccessible datasets return the identical <c>unknown_dataset</c> problem.
    /// </summary>
    public static RouteGroupBuilder MapOpenFetch(this IEndpointRouteBuilder app, string prefix = "/query")
    {
        var engine = app.ServiceProvider.GetRequiredService<QueryEngine>(); // builds and validates the model now (§26)
        var group = app.MapGroup(prefix).WithTags("OpenFetch");
        foreach (var dataset in engine.Model.Datasets.Values.Where(d => !d.Internal).OrderBy(d => d.PublicName, StringComparer.Ordinal))
        {
            var name = dataset.PublicName;
            group.MapPost("/" + name, (Func<HttpContext, Task<IResult>>)(http => QueryAsync(http, name, null)))
                .WithName(OperationId(name))
                .WithMetadata(new OpenFetchDatasetMetadata(name));
            foreach (var projection in dataset.Projections.Keys.Order(StringComparer.Ordinal))
                group.MapPost($"/{name}/{projection}", (Func<HttpContext, Task<IResult>>)(http => QueryAsync(http, name, projection)))
                    .WithName(OperationId(name, projection))
                    .WithMetadata(new OpenFetchDatasetMetadata(name, projection));
        }
        group.MapPost("/{dataset}", (HttpContext http, string dataset) => QueryAsync(http, dataset, null)).ExcludeFromDescription();
        group.MapPost("/{dataset}/{projection}", (HttpContext http, string dataset, string projection) => QueryAsync(http, dataset, projection)).ExcludeFromDescription();
        if (app.ServiceProvider.GetService<OpenFetchRateLimitingMarker>() is not null)
            group.RequireRateLimiting(OpenFetchServiceCollectionExtensions.RateLimiterPolicy);
        return group;
    }

    public static RouteHandlerBuilder MapOpenFetchCapabilities(this IEndpointRouteBuilder app, string pattern = "/query/capabilities")
    {
        var engine = app.ServiceProvider.GetRequiredService<QueryEngine>();
        var document = MetadataDocuments.Capabilities(engine.Model, engine.Compiler);
        return app.MapGet(pattern, (HttpContext http) =>
        {
            try { SecurityContext(http); }
            catch (OpenFetchException ex) { return Problem(ex); }
            return Results.Bytes(document, "application/json");
        }).WithTags("OpenFetch").WithName("getQueryCapabilities");
    }

    /// <summary>Caller-specific schema (§17.2) with a private ETag over model version + the caller's policy profile.</summary>
    public static RouteHandlerBuilder MapOpenFetchSchema(this IEndpointRouteBuilder app, string pattern = "/query/schema")
    {
        var engine = app.ServiceProvider.GetRequiredService<QueryEngine>();
        var policies = engine.Model.Datasets.Values
            .SelectMany(d => d.Policies.Concat(d.Columns.Values.SelectMany(c => c.Policies)).Concat(d.Relationships.Values.SelectMany(r => r.Policies)))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        return app.MapGet(pattern, async (HttpContext http) =>
        {
            try
            {
                var context = SecurityContext(http);
                var allowed = await QueryEngine.AllowedPoliciesAsync(context, policies, http.RequestAborted);
                var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(engine.Model.Version + "|" + string.Join(',', allowed.Order(StringComparer.Ordinal)))))[..32] + "\"";
                http.Response.Headers.CacheControl = "private, no-cache";
                http.Response.Headers.ETag = etag;
                if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Bytes(MetadataDocuments.Schema(engine.Model, p => p.All(allowed.Contains)), "application/json");
            }
            catch (OpenFetchException ex) { return Problem(ex); }
        }).WithTags("OpenFetch").WithName("getQuerySchema");
    }

    public static string OperationId(string dataset, string? projection = null) =>
        "query" + QueryModelBuilder.ComponentName(dataset) + (projection is null ? "" : QueryModelBuilder.ComponentName(projection));

    private static async Task<IResult> QueryAsync(HttpContext http, string dataset, string? projection)
    {
        var services = http.RequestServices;
        var engine = services.GetRequiredService<QueryEngine>();
        var options = services.GetRequiredService<OpenFetchOptions>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenFetch");
        var ct = http.RequestAborted;
        try
        {
            var context = SecurityContext(http);
            var body = await ReadBodyAsync(http.Request, engine.Model.Limits.MaximumDocumentBytes, ct);
            var prepared = await engine.PrepareAsync(dataset, body, context, ct, projection);
            if (options.DiagnosticMode) logger.LogDebug("OpenFetch SQL for {Dataset} ({Shape}):\n{Sql}", dataset, prepared.ShapeHash, prepared.Plan.CommandText);

            var output = new ArrayBufferWriter<byte>();
            var connection = services.GetRequiredService<OpenFetchConnectionSource>().Get(services);
            var rows = await engine.ExecuteAsync(connection, prepared, context, output, ct);
            logger.LogInformation("OpenFetch {Dataset} shape={Shape} complexity={Complexity} rows={Rows} bytes={Bytes}",
                dataset, prepared.ShapeHash, prepared.Complexity, rows, output.WrittenCount);
            return Results.Bytes(output.WrittenMemory, "application/json");
        }
        catch (OpenFetchException ex)
        {
            logger.LogInformation("OpenFetch {Dataset} rejected: {Code} at {Path}", dataset, ex.Code, ex.Path);
            return Problem(ex);
        }
        catch (QueryExecutionException ex)
        {
            // Provider error text may contain identifiers or values: logged server-side, never returned (§24).
            logger.LogError(ex.InnerException, "OpenFetch {Dataset} execution failed", dataset);
            return Problem(ex.Error);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpRequest request, int max, CancellationToken ct)
    {
        if (request.ContentLength > max) throw QueryErrors.DocumentTooLarge(max);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) throw QueryErrors.DocumentTooLarge(max);
            buffer.Write(chunk, 0, read);
        }
        return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }

    /// <summary>Trusted per-request context (§16.6): tenant only from the configured claim, never from the query.</summary>
    internal static QuerySecurityContext SecurityContext(HttpContext http)
    {
        var security = http.RequestServices.GetRequiredService<OpenFetchOptions>().Security;
        var user = http.User;
        var authenticated = user.Identity?.IsAuthenticated == true;
        if (security.AuthenticatedUserRequired && !authenticated) throw QueryErrors.Unauthenticated();

        string? tenant = null;
        if (security.TenantClaimType is { } claim)
        {
            tenant = user.FindFirst(claim)?.Value;
            if (string.IsNullOrEmpty(tenant)) throw authenticated ? QueryErrors.Forbidden("The caller has no tenant.") : QueryErrors.Unauthenticated();
        }

        var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
        return new QuerySecurityContext(user, tenant, http.RequestServices,
            async (policy, _) => (await authorization.AuthorizeAsync(user, policy)).Succeeded);
    }

    internal static IResult Problem(OpenFetchException ex)
    {
        var extensions = new Dictionary<string, object?>(ex.Extensions) { ["code"] = ex.Code };
        if (ex.Path is not null) extensions["path"] = ex.Path;
        return Results.Problem(ex.Detail, statusCode: ex.Status, title: ex.Title,
            type: "https://openfetch.dev/problems/" + ex.Code.Replace('_', '-'), extensions: extensions);
    }
}
