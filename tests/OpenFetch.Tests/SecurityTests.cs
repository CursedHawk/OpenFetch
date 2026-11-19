using System.Buffers;
using System.Text.Json;
using Npgsql;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>Dataset/column/relationship authorization and row-policy injection (§16.2-16.4), offline.</summary>
public class SecurityTests
{
    private static readonly QueryModel Guarded = TestModel.Build(o =>
    {
        TestModel.Configure(o);
        o.Dataset<Order>().RequirePolicy("Orders.Read");
        o.Dataset<Customer>().RequirePolicy("Customers.Read");
    });

    private static async Task<OpenFetchException> Fails(QueryEngine engine, string dataset, string json, string[] policies) =>
        await Assert.ThrowsAsync<OpenFetchException>(async () => await engine.PrepareAsync(dataset, json, TestEngine.Context(SeedData.TenantA, policies)));

    [Fact]
    public async Task Inaccessible_dataset_is_indistinguishable_from_unknown()
    {
        var engine = TestEngine.Create(Guarded);
        var denied = await Fails(engine, "orders", """{"select":["id"]}""", []);
        var unknown = QueryErrors.UnknownDataset("orders");
        Assert.Equal((unknown.Code, unknown.Status, unknown.Title, unknown.Detail), (denied.Code, denied.Status, denied.Title, denied.Detail));
        var missing = await Fails(engine, "nope", """{"select":["id"]}""", TestEngine.AllPolicies);
        Assert.Equal(denied.Detail!.Replace("orders", "nope"), missing.Detail);
        // Even an invalid body cannot distinguish them.
        Assert.Equal("unknown_dataset", (await Fails(engine, "orders", "{", [])).Code);
    }

    [Theory]
    [InlineData("""{"select":["total"]}""", "/select/0")]
    [InlineData("""{"select":["id"],"where":{"type":"comparison","field":"total","operator":"gt","value":1}}""", "/where/field")]
    [InlineData("""{"select":["id"],"orderBy":[{"field":"total"}]}""", "/orderBy/0/field")]
    public async Task Select_denied_column_is_not_filterable_or_sortable(string json, string path)
    {
        var ex = await Fails(TestEngine.Create(), "orders", json, ["Orders.Read"]);
        Assert.Equal(("unknown_field", path), (ex.Code, ex.Path));
    }

    [Fact]
    public async Task Relationship_requires_target_dataset_policy()
    {
        var engine = TestEngine.Create(Guarded);
        var ex = await Fails(engine, "orders", """{"select":["customer.name"]}""", ["Orders.Read"]);
        Assert.Equal("unknown_field", ex.Code);
        await engine.PrepareAsync("orders", """{"select":["customer.name"]}""", TestEngine.Context(SeedData.TenantA, ["Orders.Read", "Customers.Read"]));
    }

    [Fact]
    public async Task Relationship_policy_is_enforced()
    {
        var engine = TestEngine.Create(TestModel.Build(o => { TestModel.Configure(o); o.Dataset<Order>().Relationship(x => x.Customer).RequirePolicy("Customers.Read"); }));
        Assert.Equal("unknown_field", (await Fails(engine, "orders", """{"select":["customer.name"]}""", [])).Code);
    }

    [Fact]
    public async Task Client_filter_is_nested_under_policy_and_cannot_escape()
    {
        var engine = TestEngine.Create();
        // OR-escape and NOT-wrapping attempts: the policy stays a separate AND-ed, parenthesized predicate.
        var q = await engine.PrepareAsync("orders", """
            {"select":["id"],"where":{"type":"not","operand":{"type":"logical","operator":"or","children":[
              {"type":"comparison","field":"id","operator":"ne","value":"aaaaaaaa-1111-0000-0000-000000000001"},
              {"type":"comparison","field":"number","operator":"eq","value":"x"}]}}}
            """, TestEngine.Context(SeedData.TenantA));
        Assert.StartsWith("""WHERE ((t0."TenantId" = @p0) AND (NOT (t0."IsDeleted" = @p1)))""", q.Plan.CommandText.Split('\n')[2]);
        Assert.StartsWith("  AND (NOT (", q.Plan.CommandText.Split('\n')[3]);
        Assert.Equal(SeedData.TenantA, q.Plan.Parameters[0].Value);
    }

