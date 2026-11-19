using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using OpenFetch.AspNetCore.OpenApi;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>OpenAPI contract tests (§18, §29.5): normalized committed snapshot, breaking-change detection, example validation.</summary>
[Collection("postgres")]
public class OpenApiTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    public static readonly string SnapshotPath = Path.Combine(Corpus.Root, "openapi", "v1.json");

    private async Task<JsonNode> Document(Action<OpenFetchOptions>? configure = null)
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString(), configure);
        return Normalize(JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", Ct))!);
    }

    /// <summary>Sorts object keys recursively so the snapshot is independent of generation order.</summary>
    public static JsonNode Normalize(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Normalize(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(v => v is null ? null : Normalize(v))]),
        _ => node.DeepClone(),
    };

    [Fact]
    public async Task Document_matches_committed_contract_without_breaking_changes()
    {
        var current = await Document();
        var text = current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (File.Exists(SnapshotPath))
            Assert.Empty(BreakingChanges.Find(JsonNode.Parse(File.ReadAllText(SnapshotPath))!, current));
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        Corpus.Snapshot(Path.Combine("..", "openapi"), "v1.json", text);
    }

    [Fact]
    public async Task Dataset_operations_and_schemas_are_generated_from_the_model()
    {
        var doc = await Document();
        Assert.Equal("3.1.1", (string)doc["openapi"]!);
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/query/orders", paths);
        Assert.Contains("/query/customers", paths);
        Assert.DoesNotContain("/query/{dataset}", paths);

        var op = doc["paths"]!["/query/orders"]!["post"]!;
        Assert.Equal("queryOrders", (string)op["operationId"]!);
        Assert.Equal("#/components/schemas/OrdersQuery", (string)op["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!);
        Assert.Equal(500, (int)op["x-openfetch-limits"]!["maxPageSize"]!);

        var schemas = doc["components"]!["schemas"]!;
        Assert.Equal(["comparison", "logical", "not"], schemas["OrdersFilter"]!["discriminator"]!["mapping"]!.AsObject().Select(p => p.Key));
        Assert.Equal("logical", (string)schemas["OrdersLogicalFilter"]!["properties"]!["type"]!["const"]!);
        var selectable = schemas["OrdersSelectableField"]!["enum"]!.AsArray().Select(v => (string)v!).ToList();
        Assert.Contains("customer.name", selectable);
        Assert.Equal(["eq", "ne", "in"], schemas["OrdersRow"]!["properties"]!["status"]!["x-openfetch-operators"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(["open", "processing", "shipped", "cancelled"], schemas["OrdersRow"]!["properties"]!["status"]!["enum"]!.AsArray().Select(v => (string)v!));
        Assert.Contains("Orders.FinancialData", schemas["OrdersRow"]!["properties"]!["total"]!.ToJsonString());

        // Hidden members are invisible everywhere (§7.4).
        var text = doc.ToJsonString();
        foreach (var hidden in new[] { "tenantId", "TenantId", "isDeleted", "IsDeleted", "customerId", "CustomerId", "\"sales\"", "\"crm\"" })
            Assert.DoesNotContain(hidden, text);
    }

    [Fact]
    public async Task Examples_are_valid_queries()
    {
        var doc = await Document();
        var engine = TestEngine.Create();
        foreach (var dataset in new[] { "orders", "customers" })
        {
            var example = doc["paths"]!["/query/" + dataset]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!;
            await engine.PrepareAsync(dataset, example.ToJsonString(), TestEngine.Context(SeedData.TenantA, []));
        }
    }

    [Fact]
    public async Task Policy_profile_documents_include_only_what_the_profile_may_use()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString(), o => o.Dataset<Customer>().RequirePolicy("Orders.Read"), b =>
        {
            b.Services.AddOpenApi("public", o => o.AddOpenFetch(profile: []));
            b.Services.AddOpenApi("finance", o => o.AddOpenFetch(profile: ["Orders.FinancialData", "Orders.Read"]));
        });
        var client = app.GetTestClient();
        var pub = JsonNode.Parse(await client.GetStringAsync("/openapi/public.json", Ct))!;
        var fin = JsonNode.Parse(await client.GetStringAsync("/openapi/finance.json", Ct))!;

        Assert.Null(pub["components"]!["schemas"]!["OrdersRow"]!["properties"]!["total"]);
        Assert.Null(pub["components"]!["schemas"]!["OrdersRow"]!["properties"]!["customer"]);   // target dataset needs Orders.Read
        Assert.Null(pub["paths"]!["/query/customers"]);
        Assert.Null(pub["components"]!["schemas"]!["CustomersQuery"]);
        Assert.DoesNotContain("customer.name", pub["components"]!["schemas"]!["OrdersSelectableField"]!.ToJsonString());

        Assert.NotNull(fin["components"]!["schemas"]!["OrdersRow"]!["properties"]!["total"]);
        Assert.NotNull(fin["paths"]!["/query/customers"]);
        Assert.Equal(["Orders.FinancialData", "Orders.Read"], fin["x-openfetch-profile"]!["policies"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task Detector_reports_narrowing_changes()
    {
        var before = await Document();
        var after = await Document(o =>
        {
            o.Dataset<Order>().Column(x => x.Number).Filter(false);
            o.Dataset<Order>().Column(x => x.CreatedAt).Hide();
            o.Dataset<Order>().PageSize(50, 200);
        });
        var changes = BreakingChanges.Find(before, after);
        Assert.Contains(changes, c => c.Contains("OrdersRow") && c.Contains("createdAt"));
        Assert.Contains(changes, c => c.Contains("OrdersFilterableField") && c.Contains("'number'"));
        Assert.Contains(changes, c => c.Contains("maxPageSize"));
        Assert.DoesNotContain(BreakingChanges.Find(after, before), c => c.Contains("maxPageSize")); // widening is not breaking
    }
}

/// <summary>Detects breaking OpenAPI changes (§29.5): removed paths/properties/enum values, type changes, reduced limits, stronger auth.</summary>
public static class BreakingChanges
{
    public static List<string> Find(JsonNode before, JsonNode after)
    {
        var changes = new List<string>();
        foreach (var (path, _) in before["paths"]!.AsObject().Where(p => after["paths"]![p.Key] is null)) changes.Add($"Removed path {path}");

        var oldSchemas = before["components"]!["schemas"]!.AsObject();
        var newSchemas = after["components"]!["schemas"]!.AsObject();
        foreach (var (name, schema) in oldSchemas)
        {
            if (newSchemas[name] is not { } current) { changes.Add($"Removed schema {name}"); continue; }
            foreach (var value in Values(schema!["enum"]).Except(Values(current["enum"]))) changes.Add($"{name}: removed enum value '{value}'");
            foreach (var (prop, ps) in schema["properties"]?.AsObject() ?? [])
            {
                if (current["properties"]?[prop] is not { } cp) { changes.Add($"{name}: removed property '{prop}'"); continue; }
                if (ps?["type"]?.ToJsonString() != cp["type"]?.ToJsonString()) changes.Add($"{name}.{prop}: type changed");
                foreach (var op in Values(ps?["x-openfetch-operators"]).Except(Values(cp["x-openfetch-operators"]))) changes.Add($"{name}.{prop}: removed operator '{op}'");
                foreach (var p in Values(cp["x-openfetch-authorization"]?["policies"]).Except(Values(ps?["x-openfetch-authorization"]?["policies"]))) changes.Add($"{name}.{prop}: new required policy '{p}'");
            }
        }

        foreach (var (path, item) in before["paths"]!.AsObject())
        {
            var oldLimits = item?["post"]?["x-openfetch-limits"]?.AsObject();
            var newLimits = after["paths"]![path]?["post"]?["x-openfetch-limits"];
            foreach (var (limit, value) in oldLimits ?? [])
                if (newLimits?[limit] is { } nv && (int)nv < (int)value!) changes.Add($"{path}: {limit} reduced from {value} to {nv}");
        }
        return changes;
    }

    private static IEnumerable<string> Values(JsonNode? array) => array is JsonArray a ? a.Select(v => v?.ToJsonString() ?? "null").Select(s => s.Trim('"')) : [];
}
