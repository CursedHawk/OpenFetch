# OpenFetch

OpenFetch is a read-only relational query protocol expressed as JSON, for ASP.NET Core applications that want to expose selected parts of an EF Core model safely. This repository holds the .NET 10 reference implementation of [OPENFETCH.md](OPENFETCH.md), release scope v0.1, using PostgreSQL.

Clients send a logical query: datasets, fields, filters, ordering and paging. They never send table names, column names, SQL, joins or hints. Nothing is exposed until you opt it in.

```http
POST /query/orders
{
  "select": ["id", "number", "status", "total", "customer.name"],
  "where": { "type": "logical", "operator": "and", "children": [
    { "type": "comparison", "field": "status", "operator": "in", "value": ["open", "processing"] },
    { "type": "comparison", "field": "createdAt", "operator": "gte", "value": "2026-01-01T00:00:00Z" } ] },
  "orderBy": [ { "field": "createdAt", "direction": "desc" } ],
  "page": { "first": 100 }
}
```

```json
{ "data": [ { "id": "c839…", "number": "SO-2026-1042", "status": "open", "total": 1250.50, "customer": { "name": "Acme" } } ],
  "page": { "count": 1, "hasNext": true, "endCursor": "CfDJ8…" },
  "meta": { "dataset": "orders", "modelVersion": "a1b2c3d4e5f60718" } }
```

## Packages

| Package | Purpose |
|---|---|
| `OpenFetch.Abstractions` | Attributes, immutable `QueryModel` descriptors, canonical AST, operators, errors. No dependencies. |
| `OpenFetch.Core` | Parser, resolver/validator, security injection, complexity, cursors, ADO.NET execution, capability/schema documents. No EF Core, ASP.NET Core or provider dependency (enforced by a test). |
| `OpenFetch.Compiler.PostgreSql` | Parameterized PostgreSQL SQL and RLS session setup. |
| `OpenFetch.EntityFrameworkCore` | Reads physical metadata from a `DbContext` model. |
| `OpenFetch.AspNetCore` | `AddOpenFetch<TDbContext>`, endpoints, Problem Details, Data Protection cursors, rate limiting. |
| `OpenFetch.AspNetCore.OpenApi` | OpenAPI 3.1 transformers. |

Packages are published to this project's GitLab NuGet registry (see [CI](#ci-and-packages)).

## Quick start

```csharp
[QueryDataset("orders", Description = "Sales orders", MaximumPageSize = 500)]
public sealed class Order
{
    [QueryColumn("id", Filter = true, Sort = true, Cursor = true)] public Guid Id { get; set; }
    [QueryColumn("status", Filter = true, AllowedOperators = [QueryOperator.Equal, QueryOperator.In])] public OrderStatus Status { get; set; }
    [QueryColumn("total", Filter = true, Sort = true, AuthorizationPolicy = "Orders.FinancialData")] public decimal Total { get; set; }
    [QueryColumn("createdAt", Filter = true, Sort = true, Cursor = true)] public DateTimeOffset CreatedAt { get; set; }
    [QueryHidden] public Guid TenantId { get; set; }
    public bool IsDeleted { get; set; }                       // not attributed: never exposed
    [QueryRelationship("customer", Filter = true, Sort = true)] public Customer Customer { get; set; } = null!;
}

builder.Services.AddScoped<TenantContext>(/* from the authenticated user */);
builder.Services.AddOpenFetch<AppDbContext>(options =>
{
    options.UsePostgreSql();
    options.Security
        .RequireAuthenticatedUser()
        .UseTenantClaim("tenant")              // tenant comes only from the token, never from the query
        .UseDatabaseRowLevelSecurity();        // set_config('openfetch.tenant_id', …, true) per transaction

    options.Dataset<Order>()
        .RequirePolicy("Orders.Read")
        .ApplyRowPolicy<TenantContext>((o, ctx) => o.TenantId == ctx.TenantId && !o.IsDeleted);
});
builder.Services.AddOpenFetchRateLimiting(concurrentQueriesPerPrincipal: 4); // optional
builder.Services.AddOpenApi(o => o.AddOpenFetch());

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapOpenFetch("/query");                     // POST /query/{dataset}, one operation per dataset
app.MapOpenFetchCapabilities("/query/capabilities");
app.MapOpenFetchSchema("/query/schema");        // caller-specific, ETag + private caching
app.MapOpenApi();
```