    [Fact]
    public async Task Row_policy_provider_adds_trusted_predicate_on_hidden_field()
    {
        var engine = TestEngine.Create(providers: [new NotDeletedProvider()]);
        var q = await engine.PrepareAsync("customers", """{"select":["id"]}""", TestEngine.Context(SeedData.TenantA));
        Assert.Contains("""t0."TenantId" = @p0) AND (t0."Name" <> @p1)""", q.Plan.CommandText);
    }

    private sealed class NotDeletedProvider : IRowPolicyProvider
    {
        public ValueTask<FilterNode?> BuildPolicyAsync(QuerySecurityContext context, DatasetDescriptor dataset, CancellationToken ct) =>
            ValueTask.FromResult<FilterNode?>(dataset.PublicName == "customers"
                ? new ComparisonFilter(new FieldRef("Name"), QueryOperator.NotEqual, new ConstantOperand(new StringValue("banned")))
                : null);
    }

    [Fact]
    public async Task Complexity_is_scored_after_policy_injection_and_enforced()
    {
        var engine = TestEngine.Create();
        var q = await engine.PrepareAsync("orders", """{"select":["id"]}""", TestEngine.Context(SeedData.TenantA));
        // root 1 + 1 field + 2 policy conditions x 2 (indexed/unpenalized as trusted) = 6
        Assert.Equal(6, q.Complexity);

        var join = await engine.PrepareAsync("orders", """{"select":["id","customer.name"],"orderBy":[{"field":"number"}],"where":{"type":"comparison","field":"number","operator":"contains","value":"x"}}""", TestEngine.Context(SeedData.TenantA));
        // 1 + 2 fields + 15 join + 4 root policy + 2 join policy + (2 + 25 leading wildcard + 20 unindexed) + 30 unindexed sort
        Assert.Equal(101, join.Complexity);

        // docs/complexity-v1.md worked example (§31 reference request) and key-first ordering.
        var reference = await engine.PrepareAsync("orders", Corpus.Read("valid", "orders.reference"), TestEngine.Context(SeedData.TenantA));
        Assert.Equal(84, reference.Complexity);
        var keyFirst = await engine.PrepareAsync("orders", """{"select":["id"],"orderBy":[{"field":"id"},{"field":"createdAt"}]}""", TestEngine.Context(SeedData.TenantA));
        Assert.Equal(36, keyFirst.Complexity);

        var tight = TestEngine.Create(TestModel.Build(o => { TestModel.Configure(o); o.Security.MaximumComplexity(20); }));
        var ex = await Assert.ThrowsAsync<OpenFetchException>(async () => await tight.PrepareAsync("orders", """{"select":["id","customer.name"]}""", TestEngine.Context(SeedData.TenantA)));
        Assert.Equal("query_too_complex", ex.Code);
    }
}

