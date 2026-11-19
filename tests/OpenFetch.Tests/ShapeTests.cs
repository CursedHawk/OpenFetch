using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>
/// Query-shape stability (§23): SQL text is a pure function of the shape, never of values, so the provider's statement
/// cache (Npgsql auto-prepare) and the database plan cache key on it directly. Tenant ids never change the text.
/// </summary>
public class ShapeTests
{
    private static readonly QueryEngine Engine = TestEngine.Create();

    private static async Task<PreparedQuery> Prepare(string where, Guid tenant) =>
        await Engine.PrepareAsync("orders", $$$"""{"select":["id","number"],"where":{{{where}}},"orderBy":[{"field":"createdAt"}]}""", TestEngine.Context(tenant));

    [Fact]
    public async Task Same_shape_with_different_values_and_tenants_shares_sql_and_shape_key()
    {
        var a = await Prepare("""{"type":"comparison","field":"status","operator":"in","value":["open"]}""", SeedData.TenantA);
        var b = await Prepare("""{"type":"comparison","field":"status","operator":"in","value":["open","shipped","cancelled"]}""", SeedData.TenantB);
        Assert.Equal(a.Plan.CommandText, b.Plan.CommandText);
        Assert.Equal(a.ShapeHash, b.ShapeHash);
        Assert.NotEqual(a.Scope, b.Scope); // tenant lives in the scope key, not the shape
    }

    [Fact]
    public async Task Different_structure_or_operand_kind_changes_shape()
    {
        var eq = await Prepare("""{"type":"comparison","field":"number","operator":"eq","value":"x"}""", SeedData.TenantA);
        var ne = await Prepare("""{"type":"comparison","field":"number","operator":"ne","value":"x"}""", SeedData.TenantA);
        var isNull = await Prepare("""{"type":"comparison","field":"customer.country","operator":"eq","value":null}""", SeedData.TenantA);
        Assert.NotEqual(eq.ShapeHash, ne.ShapeHash);
        Assert.NotEqual(eq.Plan.CommandText, ne.Plan.CommandText);
        Assert.NotEqual(eq.ShapeHash, isNull.ShapeHash);
    }
}