The model is built and validated once, when the endpoints are mapped. If anything is wrong, startup fails with a list of every error found. Validation covers:

- duplicate or invalid names
- unsupported types
- nullable cursor columns
- invalid operator/type combinations
- relationships to hidden datasets
- collection relationships
- missing primary keys
- unregistered authorization policies
- OpenAPI component-name collisions
- EF query filters with no replacement policy (see below)

## Exposure and precedence

- **Opt-in.** A dataset is exposed only by `[QueryDataset]` or `Dataset<T>().Expose(name)`. A column is exposed only by `[QueryColumn]`, `Column(x => …).Expose()`, or the explicit `options.ExposeColumnsByConvention = true`.
- **Precedence.** Settings resolve in this order:
  1. `Hide()` and `[QueryHidden]`: absolute deny.
  2. Fluent configuration.
  3. Attributes.
  4. Convention.
  5. Secure default: not exposed.
- **Conflicting fluent registrations only narrow.** `false` beats `true`, the smaller page size wins, operator sets intersect, and two different names are a startup error. Authorization policies accumulate: every attribute and fluent policy is required.
- **One policy set per column.** A column's policies gate all of its uses: select, filter, sort, cursor. A field you cannot select can never be used as a Boolean oracle.
- **No disclosure of inaccessible things.** Inaccessible datasets, fields and relationships produce the same errors as unknown ones. The schema endpoint omits them.

## Security model

1. **Authentication and tenant.** Authentication is required when configured (401). The tenant comes only from the configured claim; a caller without one gets 403.
2. **Authorization.** Dataset, column and relationship authorization use ASP.NET Core policies, evaluated once per request.
3. **Row policies.** `ApplyRowPolicy` expressions are translated at startup into trusted filters. They may reference hidden columns, and their values are always SQL parameters. The root policy is AND-ed outside the client's filter, and joined-dataset policies go into the `ON` clause, so clients cannot remove, negate, or escape them. Add custom predicates with `IRowPolicyProvider`.
4. **Database RLS (defense in depth).** Each query runs in one `READ ONLY` transaction on one connection. Session values are set first with `set_config(…, true)`, which is transaction-local and therefore cannot leak through the connection pool; a test alternates tenants on a `Max Pool Size=1` pool. See [samples/postgresql-rls.sql](samples/postgresql-rls.sql) for the policies and a non-`BYPASSRLS` application role.
5. **Identifiers vs. values.** Identifiers come only from trusted metadata and are always quoted. Every client value is a parameter: `IN` becomes `= ANY(@array)`, and `LIKE` wildcards are escaped.
6. **Cursors.** Cursors are protected with ASP.NET Core Data Protection. Each one is bound to the model version, the query shape, the caller/tenant/policy scope, and an expiry.
7. **Limits.** Structural limits, complexity scoring ([docs/complexity-v1.md](docs/complexity-v1.md)), a command timeout, request cancellation, a response-size cap, and an optional per-principal concurrency limit.
8. **Logging.** Logs never contain condition values. SQL text, with parameter names only, is logged only when `options.DiagnosticMode = true`.

### EF Core global query filters

OpenFetch compiles SQL directly, so **EF global query filters (`HasQueryFilter`) are not applied** (spec §8). Exposing an entity that has a query filter fails startup unless you either:

- express the filter as `ApplyRowPolicy(...)` (recommended; ideally backed by database RLS), or
- call `Dataset<T>().AcknowledgeEfQueryFilterNotApplied()` to confirm the filter is intentionally ignored.

## Query language

| | |
|---|---|
| Top level | `version` (`"1.0"`), `select` (required), `where`, `orderBy`, `page`, `distinct`. Unknown or duplicate properties are rejected. |
| Filters | Tagged union: `comparison` / `logical` (`and`, `or`) / `not` |
| Operators | `eq ne gt gte lt lte in notIn between notBetween contains startsWith endsWith`, further limited per column and type |
| Relationships | Dotted paths (`customer.name`) in `select`, `where` and `orderBy`; returned as nested objects (`null` when the related row is absent or hidden). To-one only in v0.1. |
| Values | Native JSON. Dates, GUIDs and enums are strings: see [docs/spec-profile.md](docs/spec-profile.md). |
| Paging | `page.first` plus either `page.after` (cursor) or `page.offset` (bounded). The key is always appended as a tie-breaker, and `endCursor` is returned when every ordering field is cursor-eligible. |
| Errors | Problem Details with stable `code` and JSON Pointer `path` (spec §24). |