/// <summary>Database-backed isolation: application policies, PostgreSQL RLS, small pool, cursors (§16.5, §20.1, §29.3).</summary>
[Collection("postgres")]
public class IsolationTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Run(QueryEngine engine, string connectionString, string dataset, string json, Guid tenant)
    {
        var context = TestEngine.Context(tenant);
        var prepared = await engine.PrepareAsync(dataset, json, context);
        var output = new ArrayBufferWriter<byte>();
        await using var connection = new NpgsqlConnection(connectionString);
        await engine.ExecuteAsync(connection, prepared, context, output, Ct);
        return JsonDocument.Parse(output.WrittenMemory).RootElement.Clone();
    }

    private static IEnumerable<string> Ids(JsonElement result) => result.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("id").GetString()!);

    [Theory]
    [InlineData("""{"select":["id"]}""")]
    [InlineData("""{"select":["id"],"where":{"type":"comparison","field":"id","operator":"eq","value":"bbbbbbbb-1111-0000-0000-000000000001"}}""")]
    [InlineData("""{"select":["id"],"where":{"type":"logical","operator":"or","children":[{"type":"comparison","field":"total","operator":"gt","value":0},{"type":"comparison","field":"total","operator":"lte","value":0}]}}""")]
    [InlineData("""{"select":["id","customer.name"],"orderBy":[{"field":"customer.name","direction":"desc"}]}""")]
    [InlineData("""{"select":["id"],"where":{"type":"comparison","field":"customer.name","operator":"startsWith","value":"Customer B"}}""")]
    public async Task Application_policy_isolates_tenants(string json)
    {
        var result = await Run(TestEngine.Create(), pg.ConnectionString, "orders", json, SeedData.TenantA);
        Assert.All(Ids(result), id => Assert.StartsWith("aaaaaaaa", id));
        Assert.DoesNotContain("Customer B", result.GetRawText());
    }

    [Fact]
    public async Task Database_rls_alone_isolates_tenants_with_app_policy_disabled()
    {
        var noAppPolicy = TestModel.Build();
        var options = new OpenFetchOptions();
        options.Security.UseDatabaseRowLevelSecurity();
        var engine = TestEngine.Create(noAppPolicy, security: options.Security);

        var a = await Run(engine, pg.AppConnectionString(), "orders", """{"select":["id"],"page":{"first":500}}""", SeedData.TenantA);
        Assert.Equal(10, Ids(a).Count()); // RLS only: soft-deleted row visible, other tenant never
        Assert.All(Ids(a), id => Assert.StartsWith("aaaaaaaa", id));

        // Without RLS settings the app role sees nothing (fail closed).
        var plain = await Run(TestEngine.Create(noAppPolicy), pg.AppConnectionString(), "orders", """{"select":["id"]}""", SeedData.TenantA);
        Assert.Empty(Ids(plain));
    }

    [Fact]
    public async Task Rls_settings_do_not_leak_across_pooled_connections()
    {
        var options = new OpenFetchOptions();
        options.Security.UseDatabaseRowLevelSecurity();
        var engine = TestEngine.Create(TestModel.Build(), security: options.Security);
        var noRls = TestEngine.Create(TestModel.Build());
        var connection = pg.AppConnectionString(maxPoolSize: 1);

        for (var i = 0; i < 20; i++)
        {
            var tenant = i % 2 == 0 ? SeedData.TenantA : SeedData.TenantB;
            var prefix = i % 2 == 0 ? "aaaaaaaa" : "bbbbbbbb";
            var ids = Ids(await Run(engine, connection, "orders", """{"select":["id"]}""", tenant)).ToList();
            Assert.Equal(10, ids.Count);
            Assert.All(ids, id => Assert.StartsWith(prefix, id));
            // The same physical connection, used without settings, must not inherit the previous tenant.
            Assert.Empty(Ids(await Run(noRls, connection, "orders", """{"select":["id"]}""", tenant)));
        }
    }

    [Fact]
    public async Task Rls_also_applies_to_joined_rows()
    {
        var options = new OpenFetchOptions();
        options.Security.UseDatabaseRowLevelSecurity();
        var engine = TestEngine.Create(TestModel.Build(), security: options.Security);
        var result = await Run(engine, pg.AppConnectionString(), "orders", """{"select":["id","customer.name"]}""", SeedData.TenantB);
        Assert.All(result.GetProperty("data").EnumerateArray(), r => Assert.StartsWith("Customer B", r.GetProperty("customer").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Cursor_pages_cover_all_rows_exactly_once_with_mixed_ordering()
    {
        var engine = TestEngine.Create();
        var seen = new List<string>();
        string? after = null;
        do
        {
            var page = after is null ? "{\"first\":3}" : $"{{\"first\":3,\"after\":\"{after}\"}}";
            var result = await Run(engine, pg.ConnectionString, "orders",
                $$"""{"select":["id","createdAt"],"orderBy":[{"field":"createdAt","direction":"desc"},{"field":"id"}],"page":{{page}}}""", SeedData.TenantA);
            seen.AddRange(Ids(result));
            after = result.GetProperty("page").GetProperty("endCursor").GetString();
        } while (after is not null);
        Assert.Equal(9, seen.Count);
        Assert.Equal(seen.Distinct(), seen);
        Assert.Equal(seen.OrderByDescending(x => x, StringComparer.Ordinal), seen); // createdAt order == id order in seed
    }

    private async Task<string> FirstCursor(QueryEngine engine, Guid tenant, string where = "")
    {
        var result = await Run(engine, pg.ConnectionString, "orders", $$$"""{"select":["id"]{{{where}}},"page":{"first":2}}""", tenant);
        return result.GetProperty("page").GetProperty("endCursor").GetString()!;
    }

    [Fact]
    public async Task Cursor_rejects_tampering_replay_shape_change_model_change_and_expiry()
    {
        var time = new ManualTime();
        var engine = TestEngine.Create(time: time);
        var cursor = await FirstCursor(engine, SeedData.TenantA);
        string Next(string c, string where = "") => $$$"""{"select":["id"]{{{where}}},"page":{"first":2,"after":"{{{c}}}"}}""";

        async Task<string> Code(QueryEngine e, string json, Guid tenant) =>
            (await Assert.ThrowsAsync<OpenFetchException>(async () => await e.PrepareAsync("orders", json, TestEngine.Context(tenant)))).Code;

        var tampered = cursor[..^4] + (cursor[^4] == 'A' ? 'B' : 'A') + cursor[^3..];
        Assert.Equal("invalid_cursor", await Code(engine, Next(tampered), SeedData.TenantA));
        Assert.Equal("cursor_scope_mismatch", await Code(engine, Next(cursor), SeedData.TenantB));
        Assert.Equal("cursor_scope_mismatch", await Code(engine, Next(cursor, ""","where":{"type":"comparison","field":"number","operator":"eq","value":"x"}"""), SeedData.TenantA));
        var otherModel = TestEngine.Create(TestModel.Build(o => { TestModel.Configure(o); o.Dataset<Order>().Description("v2"); }), time);
        Assert.Equal("cursor_scope_mismatch", await Code(otherModel, Next(cursor), SeedData.TenantA));

        await engine.PrepareAsync("orders", Next(cursor), TestEngine.Context(SeedData.TenantA)); // still valid
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal("invalid_cursor", await Code(engine, Next(cursor), SeedData.TenantA));
    }

    [Fact]
    public async Task Counts_and_groups_only_see_the_callers_tenant()
    {
        static long Count(JsonElement r) => r.GetProperty("data")[0].GetProperty("n").GetInt64();
        const string count = """{"aggregates":[{"function":"count","alias":"n"}]}""";
        Assert.Equal(9, Count(await Run(TestEngine.Create(), pg.ConnectionString, "orders", count, SeedData.TenantA)));

        var options = new OpenFetchOptions();
        options.Security.UseDatabaseRowLevelSecurity();
        var rlsOnly = TestEngine.Create(TestModel.Build(), security: options.Security);
        Assert.Equal(10, Count(await Run(rlsOnly, pg.AppConnectionString(), "orders", count, SeedData.TenantB))); // RLS alone: includes soft-deleted, never tenant A

        var groups = await Run(TestEngine.Create(), pg.ConnectionString, "orders",
            """{"groupBy":["customer.country"],"aggregates":[{"function":"countDistinct","field":"customer.id","alias":"n"}]}""", SeedData.TenantB);
        Assert.Equal(["SK", null], groups.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("customer").GetProperty("country").GetString()));
    }

    [Fact]
    public async Task Error_responses_do_not_differ_between_tenants()
    {
        // Oracle check: probing another tenant's id yields an empty page, exactly like a non-existent id.
        var engine = TestEngine.Create();
        static string Probe(string id) => $$$"""{"select":["id"],"where":{"type":"comparison","field":"id","operator":"eq","value":"{{{id}}}"}}""";
        var other = await Run(engine, pg.ConnectionString, "orders", Probe("bbbbbbbb-1111-0000-0000-000000000001"), SeedData.TenantA);
        var none = await Run(engine, pg.ConnectionString, "orders", Probe("cccccccc-1111-0000-0000-000000000001"), SeedData.TenantA);
        Assert.Equal(none.GetProperty("data").GetRawText(), other.GetProperty("data").GetRawText());
        Assert.Equal(none.GetProperty("page").GetRawText(), other.GetProperty("page").GetRawText());
    }

    [Theory]
    [InlineData("' OR 1=1--")]
    [InlineData("name]; DROP TABLE Users;--")]
    [InlineData("x UNION SELECT * FROM pg_user")]
    [InlineData("\"; DROP SCHEMA sales CASCADE; --")]
    [InlineData("%_\\")]
    public async Task Hostile_values_stay_parameters(string value)
    {
        var engine = TestEngine.Create();
        string Query(string v) => $$$"""{"select":["id"],"where":{"type":"logical","operator":"or","children":[{"type":"comparison","field":"number","operator":"eq","value":{{{JsonSerializer.Serialize(v)}}}},{"type":"comparison","field":"number","operator":"contains","value":{{{JsonSerializer.Serialize(v)}}}}]}}""";
        var benign = await engine.PrepareAsync("orders", Query("benign"), TestEngine.Context(SeedData.TenantA));
        var hostile = await engine.PrepareAsync("orders", Query(value), TestEngine.Context(SeedData.TenantA));
        Assert.Equal(benign.Plan.CommandText, hostile.Plan.CommandText);
        Assert.Contains(hostile.Plan.Parameters, p => Equals(p.Value, value));
        Assert.Empty(Ids(await Run(engine, pg.ConnectionString, "orders", Query(value), SeedData.TenantA)));
        await using var db = pg.CreateDbContext();
        Assert.Equal(20, db.Orders.Count()); // nothing executed
    }
}

public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
