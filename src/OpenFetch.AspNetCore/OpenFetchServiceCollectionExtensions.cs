using System.Buffers.Text;
using System.Data.Common;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenFetch.EntityFrameworkCore;

namespace OpenFetch.AspNetCore;

public static class OpenFetchServiceCollectionExtensions
{
    public const string RateLimiterPolicy = "openfetch";

    /// <summary>
    /// Registers OpenFetch over <typeparamref name="TDbContext"/> (§27). The model is built once from the context's EF metadata
    /// the first time it is needed (at the latest when endpoints are mapped) and startup fails if it is invalid (§26).
    /// </summary>
    public static IServiceCollection AddOpenFetch<TDbContext>(this IServiceCollection services, Action<OpenFetchOptions> configure)
        where TDbContext : DbContext
    {
        var options = new OpenFetchOptions();
        configure(options);
        if (options.Compiler is null) throw new InvalidOperationException("No OpenFetch provider configured; call options.UsePostgreSql().");

        services.AddSingleton(options);
        services.AddDataProtection();
        services.AddAuthorizationCore();
        services.AddProblemDetails();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICursorProtector, DataProtectionCursorProtector>();
        services.AddSingleton(new OpenFetchConnectionSource(sp => sp.GetRequiredService<TDbContext>().Database.GetDbConnection()));
        services.AddSingleton(sp => BuildModel<TDbContext>(sp, options));
        services.AddSingleton(sp => new QueryEngine(
            sp.GetRequiredService<QueryModel>(),
            options.Compiler,
            sp.GetRequiredService<ICursorProtector>(),
            options.Security,
            sp.GetServices<IRowPolicyProvider>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Per-principal concurrency limit on query endpoints (§22); rejections are 429 Problem Details.
    /// For request-rate limits attach your own policy to the builder returned by <c>MapOpenFetch</c>.
    /// </summary>
    // ponytail: one limiter per policy (ASP.NET allows one policy per endpoint); chain a custom IRateLimiterPolicy if rate + concurrency are both needed.
    public static IServiceCollection AddOpenFetchRateLimiting(this IServiceCollection services, int concurrentQueriesPerPrincipal = 4)
    {
        services.AddSingleton<OpenFetchRateLimitingMarker>();
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, ct) => new ValueTask(Results.Problem(statusCode: 429, title: "Too many requests", type: "https://openfetch.dev/problems/rate-limited",
                extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" }).ExecuteAsync(ctx.HttpContext));
            o.AddPolicy(RateLimiterPolicy, http => RateLimitPartition.GetConcurrencyLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new ConcurrencyLimiterOptions { PermitLimit = concurrentQueriesPerPrincipal, QueueLimit = 0 }));
        });
    }

    /// <summary>
    /// Uses the portable encrypted cursor profile v1 (docs/cursor-profile-v1.md) instead of ASP.NET Core Data Protection.
    /// Every instance (and any other OpenFetch implementation) sharing these keys accepts the same cursors.
    /// </summary>
    public static IServiceCollection AddOpenFetchPortableCursors(this IServiceCollection services, string currentKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        services.RemoveAll<ICursorProtector>();
        return services.AddSingleton<ICursorProtector>(new PortableCursorProtector(currentKeyId, keys));
    }

    private static QueryModel BuildModel<TDbContext>(IServiceProvider sp, OpenFetchOptions options) where TDbContext : DbContext
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var warnings = new List<string>();
        var model = QueryModelBuilder.Build(EfCoreModelReader.Read(db.Model), options, warnings);

        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OpenFetch");
        foreach (var w in warnings) logger.LogWarning("{Warning}", w);

        // §26: every referenced authorization policy must exist, or startup fails.
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var missing = model.Datasets.Values
            .SelectMany(d => d.Policies.Concat(d.Columns.Values.SelectMany(c => c.Policies)).Concat(d.Relationships.Values.SelectMany(r => r.Policies)))
            .Distinct()
            .Where(p => provider.GetPolicyAsync(p).GetAwaiter().GetResult() is null)
            .Select(p => $"Authorization policy '{p}' is referenced but not registered.")
            .ToList();
        if (missing.Count > 0) throw new OpenFetchConfigurationException(missing);

        logger.LogInformation("OpenFetch model {Version}: {Count} datasets.", model.Version, model.Datasets.Count);
        return model;
    }
}

/// <summary>Resolves the DbConnection used for a request (the TDbContext's connection).</summary>
public sealed record OpenFetchConnectionSource(Func<IServiceProvider, DbConnection> Get);

internal sealed class OpenFetchRateLimitingMarker;

/// <summary>Cursor protection with ASP.NET Core Data Protection (D4): authenticated encryption, base64url token.</summary>
public sealed class DataProtectionCursorProtector(IDataProtectionProvider provider) : ICursorProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("OpenFetch.Cursor.v1");

    public string Protect(byte[] payload) => Base64Url.EncodeToString(_protector.Protect(payload));

    public byte[] Unprotect(string token) => _protector.Unprotect(Base64Url.DecodeFromChars(token));
}
