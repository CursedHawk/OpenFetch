using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenFetch.Certification;

/// <summary>The two callers of the reference sample: tenant A (with the Orders.FinancialData policy) and tenant B (without it).</summary>
public enum Caller { TenantA, TenantB }

public sealed record CertificationResponse(int Status, JsonNode? Body);

/// <summary>Anything that can answer OpenFetch queries for the reference sample: an HTTP server, or an in-process host.</summary>
public interface ICertificationTarget
{
    Task<CertificationResponse> QueryAsync(string path, string body, Caller caller, CancellationToken cancellationToken);
}

public sealed record CaseResult(string Name, string Category, bool Passed, string? Message = null);

public sealed class CertificationReport(IReadOnlyList<CaseResult> results)
{
    public IReadOnlyList<CaseResult> Results { get; } = results;
    public bool Passed => Results.All(r => r.Passed);

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var group in Results.GroupBy(r => r.Category))
            sb.AppendLine($"{group.Key}: {group.Count(r => r.Passed)}/{group.Count()} passed");
        foreach (var failure in Results.Where(r => !r.Passed)) sb.AppendLine($"  FAIL {failure.Category}/{failure.Name}: {failure.Message}");
        sb.Append(Passed ? "CERTIFIED" : "NOT CERTIFIED");
        return sb.ToString();
    }
}

/// <summary>
/// Compatibility and security certification (spec §29, §30.3). Black-box: only the HTTP contract and the reference sample
/// (docs/certification.md) are assumed, so implementations in any language can be certified.
/// </summary>
public static class CertificationSuite
{
    private static readonly Assembly Self = typeof(CertificationSuite).Assembly;

    public static async Task<CertificationReport> RunAsync(ICertificationTarget target, CancellationToken ct = default)
    {
        var results = new List<CaseResult>();
        foreach (var name in Names("valid/")) results.Add(await Valid(target, name, ct));
        var expected = JsonNode.Parse(Resource("invalid/expected.json"))!.AsObject();
        foreach (var name in Names("invalid/").Where(n => n != "expected")) results.Add(await Invalid(target, name, expected[name]!, ct));
        foreach (var (name, check) in SecurityChecks) results.Add(await Run(name, "security", () => check(target, ct)));
        return new CertificationReport(results);
    }

    private static IEnumerable<string> Names(string folder) =>
        Self.GetManifestResourceNames().Where(r => r.StartsWith(folder, StringComparison.Ordinal))
            .Select(r => r[folder.Length..^".json".Length]).Order(StringComparer.Ordinal);

    private static string Resource(string name)
    {
        using var stream = Self.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");
        return new StreamReader(stream).ReadToEnd();
    }

    private static string Path(string name) => "/" + name[..name.IndexOf('.')];

    private static async Task<CaseResult> Run(string name, string category, Func<Task<string?>> check)
    {
        try { return await check() is { } failure ? new(name, category, false, failure) : new(name, category, true); }
        catch (Exception ex) { return new(name, category, false, ex.GetType().Name + ": " + ex.Message); }
    }

    private static Task<CaseResult> Valid(ICertificationTarget target, string name, CancellationToken ct) => Run(name, "conformance", async () =>
    {
        var response = await target.QueryAsync(Path(name), Resource($"valid/{name}.json"), Caller.TenantA, ct);
        if (response.Status != 200) return $"status {response.Status}: {response.Body?.ToJsonString()}";
        var actual = Normalize(response.Body!);
        var expected = Normalize(JsonNode.Parse(Resource($"expected-results/{name}.json"))!);
        return JsonNode.DeepEquals(actual, expected) ? null : $"result differs:\n  expected {expected.ToJsonString()}\n  actual   {actual.ToJsonString()}";
    });

