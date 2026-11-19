using System.Buffers;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OpenFetch.Compiler.SqlServer;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>
/// SQL Server provider (§30.2). The same conformance corpus must return byte-identical results to PostgreSQL
/// (provider conformance), with every query running as an RLS-bound login.
/// </summary>
[Collection("sqlserver")]
public class SqlServerTests(SqlServerFixture ms)
{
    private static readonly SecurityOptions Rls = new OpenFetchOptions().Security.UseDatabaseRowLevelSecurity();
    private static readonly QueryEngine Engine = TestEngine.Create(SqlServerFixture.Model(TestModel.Configure), security: Rls, compiler: new SqlServerCompiler());

    private static async Task<byte[]> Run(QueryEngine engine, string connectionString, string dataset, string json, Guid tenant)
    {
        var context = TestEngine.Context(tenant);
        var prepared = await engine.PrepareAsync(dataset, json, context);
        var output = new ArrayBufferWriter<byte>();
        await using var connection = new SqlConnection(connectionString);
        await engine.ExecuteAsync(connection, prepared, context, output, TestContext.Current.CancellationToken);
        return output.WrittenSpan.ToArray();
    }

    private static List<string> Ids(byte[] result) =>
        [.. JsonDocument.Parse(result).RootElement.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];

    [Theory, MemberData(nameof(Corpus.Valid), MemberType = typeof(Corpus))]
    public async Task Conformance_results_match_postgresql(string name)
    {
        var json = Corpus.Read("valid", name);
        var prepared = await Engine.PrepareAsync(Corpus.Dataset(name), json, TestEngine.Context(SeedData.TenantA));
        Corpus.Snapshot("expected-plan-sqlserver", name + ".sql", Corpus.Plan(prepared, Engine.Compiler));

        var result = await Run(Engine, ms.AppConnectionString(), Corpus.Dataset(name), json, SeedData.TenantA);
        var expected = File.ReadAllText(Path.Combine(Corpus.Root, "conformance", "expected-results", name + ".json"));
        Assert.Equal(expected, Corpus.Result(result));
    }

    [Fact]
    public async Task Rls_alone_isolates_and_does_not_leak_through_a_one_connection_pool()
    {
        var rlsOnly = TestEngine.Create(SqlServerFixture.Model(), security: Rls, compiler: new SqlServerCompiler());
        var none = TestEngine.Create(SqlServerFixture.Model(), compiler: new SqlServerCompiler());
        var connection = ms.AppConnectionString(maxPoolSize: 1);
        for (var i = 0; i < 20; i++)
        {
            var (tenant, prefix) = i % 2 == 0 ? (SeedData.TenantA, "aaaaaaaa") : (SeedData.TenantB, "bbbbbbbb");
            var ids = Ids(await Run(rlsOnly, connection, "orders", """{"select":["id"],"page":{"first":50}}""", tenant));
            Assert.Equal(10, ids.Count);
            Assert.All(ids, id => Assert.StartsWith(prefix, id));
            Assert.Empty(Ids(await Run(none, connection, "orders", """{"select":["id"]}""", tenant)));
        }
    }

    [Fact]
    public async Task Cursor_pages_cover_all_rows_exactly_once()
    {
        var seen = new List<string>();
        string? after = null;
        do
        {
            var page = after is null ? "{\"first\":4}" : $"{{\"first\":4,\"after\":\"{after}\"}}";
            var result = await Run(Engine, ms.AppConnectionString(), "orders",
                $$$"""{"select":["id"],"orderBy":[{"field":"createdAt","direction":"desc"}],"page":{{{page}}}}""", SeedData.TenantB);
            seen.AddRange(Ids(result));
            after = JsonDocument.Parse(result).RootElement.GetProperty("page").GetProperty("endCursor").GetString();
        } while (after is not null);
        Assert.Equal(9, seen.Count);
        Assert.Equal(seen.Distinct(), seen);
    }

    [Fact]
    public async Task In_lists_compare_full_values_not_values_cut_to_column_size()
    {
        // customer.country is nvarchar(2) and total decimal(18,2): "CZxx" or 100.504 must not match "CZ"/100.50 by truncation/rounding.
        var longNumber = "CZxx";
        var json = $$$"""{"select":["id"],"where":{"type":"logical","operator":"or","children":[{"type":"comparison","field":"customer.country","operator":"in","value":["{{{longNumber}}}"]},{"type":"comparison","field":"total","operator":"in","value":[100.504]}]}}""";
        Assert.Empty(Ids(await Run(Engine, ms.AppConnectionString(), "orders", json, SeedData.TenantA)));
        var exact = """{"select":["id"],"where":{"type":"comparison","field":"total","operator":"in","value":[100.50, 200.5]}}""";
        Assert.Equal(2, Ids(await Run(Engine, ms.AppConnectionString(), "orders", exact, SeedData.TenantA)).Count);
    }

    [Fact]
    public async Task Session_context_is_cleared_after_the_query_on_a_kept_open_connection()
    {
        var context = TestEngine.Context(SeedData.TenantA);
        var prepared = await Engine.PrepareAsync("orders", """{"select":["id"]}""", context);
        await using var connection = new SqlConnection(ms.AppConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken); // caller-owned and kept open (like a DbContext connection)
        await Engine.ExecuteAsync(connection, prepared, context, new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken);
        await using var probe = new SqlCommand("SELECT SESSION_CONTEXT(N'openfetch.tenant_id')", connection);
        Assert.Equal(DBNull.Value, await probe.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("' OR 1=1--")]
    [InlineData("]; DROP TABLE sales.Orders;--")]
    [InlineData("[a-z]%_\\")]
    public async Task Hostile_values_stay_parameters(string value)
    {
        var v = JsonSerializer.Serialize(value);
        var json = $$$"""{"select":["id"],"where":{"type":"logical","operator":"or","children":[{"type":"comparison","field":"number","operator":"contains","value":{{{v}}}},{"type":"comparison","field":"customer.country","operator":"in","value":[{{{v}}}]}]}}""";
        Assert.Empty(Ids(await Run(Engine, ms.AppConnectionString(), "orders", json, SeedData.TenantA)));
        var prepared = await Engine.PrepareAsync("orders", json, TestEngine.Context(SeedData.TenantA));
        Assert.DoesNotContain(value, prepared.Plan.CommandText);
    }
}
