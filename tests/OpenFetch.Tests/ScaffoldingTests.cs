using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

public class ArchitectureTests
{
    // Spec §6: the core protocol MUST NOT depend on EF Core, ASP.NET Core, or a specific SQL provider.
    [Theory]
    [InlineData("OpenFetch.Abstractions")]
    [InlineData("OpenFetch.Core")]
    public void Core_assemblies_have_no_forbidden_references(string assembly)
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql"];
        var bad = Assembly.Load(assembly).GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => forbidden.Any(f => n.StartsWith(f, StringComparison.Ordinal)));
        Assert.Empty(bad);
    }
}

[Collection("postgres")]
public class SampleDomainTests(PostgresFixture pg)
{
    [Fact]
    public async Task Seed_has_two_isolated_tenants()
    {
        await using var db = pg.CreateDbContext();
        var counts = await db.Orders.GroupBy(o => o.TenantId).Select(g => g.Count()).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([10, 10], counts);
        Assert.True(await db.Orders.AllAsync(o => o.Customer.TenantId == o.TenantId, TestContext.Current.CancellationToken));
    }
}
