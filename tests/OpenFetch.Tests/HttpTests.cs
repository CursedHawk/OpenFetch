using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFetch.AspNetCore;
using OpenFetch.AspNetCore.OpenApi;
using OpenFetch.Compiler.PostgreSql;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>Sample host wiring as an application would (§27): auth, tenant claim, app row policies + database RLS.</summary>
public static class TestHost
{
    public static async Task<WebApplication> StartAsync(string connectionString, Action<OpenFetchOptions>? configure = null,
        Action<WebApplicationBuilder>? services = null, Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("Orders.FinancialData", p => p.RequireRole("finance"));
            o.AddPolicy("Orders.Read", p => p.RequireRole("reader"));
        });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped(sp => new TenantContext
        {
            TenantId = Guid.Parse(sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.User.FindFirst("tenant")!.Value),
        });
        builder.Services.AddOpenFetch<AppDbContext>(o =>
        {
            o.UsePostgreSql();
            o.Security.RequireAuthenticatedUser().UseTenantClaim("tenant").UseDatabaseRowLevelSecurity();
            TestModel.Configure(o);
            configure?.Invoke(o);
        });
        builder.Services.AddOpenApi(o => o.AddOpenFetch());
        services?.Invoke(builder);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenFetch();
        app.MapOpenFetchCapabilities();
        app.MapOpenFetchSchema();
        app.MapOpenApi();
        map?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    public static HttpClient Client(this WebApplication app, string? user = "alice", Guid? tenant = null, params string[] roles)
    {
        var client = app.GetTestClient();
        if (user is not null) client.DefaultRequestHeaders.Add("X-User", user);
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant.ToString());
        if (roles.Length > 0) client.DefaultRequestHeaders.Add("X-Roles", string.Join(',', roles));
        return client;
    }

    public static Task<HttpResponseMessage> Query(this HttpClient client, string dataset, string json) =>
        client.PostAsync("/query/" + dataset, new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
}

public sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-User"].ToString() is not { Length: > 0 } user) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Name, user) };
        if (Request.Headers["X-Tenant"].ToString() is { Length: > 0 } tenant) claims.Add(new("tenant", tenant));
        claims.AddRange(Request.Headers["X-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

[Collection("postgres")]
public class HttpTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private const string Reference = """
        {
          "version": "1.0",
          "select": ["id", "number", "status", "total", "createdAt"],
          "where": {
            "type": "logical", "operator": "and",
            "children": [
              { "type": "comparison", "field": "status", "operator": "in", "value": ["open", "processing"] },
              { "type": "comparison", "field": "createdAt", "operator": "gte", "value": "2026-01-01T00:00:00Z" }
            ]
          },
          "orderBy": [ { "field": "createdAt", "direction": "desc" }, { "field": "id", "direction": "asc" } ],
          "page": { "first": 2 }
        }
        """;

    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!;

    [Fact]
    public async Task Reference_request_returns_spec_envelope_and_pages_with_cursor()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString());
        var client = app.Client(tenant: SeedData.TenantA, roles: "finance");

        var response = await client.Query("orders", Reference);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await Json(response);
        Assert.Equal(["id", "number", "status", "total", "createdAt"], body["data"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal("SO-A-0009", (string)body["data"]![0]!["number"]!);
        Assert.Equal(900.50m, (decimal)body["data"]![0]!["total"]!);
        Assert.Equal("2026-01-09T12:00:00Z", (string)body["data"]![0]!["createdAt"]!);
        Assert.True((bool)body["page"]!["hasNext"]!);
        Assert.Equal("orders", (string)body["meta"]!["dataset"]!);

        var next = JsonNode.Parse(Reference)!;
        next["page"]!["after"] = (string)body["page"]!["endCursor"]!;
        var page2 = await Json(await client.Query("orders", next.ToJsonString()));
        Assert.Equal(["SO-A-0005", "SO-A-0004"], page2["data"]!.AsArray().Select(r => (string)r!["number"]!));

        // The same cursor replayed by another tenant is rejected.
        var other = app.Client("bob", SeedData.TenantB, "finance");
        var replay = await other.Query("orders", next.ToJsonString());
        Assert.Equal("cursor_scope_mismatch", (string)(await Json(replay))["code"]!);
    }

    [Fact]
    public async Task Column_policy_hides_field_and_problem_details_are_returned()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString());
        var response = await app.Client(tenant: SeedData.TenantA).Query("orders", """{"select":["id","total"]}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await Json(response);
        Assert.Equal("unknown_field", (string)problem["code"]!);
        Assert.Equal("/select/1", (string)problem["path"]!);
        Assert.Equal("https://openfetch.dev/problems/unknown-field", (string)problem["type"]!);
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized, "unauthenticated")]
    [InlineData("alice", null, HttpStatusCode.Forbidden, "forbidden")]
    public async Task Authentication_and_tenant_are_required(string? user, string? tenant, HttpStatusCode status, string code)
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString());
        var response = await app.Client(user, tenant is null ? null : Guid.Parse(tenant)).Query("orders", """{"select":["id"]}""");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (string)(await Json(response))["code"]!);
    }

    [Theory]
    [InlineData("{", HttpStatusCode.BadRequest, "invalid_json")]
    [InlineData("""{"select":["id"],"page":{"first":1000}}""", HttpStatusCode.UnprocessableEntity, "page_size_exceeded")]
    [InlineData("""{"select":["id"],"page":{"after":"garbage"}}""", HttpStatusCode.UnprocessableEntity, "invalid_cursor")]
    public async Task Status_codes_follow_error_mapping(string json, HttpStatusCode status, string code)
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString());
        var response = await app.Client(tenant: SeedData.TenantA).Query("orders", json);
        Assert.Equal((status, code), (response.StatusCode, (string)(await Json(response))["code"]!));
    }

    [Fact]
    public async Task Oversized_document_is_413()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString(), o => o.Limits = o.Limits with { MaximumDocumentBytes = 100 });
        var response = await app.Client(tenant: SeedData.TenantA).Query("orders", """{"select":["id"],"where":null}""" + new string(' ', 200));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Denied_dataset_response_is_identical_to_unknown_dataset()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString(), o => o.Dataset<Order>().RequirePolicy("Orders.Read"));
        var client = app.Client(tenant: SeedData.TenantA);
        static async Task<string> Body(HttpResponseMessage r, string name)
        {
            var node = JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!.AsObject();
            node.Remove("traceId");
            return $"{(int)r.StatusCode} {node.ToJsonString().Replace(name, "X")}";
        }
        var denied = await Body(await client.Query("orders", """{"select":["id"]}"""), "orders");
        var unknown = await Body(await client.Query("ordersz", """{"select":["id"]}"""), "ordersz");
        Assert.Equal(unknown, denied);
        Assert.StartsWith("404 ", denied);
    }

    [Fact]
    public async Task Capabilities_and_caller_specific_schema_with_etag()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString());
        var caps = await Json(await app.Client(tenant: SeedData.TenantA).GetAsync("/query/capabilities", Ct));
        Assert.Equal(500, (int)caps["limits"]!["maxPageSize"]!);
        Assert.Equal("postgresql", (string)caps["provider"]!["name"]!);

        var plain = app.Client(tenant: SeedData.TenantA);
        var response = await plain.GetAsync("/query/schema", Ct);
        var schema = await Json(response);
        var orderFields = schema["entities"]!.AsArray().Single(e => (string)e!["name"]! == "orders")!["fields"]!.AsArray().Select(f => (string)f!["name"]!);
        Assert.DoesNotContain("total", orderFields);
        Assert.DoesNotContain("tenantId", orderFields);
        Assert.Contains("private", response.Headers.CacheControl!.ToString());

        var finance = await Json(await app.Client(tenant: SeedData.TenantA, roles: "finance").GetAsync("/query/schema", Ct));
        Assert.Contains("total", finance["entities"]![0]!["fields"]!.AsArray().Concat(finance["entities"]![1]!["fields"]!.AsArray()).Select(f => (string)f!["name"]!));

        var etag = response.Headers.ETag!.Tag;
        plain.DefaultRequestHeaders.Add("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await plain.GetAsync("/query/schema", Ct)).StatusCode);
    }

    [Fact]
    public async Task Named_projection_returns_fixed_fields()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString(), o => o.Dataset<Order>().Projection("financial", "id", "total"));
        var client = app.Client(tenant: SeedData.TenantA);
        Task<HttpResponseMessage> Post(string path, string json) =>
            client.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Json(await Post("/query/orders/summary", """{"orderBy":[{"field":"number"}],"page":{"first":2}}"""));
        Assert.Equal(["id", "number", "status", "customer"], body["data"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal("Customer A2", (string)body["data"]![0]!["customer"]!["name"]!);

        var withSelect = await Post("/query/orders/summary", """{"select":["id"]}""");
        Assert.Equal((HttpStatusCode.BadRequest, "/select"), (withSelect.StatusCode, (string)(await Json(withSelect))["path"]!));
        Assert.Equal("unknown_dataset", (string)(await Json(await Post("/query/orders/nope", "{}")))["code"]!);
        Assert.Equal("unknown_field", (string)(await Json(await Post("/query/orders/financial", "{}")))["code"]!); // lacks finance role
        Assert.Equal(HttpStatusCode.OK, (await app.Client(tenant: SeedData.TenantA, roles: "finance")
            .PostAsync("/query/orders/financial", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Invalid_projection_fails_startup()
    {
        var ex = await Assert.ThrowsAsync<OpenFetchConfigurationException>(() =>
            TestHost.StartAsync(pg.AppConnectionString(), o => o.Dataset<Order>().Projection("bad", "id", "tenantId")));
        Assert.Contains(ex.Errors, e => e.Contains("projection 'bad'"));
    }

    [Fact]
    public async Task Missing_authorization_policy_fails_startup()
    {
        var ex = await Assert.ThrowsAsync<OpenFetchConfigurationException>(() =>
            TestHost.StartAsync(pg.AppConnectionString(), o => o.Dataset<Customer>().RequirePolicy("Nope")));
        Assert.Contains(ex.Errors, e => e.Contains("'Nope'"));
    }

    [Fact]
    public async Task Logs_never_contain_values_and_sql_only_in_diagnostic_mode()
    {
        foreach (var diagnostic in new[] { false, true })
        {
            var sink = new LogSink();
            await using var app = await TestHost.StartAsync(pg.AppConnectionString(), o => o.DiagnosticMode = diagnostic,
                b => { b.Logging.AddProvider(sink); b.Logging.SetMinimumLevel(LogLevel.Debug); });
            await app.Client(tenant: SeedData.TenantA).Query("orders", """{"select":["id"],"where":{"type":"comparison","field":"number","operator":"eq","value":"SECRET-VALUE-42"}}""");
            await app.Client(tenant: SeedData.TenantA).Query("orders", """{"select":["id"],"where":{"type":"comparison","field":"total","operator":"eq","value":"SECRET-VALUE-43"}}""");
            Assert.DoesNotContain(sink.Messages, m => m.Contains("SECRET-VALUE") || m.Contains(SeedData.TenantA.ToString()));
            Assert.Equal(diagnostic, sink.Messages.Any(m => m.Contains("SELECT ")));
            Assert.Contains(sink.Messages, m => m.Contains("shape="));
        }
    }

    [Fact]
    public async Task Concurrency_limit_rejects_with_429()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(pg.ConnectionString));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("Orders.FinancialData", p => p.RequireRole("finance")));
        builder.Services.AddOpenFetch<AppDbContext>(o => { o.UsePostgreSql(); o.Dataset<Order>().ApplyRowPolicy(x => !x.IsDeleted); });
        builder.Services.AddOpenFetchRateLimiting(concurrentQueriesPerPrincipal: 1);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseRateLimiter();
        var first = 0;
        app.Use(async (ctx, next) => { if (Interlocked.Exchange(ref first, 1) == 0) { entered.SetResult(); await gate.Task; } await next(); }); // holds the only permit
        app.MapOpenFetch();
        await app.StartAsync(Ct);

        var client = app.Client(tenant: SeedData.TenantA);
        var held = client.Query("orders", """{"select":["id"]}""");
        await entered.Task.WaitAsync(Ct);
        var rejected = await client.Query("orders", """{"select":["id"]}""");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("rate_limited", (string)(await Json(rejected))["code"]!);
        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await held).StatusCode);
    }

    private sealed class LogSink : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Messages = [];
        public ILogger CreateLogger(string category) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(LogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) =>
                sink.Messages.Add(formatter(state, ex) + ex);
        }
    }
}
