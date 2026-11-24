using System.Buffers;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenFetch.Compiler.PostgreSql;
using OpenFetch.Compiler.SqlServer;
using OpenFetch.EntityFrameworkCore;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>Inheritance (TPH/TPT/TPC) and owned types; the same assertions run on PostgreSQL and SQL Server.</summary>
public abstract class ShapesTests
{
    protected abstract QueryEngine Engine { get; }
    protected abstract DbConnection Connection();

    private async Task<JsonElement> Run(string dataset, string json)
    {
        var context = TestEngine.Context(SeedData.TenantA);
        var prepared = await Engine.PrepareAsync(dataset, json, context);
        var output = new ArrayBufferWriter<byte>();
        await using var connection = Connection();
        await Engine.ExecuteAsync(connection, prepared, context, output, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(output.WrittenMemory).RootElement.GetProperty("data").Clone();
    }

    private static List<string> Values(JsonElement data, string property) => [.. data.EnumerateArray().Select(r => r.GetProperty(property).ToString())];

    [Fact]
    public async Task Tph_base_sees_all_types_and_derived_only_its_own()
    {
        Assert.Equal(["Generic", "Rex", "Tom", "Fido"], Values(await Run("animals", """{"select":["name"]}"""), "name"));
        Assert.Equal(["Lab", "Pug"], Values(await Run("dogs", """{"select":["name","breed"]}"""), "breed"));
        Assert.Equal(["Fido"], Values(await Run("dogs", """{"select":["name"],"where":{"type":"comparison","field":"breed","operator":"eq","value":"Pug"}}"""), "name"));
    }

    [Fact]
    public async Task Tpt_derived_joins_its_base_table()
    {
        Assert.Equal(["Generic", "Skoda"], Values(await Run("vehicles", """{"select":["make"]}"""), "make"));
        var cars = await Run("cars", """{"select":["id","make","doors"],"where":{"type":"comparison","field":"make","operator":"startsWith","value":"Sk"}}""");
        Assert.Equal(("Skoda", 5), (cars[0].GetProperty("make").GetString(), cars[0].GetProperty("doors").GetInt32()));
    }

    [Fact]
    public async Task Tpc_base_unions_concrete_tables()
    {
        Assert.Equal(["10.50", "20.00"], Values(await Run("payments", """{"select":["amount"],"orderBy":[{"field":"amount"}]}"""), "amount"));
        Assert.Equal(["4242"], Values(await Run("cardPayments", """{"select":["last4"]}"""), "last4"));
    }

    [Fact]
    public async Task Owned_reference_is_a_nested_value_and_owned_collection_is_a_collection()
    {
        var bob = await Run("people", """{"select":["name","home.city"],"where":{"type":"comparison","field":"home.city","operator":"eq","value":"Brno"}}""");
        Assert.Equal("Brno", bob[0].GetProperty("home").GetProperty("city").GetString());

        // An absent optional owned reference is null (as EF Core loads it), even when only some of its fields are selected.
        var homes = await Run("people", """{"select":["name","home.zip"],"orderBy":[{"field":"id"}]}""");
        Assert.Equal("11000", homes[0].GetProperty("home").GetProperty("zip").GetString());
        Assert.Equal(JsonValueKind.Null, homes[2].GetProperty("home").ValueKind);
        var distinct = await Run("people", """{"select":["home.city"],"distinct":true,"orderBy":[{"field":"home.city"}]}""");
        Assert.Equal(3, distinct.GetArrayLength());
        Assert.Single(distinct.EnumerateArray(), r => r.GetProperty("home").ValueKind == JsonValueKind.Null);

        var work = await Run("people", """{"select":["name"],"where":{"type":"any","relationship":"phones","where":{"type":"comparison","field":"kind","operator":"eq","value":"work"}}}""");
        Assert.Equal(["Ann"], Values(work, "name"));

        var expanded = await Run("people", """{"select":["name"],"orderBy":[{"field":"name"}],"expand":[{"relationship":"phones","select":["number","kind","extension.code"],"orderBy":[{"field":"kind"}]}]}""");
        var phones = expanded[0].GetProperty("phones");
        Assert.Equal(["mobile", "work"], phones.EnumerateArray().Select(p => p.GetProperty("kind").GetString()));
        Assert.Equal("12", phones[0].GetProperty("extension").GetProperty("code").GetString()); // owned values nest in expanded rows too
        Assert.Equal(JsonValueKind.Null, phones[1].GetProperty("extension").ValueKind);
        Assert.Equal(0, expanded[1].GetProperty("phones").GetArrayLength());

        var ex = await Assert.ThrowsAsync<OpenFetchException>(async () => await Engine.PrepareAsync("phone", """{"select":["number"]}""", TestEngine.Context(SeedData.TenantA)));
        Assert.Equal("unknown_dataset", ex.Code); // owned collections are reachable only through their owner
    }
}

[Collection("postgres")]
public sealed class PostgresShapesTests(PostgresFixture pg) : ShapesTests
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;
    private string Cs => new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = "shapes" }.ConnectionString;

    protected override QueryEngine Engine { get; } = TestEngine.Create(Model(o => o.UseNpgsql("Host=unused")), compiler: new PostgreSqlCompiler());

    protected override DbConnection Connection()
    {
        Gate.Wait();
        try
        {
            if (!_seeded)
            {
                using var db = new ShapesContext(new DbContextOptionsBuilder<ShapesContext>().UseNpgsql(Cs).Options);
                db.Database.EnsureCreated();
                ShapesContext.Seed(db);
                _seeded = true;
            }
        }
        finally { Gate.Release(); }
        return new NpgsqlConnection(Cs);
    }

    internal static QueryModel Model(Action<DbContextOptionsBuilder<ShapesContext>> provider)
    {
        var builder = new DbContextOptionsBuilder<ShapesContext>();
        provider(builder);
        return QueryModelBuilder.Build(EfCoreModelReader.Read(new ShapesContext(builder.Options).Model), new OpenFetchOptions());
    }
}

[Collection("sqlserver")]
public sealed class SqlServerShapesTests(SqlServerFixture ms) : ShapesTests
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;
    private string Cs => new SqlConnectionStringBuilder(ms.AdminConnectionString) { InitialCatalog = "shapes" }.ConnectionString;

    protected override QueryEngine Engine { get; } = TestEngine.Create(PostgresShapesTests.Model(o => o.UseSqlServer("Server=unused")), compiler: new SqlServerCompiler());

    protected override DbConnection Connection()
    {
        Gate.Wait();
        try
        {
            if (!_seeded)
            {
                using var db = new ShapesContext(new DbContextOptionsBuilder<ShapesContext>().UseSqlServer(Cs).Options);
                db.Database.EnsureCreated();
                ShapesContext.Seed(db);
                _seeded = true;
            }
        }
        finally { Gate.Release(); }
        return new SqlConnection(Cs);
    }
}
