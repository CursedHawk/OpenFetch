using Microsoft.EntityFrameworkCore;
using OpenFetch.EntityFrameworkCore;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>Builds a QueryModel from the sample AppDbContext metadata (no database connection needed).</summary>
public static class TestModel
{
    private static readonly Microsoft.EntityFrameworkCore.Metadata.IModel EfModel =
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=unused").Options).Model;

    public static IReadOnlyList<EntityMapping> Mappings { get; } = [.. EfCoreModelReader.Read(EfModel)];

    public static QueryModel Build(Action<OpenFetchOptions>? configure = null, ICollection<string>? warnings = null)
    {
        var options = new OpenFetchOptions();
        configure?.Invoke(options);
        return QueryModelBuilder.Build(Mappings, options, warnings);
    }

    public static QueryModel Default { get; } = Build(Configure);

    /// <summary>Standard test configuration: tenant isolation + soft-delete as trusted row policies.</summary>
    public static void Configure(OpenFetchOptions o)
    {
        o.Dataset<Order>()
            .ApplyRowPolicy<TenantContext>((x, c) => x.TenantId == c.TenantId && !x.IsDeleted)
            .Projection("summary", "id", "number", "status", "customer.name");
        o.Dataset<Customer>().ApplyRowPolicy<TenantContext>((x, c) => x.TenantId == c.TenantId);
    }
}

/// <summary>Per-request row-policy context, resolved from DI.</summary>
public sealed class TenantContext
{
    public Guid TenantId { get; set; }
}
