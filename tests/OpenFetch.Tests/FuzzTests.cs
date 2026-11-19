using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>
/// Property-based fuzzing of parser + validator + compiler (§29.4). The only acceptable outcomes are a plan or an
/// OpenFetchException; plans must never contain client text outside parameters.
/// </summary>
public class FuzzTests
{
    private static readonly QueryEngine Engine = TestEngine.Create();
    private static readonly string[] Fields = ["id", "number", "status", "total", "createdAt", "customer.name", "customer.country", "customer.id",
        "tenantId", "TenantId", "isDeleted", "x", "", ".", "customer.", "customer.customer.name", "name]; DROP TABLE Users;--", "' OR 1=1--",
        "x UNION SELECT ...", "dbo.Users", "../../password", "\u0000", "\uD800", "ü€𝄞"];
    private static readonly string[] Good = ["id", "number", "status", "createdAt", "customer.name", "total"];

    private static string Field(Random r) => r.Next(10) < 7 ? Good[r.Next(Good.Length)] : Fields[r.Next(Fields.Length)];

    private static readonly string[] Operators = ["eq", "ne", "gt", "gte", "lt", "lte", "in", "notIn", "between", "notBetween", "contains", "startsWith", "endsWith", "like", "EQ", ""];

    private static JsonNode? Value(Random r) => r.Next(14) switch
    {
        0 => null,
        1 => r.Next(2) == 0,
        2 => r.NextInt64(long.MinValue, long.MaxValue),
        3 => JsonValue.Create(r.NextDouble() * 1e300),
        4 => "open",
        5 => "2026-01-01T00:00:00Z",
        6 => "2026-13-45T99:99:99Z",
        7 => Guid.NewGuid().ToString(),
        8 => new string('a', r.Next(0, 5000)),
        9 => new JsonArray([.. Enumerable.Range(0, r.Next(0, 400)).Select(i => (JsonNode)JsonValue.Create(i.ToString()))]),
        10 => new JsonArray(1, 2),
        11 => new JsonObject { ["field"] = Field(r) },
        12 => "' OR 1=1--",
        _ => Field(r),
    };

    private static JsonNode Filter(Random r, int depth)
    {
        var kind = depth > 12 ? 0 : r.Next(4);
        JsonObject node = kind switch
        {
            0 or 3 => new JsonObject { ["type"] = "comparison", ["field"] = Field(r), ["operator"] = Operators[r.Next(Operators.Length)], ["value"] = Value(r) },
            1 => new JsonObject
            {
                ["type"] = "logical", ["operator"] = r.Next(5) == 0 ? "xor" : r.Next(2) == 0 ? "and" : "or",
                ["children"] = new JsonArray([.. Enumerable.Range(0, r.Next(0, 4)).Select(_ => Filter(r, depth + 1))]),
            },
            _ => new JsonObject { ["type"] = "not", ["operand"] = Filter(r, depth + 1) },
        };
        if (r.Next(20) == 0) node["unexpected"] = 1;
        if (r.Next(25) == 0) node["compareTo"] = new JsonObject { ["field"] = Field(r) };
        if (r.Next(30) == 0) node.Remove("type");
        return node;
    }

    private static byte[] Document(Random r)
    {
        var doc = new JsonObject
        {
            ["select"] = new JsonArray([.. Enumerable.Range(0, r.Next(0, 6)).Select(_ => (JsonNode)Field(r))]),
        };
        if (r.Next(3) > 0) doc["where"] = Filter(r, 1);
        if (r.Next(2) == 0) doc["orderBy"] = new JsonArray([.. Enumerable.Range(0, r.Next(0, 3)).Select(_ =>
            (JsonNode)new JsonObject { ["field"] = Field(r), ["direction"] = r.Next(4) == 0 ? "sideways" : "desc" })]);
        if (r.Next(2) == 0) doc["page"] = new JsonObject { ["first"] = r.Next(-5, 700), [r.Next(2) == 0 ? "offset" : "after"] = r.Next(2) == 0 ? r.Next(-1, 20000) : "zz" };
        if (r.Next(15) == 0) doc["version"] = r.Next(2) == 0 ? "1.0" : "9.9";
        if (r.Next(15) == 0) doc["distinct"] = true;
        var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString());
        return r.Next(8) switch
        {
            0 => bytes[..r.Next(bytes.Length + 1)],                                     // truncated
            1 => [.. bytes.Select(b => r.Next(50) == 0 ? (byte)r.Next(256) : b)],       // malformed bytes / Unicode
            _ => bytes,
        };
    }

    [Fact]
    public async Task Random_documents_yield_plan_or_controlled_error()
    {
        var random = new Random(20261119);
        var context = TestEngine.Context(SeedData.TenantA);
        var (ok, rejected) = (0, 0);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++)
        {
            var body = Document(random);
            try
            {
                var prepared = await Engine.PrepareAsync("orders", body, context, TestContext.Current.CancellationToken);
                // Identifiers come only from metadata: the only literal quote in the SQL is the LIKE escape clause.
                Assert.DoesNotContain("'", prepared.Plan.CommandText.Replace(@"ESCAPE '\'", ""));
                Assert.DoesNotContain("DROP", prepared.Plan.CommandText);
                ok++;
            }
            catch (OpenFetchException) { rejected++; }
            catch (Exception ex) { Assert.Fail($"Case {i} escaped as {ex.GetType().Name}: {ex.Message}\n{Encoding.UTF8.GetString(body)}"); }
        }
        Assert.True(ok > 100, $"generator too hostile: {ok} accepted");
        Assert.True(rejected > 1000);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000)]
    public async Task Deep_nesting_cannot_overflow_the_stack(int depth)
    {
        var json = new StringBuilder("""{"select":["id"],"where":""");
        for (var i = 0; i < depth; i++) json.Append("""{"type":"not","operand":""");
        json.Append("""{"type":"comparison","field":"id","operator":"eq","value":"x"}""").Append('}', depth).Append('}');
        var engine = TestEngine.Create(TestModel.Build(o => { TestModel.Configure(o); o.Limits = o.Limits with { MaximumDocumentBytes = 10_000_000 }; }));
        var ex = await Assert.ThrowsAsync<OpenFetchException>(async () => await engine.PrepareAsync("orders", json.ToString(), TestEngine.Context(SeedData.TenantA)));
        Assert.True(ex.Code is "invalid_json" or "query_too_complex");
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("123456789012345678901234567890")]
    [InlineData("1e-400")]
    [InlineData("-0")]
    public async Task Extreme_numbers_are_rejected_or_exact(string number)
    {
        try
        {
            var q = await Engine.PrepareAsync("orders", $$$"""{"select":["id"],"where":{"type":"comparison","field":"total","operator":"gt","value":{{{number}}}}}""", TestEngine.Context(SeedData.TenantA));
            Assert.IsType<decimal>(q.Plan.Parameters[^2].Value);
        }
        catch (OpenFetchException ex) { Assert.Equal("invalid_value_type", ex.Code); }
    }
}