    private static Task<CaseResult> Invalid(ICertificationTarget target, string name, JsonNode expected, CancellationToken ct) => Run(name, "conformance", async () =>
    {
        var response = await target.QueryAsync(Path(name), Resource($"invalid/{name}.json"), Caller.TenantA, ct);
        var (code, path) = ((string?)response.Body?["code"], (string?)response.Body?["path"]);
        return response.Status >= 400 && code == (string?)expected["code"] && path == (string?)expected["path"]
            ? null : $"expected {expected["code"]} at {expected["path"]}, got {response.Status} {code} at {path}";
    });

    /// <summary>Masks run-specific values and compares numbers by value (900.5 == 900.50).</summary>
    private static JsonNode Normalize(JsonNode node)
    {
        var copy = node.DeepClone();
        if (copy["page"]?["endCursor"] is not null) copy["page"]!["endCursor"] = "CURSOR";
        if (copy["meta"] is JsonObject meta) meta["modelVersion"] = "MODEL";
        return Numbers(copy);
    }

    private static JsonNode Numbers(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Numbers(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(v => v is null ? null : Numbers(v))]),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(v.GetValue<decimal>()),
        _ => node.DeepClone(),
    };

    // ---- Security checks (§16, §20.1, §24, §29.3) ----

    private static readonly (string Name, Func<ICertificationTarget, CancellationToken, Task<string?>> Check)[] SecurityChecks =
    [
        ("isolation-select", async (t, ct) => OnlyTenantB(await Data(t, "/orders", """{"select":["number"],"page":{"first":500}}""", Caller.TenantB, ct), "number", "SO-B")),
        ("isolation-filter-probe", async (t, ct) => Empty(await Data(t, "/orders", """{"select":["id"],"where":{"type":"comparison","field":"number","operator":"eq","value":"SO-A-0001"}}""", Caller.TenantB, ct))),
        ("isolation-relationship", async (t, ct) =>
            (await Data(t, "/orders", """{"select":["customer.name"],"page":{"first":500}}""", Caller.TenantB, ct)).Any(r => ((string?)r?["customer"]?["name"])?.StartsWith("Customer A") == true)
                ? "a tenant A customer was reachable through a relationship" : null),
        ("isolation-aggregate", async (t, ct) =>
            (long)(await Data(t, "/orders", """{"aggregates":[{"function":"count","alias":"n"}]}""", Caller.TenantB, ct))[0]!["n"]! == 9 ? null : "count includes rows outside tenant B (or soft-deleted rows)"),
        ("isolation-collection", async (t, ct) =>
        {
            var rows = await Data(t, "/customers", """{"select":["id"],"expand":[{"relationship":"orders","select":["number"],"first":50}]}""", Caller.TenantB, ct);
            return OnlyTenantB([.. rows.SelectMany(r => r!["orders"]!.AsArray()).Select(n => n?.DeepClone())], "number", "SO-B");
        }),
        ("isolation-any", async (t, ct) => Empty(await Data(t, "/customers",
            """{"select":["id"],"where":{"type":"any","relationship":"orders","where":{"type":"comparison","field":"number","operator":"startsWith","value":"SO-A"}}}""", Caller.TenantB, ct))),
        ("cursor-cross-tenant-replay", async (t, ct) =>
        {
            var cursor = await EndCursor(t, Caller.TenantA, ct);
            return await Code(t, "/orders", $$$"""{"select":["id"],"page":{"first":2,"after":"{{{cursor}}}"}}""", Caller.TenantB, ct) is "cursor_scope_mismatch" or "invalid_cursor"
                ? null : "a tenant A cursor was accepted for tenant B";
        }),
        ("cursor-tamper", async (t, ct) =>
        {
            var cursor = await EndCursor(t, Caller.TenantA, ct);
            var tampered = cursor[..^3] + (cursor[^3] == 'A' ? 'B' : 'A') + cursor[^2..];
            return await Code(t, "/orders", $$$"""{"select":["id"],"page":{"first":2,"after":"{{{tampered}}}"}}""", Caller.TenantA, ct) == "invalid_cursor" ? null : "a tampered cursor was not rejected";
        }),
        ("cursor-shape-change", async (t, ct) =>
        {
            var cursor = await EndCursor(t, Caller.TenantA, ct);
            return await Code(t, "/orders", $$$"""{"select":["id"],"where":{"type":"comparison","field":"number","operator":"ne","value":"x"},"page":{"first":2,"after":"{{{cursor}}}"}}""", Caller.TenantA, ct)
                == "cursor_scope_mismatch" ? null : "a cursor was accepted for a different query shape";
        }),
        ("unknown-dataset", async (t, ct) => await Code(t, "/doesNotExist", """{"select":["id"]}""", Caller.TenantA, ct) == "unknown_dataset" ? null : "unknown dataset not reported as unknown_dataset"),
        ("column-authorization", async (t, ct) =>
            await Code(t, "/orders", """{"select":["id"],"where":{"type":"comparison","field":"total","operator":"gt","value":0}}""", Caller.TenantB, ct) == "unknown_field"
                ? null : "a policy-protected field was usable without the policy"),
        ("error-oracle", async (t, ct) =>
        {
            static string Probe(string id) => $$$"""{"select":["id"],"where":{"type":"comparison","field":"id","operator":"eq","value":"{{{id}}}"}}""";
            var other = await t.QueryAsync("/orders", Probe("aaaaaaaa-1111-0000-0000-000000000001"), Caller.TenantB, ct);
            var none = await t.QueryAsync("/orders", Probe("cccccccc-1111-0000-0000-000000000001"), Caller.TenantB, ct);
            return other.Status == none.Status && JsonNode.DeepEquals(other.Body?["data"], none.Body?["data"]) && JsonNode.DeepEquals(other.Body?["page"], none.Body?["page"])
                ? null : "responses differ for another tenant's id vs a non-existent id";
        }),
        ("injection-values", async (t, ct) => Empty(await Data(t, "/orders",
            """{"select":["id"],"where":{"type":"comparison","field":"number","operator":"eq","value":"' OR 1=1--"}}""", Caller.TenantA, ct))),
    ];

    private static async Task<JsonArray> Data(ICertificationTarget t, string path, string body, Caller caller, CancellationToken ct)
    {
        var r = await t.QueryAsync(path, body, caller, ct);
        return r.Status == 200 ? r.Body!["data"]!.AsArray() : throw new InvalidOperationException($"status {r.Status}: {r.Body?.ToJsonString()}");
    }

    private static async Task<string?> Code(ICertificationTarget t, string path, string body, Caller caller, CancellationToken ct) =>
        (string?)(await t.QueryAsync(path, body, caller, ct)).Body?["code"];

    private static async Task<string> EndCursor(ICertificationTarget t, Caller caller, CancellationToken ct)
    {
        var r = await t.QueryAsync("/orders", """{"select":["id"],"page":{"first":2}}""", caller, ct);
        return (string?)r.Body?["page"]?["endCursor"] ?? throw new InvalidOperationException("no endCursor returned");
    }

    private static string? Empty(JsonArray rows) => rows.Count == 0 ? null : $"{rows.Count} rows leaked";

    private static string? OnlyTenantB(JsonArray rows, string field, string prefix) =>
        rows.Count == 0 ? "no rows returned" : rows.FirstOrDefault(r => !((string?)r?[field])!.StartsWith(prefix, StringComparison.Ordinal)) is { } leak ? $"leaked {leak.ToJsonString()}" : null;
}

/// <summary>Certifies a running server: POST {prefix}{path} with per-caller authentication headers.</summary>
public sealed class HttpCertificationTarget(HttpClient client, IReadOnlyDictionary<Caller, Action<HttpRequestHeaders>> authenticate, string prefix = "/query") : ICertificationTarget
{
    public async Task<CertificationResponse> QueryAsync(string path, string body, Caller caller, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix.TrimEnd('/') + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        authenticate[caller](request.Headers);
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new CertificationResponse((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