Error statuses:

| Status | Meaning |
|---|---|
| 400 | Syntax |
| 401 | Unauthenticated |
| 403 | Forbidden |
| 404 | Unknown dataset |
| 413 | Query document too large |
| 422 | Semantically invalid query |
| 429 | Rate or concurrency limit |
| 500 | Execution failure (details are logged server-side only) |
| 504 | Timeout |

## OpenAPI

`AddOpenApi(o => o.AddOpenFetch())` generates an OpenAPI 3.1 document from the same `QueryModel` used for validation, with one operation per dataset (`queryOrders`).

Each dataset gets these component schemas:

- `OrdersQuery`
- `OrdersSelectableField`, `OrdersSortableField`, `OrdersFilterableField` (enums)
- `OrdersFilter`: a `oneOf` with a discriminator; every branch pins `type` with `const`
- `OrdersComparisonFilter`, `OrdersLogicalFilter`, `OrdersNotFilter`
- `OrdersOrder`
- `OrdersPage`
- `OrdersRow`: a superset of the selectable fields, all optional
- `OrdersResult`

Operations carry an example request and `x-openfetch-*` extensions for capabilities, operators, limits, authorization, complexity, and relationships.

For stable authorization profiles (§18.7), register named documents. Each one contains only what a caller holding exactly those policies can use:

```csharp
builder.Services.AddOpenApi("public",  o => o.AddOpenFetch(profile: []));
builder.Services.AddOpenApi("finance", o => o.AddOpenFetch(profile: ["Orders.FinancialData"]));
// served at /openapi/public.json and /openapi/finance.json
```

The committed contract [openapi/v1.json](openapi/v1.json) is checked by the test suite against the generated document and a breaking-change detector. CI also generates and compiles a Kiota C# client and an `openapi-typescript` client ([eng/sdk-smoke](eng/sdk-smoke)).

## Development

```sh
dotnet test                                    # needs Docker (Testcontainers PostgreSQL 17)
OPENFETCH_UPDATE_SNAPSHOTS=1 dotnet test       # rewrite conformance/ and openapi/ snapshots after an intended change
eng/sdk-smoke/run.sh                           # generate + compile C#/TS clients (needs Node)
```

The test suite covers:

- **Conformance corpus** ([conformance/](conformance)): valid cases with AST, SQL plan and live-result snapshots, and invalid cases with their expected `code` and `path`.
- **Injection:** hostile identifiers and values.
- **Tenant isolation:** application policies, RLS alone, and pool reuse.
- **Cursors:** tampering, replay, shape change, model change, expiry.
- **HTTP end-to-end.**
- **Seeded fuzzing:** 5,000 documents, deep nesting, malformed UTF-8.
- **OpenAPI contract.**

## CI and packages

`.gitlab-ci.yml` stages:

| Stage | What it does |
|---|---|
| build | Build the solution |
| test | Run the tests, with Docker-in-Docker for Testcontainers |
| contract | SDK smoke builds (C# and TypeScript clients) |
| pack | Package versions are `0.1.0-ci.<pipeline>` on branches and the tag version on `vX.Y.Z` tags |
| publish | Push to the GitLab NuGet registry: automatically on tags, as a manual job on `master` |

To consume the packages:

```sh
dotnet nuget add source "https://gitlab.highgeek.eu/api/v4/projects/5/packages/nuget/index.json" \
  --name openfetch --username <user> --password <token>
```

## Status and known limits (v0.1)

- **Providers:** PostgreSQL only; SQL Server is v0.2.
- **Plan reuse:** SQL text depends only on the query shape, never on values or tenants, so the provider statement cache and the database plan cache reuse plans without a separate OpenFetch cache.
- **Not yet supported:** aggregation, grouping, collection relationships, and inheritance or owned-type datasets. They are rejected at startup or with `unsupported_provider_feature`.
- **Response buffering:** responses are buffered up to `MaximumResponseBytes` (4 MiB by default) before they are sent, so a limit breach still returns a clean error status.
- **Rate limiting:** `AddOpenFetchRateLimiting` limits concurrency only. To limit request rate, attach your own policy to the builder that `MapOpenFetch` returns.
