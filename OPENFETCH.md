# OpenFetch Specification

**Status:** Draft 0.1  
**Language version:** 1.0-draft  
**Document type:** Protocol and reference architecture specification

## 1. Abstract

OpenFetch is an open, read-only, relational query protocol expressed as JSON. It is designed for ASP.NET Core applications that expose selected parts of an Entity Framework Core model through a secure, capability-aware API.

OpenFetch is not a JSON serialization of FetchXML. It defines an independent logical query language with native JSON values, an immutable query model, explicit dataset and column exposure, application row policies, optional database-native row-level security, provider-specific parameterized SQL compilation, capability discovery, and first-class OpenAPI integration.

The public query model is logical. Clients never provide physical table names, column names, schemas, SQL fragments, join predicates, database functions, collations, or query hints.

## 2. Goals

OpenFetch MUST:

1. Be safe by default and deny exposure unless explicitly enabled.
2. Allow applications to expose datasets, columns, and relationships with .NET attributes.
3. Allow fluent configuration to override or extend attribute metadata.
4. Discover eligible entities and property metadata from an EF Core `DbContext`.
5. Separate the public logical model from the physical database model.
6. Preserve native JSON scalar types.
7. Parse requests into a provider-neutral canonical AST.
8. Validate queries against dataset, column, relationship, operator, and complexity capabilities.
9. Support entity-, field-, operation-, and row-level authorization.
10. Support database-native row-level security as defense in depth.
11. Compile resolved queries into provider-specific parameterized SQL.
12. Expose machine-readable capabilities and schema metadata.
13. Generate an OpenAPI contract aware of datasets, field capabilities, operators, limits, security, and result types.
14. Support deterministic query-shape caching.
15. Provide conformance, injection, isolation, and fuzz-testing suites.

## 3. Non-goals

Version 1.0 does not aim to provide:

- A replacement for database authorization.
- Automatic exposure of all EF Core entities and properties.
- An abstraction that guarantees identical text-search or collation behavior across all database providers.

Mutations, if added later, MUST be a separate protocol and package.

## 4. Terminology

- **Dataset:** A logical, queryable public resource backed by an EF Core entity, SQL view, or registered query source.
- **Column:** A logical public field exposed by a dataset.
- **Relationship:** A registered logical association between datasets.
- **Query model:** The immutable, authoritative metadata model created during application initialization.
- **Syntax tree:** A representation that closely mirrors the incoming JSON document.
- **Resolved query:** A query in which every dataset, field, operator, and relationship points to trusted metadata.
- **Secured query:** A resolved query after authorization and mandatory policy injection.
- **Query plan:** A normalized provider-neutral representation suitable for caching and compilation.
- **Compiled command:** Provider-specific SQL plus typed parameter definitions and result metadata.
- **Capability:** A permitted operation such as selecting, filtering, sorting, grouping, aggregating, or expanding.
- **Row policy:** A trusted server-generated predicate added to every applicable query.

The key words MUST, MUST NOT, REQUIRED, SHOULD, SHOULD NOT, and MAY are normative requirement levels.

## 5. Architecture

The required processing pipeline is:

```text
JSON request
  -> JSON and syntax validation
  -> canonical AST
  -> logical-name resolution
  -> capability validation
  -> authorization
  -> mandatory row-policy injection
  -> complexity validation
  -> query planning and shape-key generation
  -> provider-specific SQL compilation
  -> database security-context setup
  -> parameterized execution
  -> streamed JSON response
```

The following outputs MUST be generated from the same finalized `QueryModel`:

- Runtime validation rules.
- Authorization metadata.
- SQL compiler metadata.
- `/query/capabilities`.
- `/query/schema`.
- OpenAPI operations and component schemas.
- SDK-generation contracts.

Components MUST NOT independently rediscover EF metadata after initialization.

## 6. Package model

A reference .NET implementation SHOULD use this package layout:

```text
OpenFetch.Abstractions
OpenFetch.Attributes
OpenFetch.Metadata
OpenFetch.Parser
OpenFetch.Validation
OpenFetch.Security
OpenFetch.Planning
OpenFetch.Compiler.Abstractions
OpenFetch.Compiler.SqlServer
OpenFetch.Compiler.PostgreSql
OpenFetch.Execution.AdoNet
OpenFetch.EntityFrameworkCore
OpenFetch.AspNetCore
OpenFetch.AspNetCore.OpenApi
OpenFetch.Specification
OpenFetch.ConformanceTests
```

The core protocol MUST NOT depend on EF Core, ASP.NET Core, or a specific SQL provider.

## 7. Dataset and column declaration

### 7.1 Secure default

EF Core discovery MUST NOT expose an entity or property by itself. A dataset MUST be opted in explicitly. A column MUST be opted in explicitly or enabled by an explicitly configured exposure convention.

Automatic discovery means automatic mapping of eligible metadata, not automatic public exposure.

### 7.2 Dataset attribute

```csharp
[QueryDataset(
    "orders",
    Description = "Queryable sales orders",
    DefaultPageSize = 100,
    MaximumPageSize = 500,
    AuthorizationPolicy = "Orders.Read")]
public sealed class Order
{
}
```

A dataset attribute SHOULD support:

- Public name.
- Description.
- Default and maximum page sizes.
- Authorization policy.
- Whether distinct selection is allowed.
- Maximum relationship depth.
- Capability flags.
- Deprecation status and message.

### 7.3 Column attribute

```csharp
[QueryColumn(
    "number",
    Select = true,
    Filter = true,
    Sort = true,
    AllowedOperators = [
        QueryOperator.Equal,
        QueryOperator.NotEqual,
        QueryOperator.Contains,
        QueryOperator.StartsWith
    ])]
public required string Number { get; set; }
```

A column attribute SHOULD support:

- Public name and description.
- Select, filter, sort, group, aggregate, and cursor capabilities.
- Allowed operators.
- Authorization policy.
- Sensitivity classification.
- Deprecation status.
- Public format override.
- Maximum string length or numeric precision metadata.

Example:

```csharp
[QueryDataset("orders", DefaultPageSize = 100, MaximumPageSize = 500)]
public sealed class Order
{
    [QueryColumn("id", Select = true, Filter = true, Sort = true, Cursor = true)]
    public Guid Id { get; set; }

    [QueryColumn("number", Select = true, Filter = true, Sort = true)]
    public required string Number { get; set; }

    [QueryColumn(
        "status",
        Select = true,
        Filter = true,
        Sort = true,
        AllowedOperators = [
            QueryOperator.Equal,
            QueryOperator.NotEqual,
            QueryOperator.In
        ])]
    public OrderStatus Status { get; set; }

    [QueryColumn(
        "total",
        Select = true,
        Filter = true,
        Sort = true,
        Aggregate = true,
        AuthorizationPolicy = "Orders.FinancialData")]
    public decimal Total { get; set; }

    [QueryColumn("createdAt", Select = true, Filter = true, Sort = true, Cursor = true)]
    public DateTimeOffset CreatedAt { get; set; }

    [QueryHidden]
    public Guid TenantId { get; set; }

    [QueryRelationship(
        "customer",
        Select = true,
        Filter = true,
        Expand = true,
        MaximumDepth = 1)]
    public Customer Customer { get; set; } = null!;
}
```

### 7.4 Hidden members

`[QueryHidden]` MUST prevent a member from being selected, filtered, sorted, grouped, aggregated, expanded, or disclosed through schema metadata.

A hidden field MAY still be used internally by a trusted row policy or compiler.

### 7.5 Relationship attribute

Relationships MUST be registered through trusted metadata. Clients MUST NOT submit physical key columns.

```csharp
[QueryRelationship(
    "customer",
    Expand = true,
    Select = true,
    Filter = true,
    MaximumDepth = 1,
    AuthorizationPolicy = "Customers.Read")]
public Customer Customer { get; set; } = null!;
```

A relationship descriptor MUST contain trusted source entity, target entity, cardinality, navigation or join metadata, approved join type, capability flags, maximum depth, and optional authorization policy.

### 7.6 Attributes and fluent configuration

Attributes define static metadata. Fluent configuration MUST be available for contextual and advanced settings.

```csharp
services.AddOpenFetch<AppDbContext>(options =>
{
    options.UseSqlServer();

    options.Security
        .RequireAuthenticatedUser()
        .UseDatabaseRowLevelSecurity()
        .MaximumComplexity(150);

    options.Dataset<Order>()
        .RequirePolicy("Orders.Read")
        .ApplyRowPolicy<OrderQueryContext>(
            (order, context) => order.TenantId == context.TenantId);

    options.Dataset<Order>()
        .Column(x => x.Total)
        .RequirePolicy("Orders.FinancialData");
});
```

Precedence SHOULD be:

1. Security-denying fluent configuration.
2. Explicit fluent configuration.
3. Attributes.
4. Conventions.
5. Secure defaults.

Configuration MUST NOT broaden exposure accidentally when multiple registrations conflict. A deny decision SHOULD win unless a documented explicit override API is used.

## 8. EF Core metadata discovery

The EF Core adapter SHOULD inspect `DbContext.Model.GetEntityTypes()` and resolve:

- CLR types.
- Tables, views, and schemas.
- Properties and physical column names.
- Primary and alternate keys.
- Foreign keys and navigations.
- Nullability.
- Precision and scale.
- Maximum lengths.
- Value converters.
- Enum mappings.
- Owned types.
- Shadow properties.
- Inheritance mapping.
- Query filters.
- Index metadata where available.

The finalized public model MUST use logical names. Physical identifiers MUST remain internal.

Direct SQL compilation bypasses EF global query filters. Therefore, an implementation MUST NOT claim to preserve EF query filters unless it translates them correctly. The recommended design is explicit OpenFetch row policies plus database-native RLS.

## 9. Authoritative metadata model

```csharp
public sealed record QueryModel(
    IReadOnlyDictionary<string, DatasetDescriptor> Datasets,
    string Version);

public sealed record DatasetDescriptor(
    string PublicName,
    Type ClrType,
    string Description,
    int DefaultPageSize,
    int MaximumPageSize,
    IReadOnlyDictionary<string, ColumnDescriptor> Columns,
    IReadOnlyDictionary<string, RelationshipDescriptor> Relationships,
    string? AuthorizationPolicy);

public sealed record ColumnDescriptor(
    string PublicName,
    Type ClrType,
    bool Nullable,
    bool Selectable,
    bool Filterable,
    bool Sortable,
    bool Groupable,
    bool Aggregatable,
    bool CursorEligible,
    IReadOnlySet<QueryOperator> Operators,
    string? AuthorizationPolicy);
```

Descriptors MUST be immutable after startup. A model version or structural hash MUST change whenever externally observable query behavior changes.

## 10. HTTP API

A conforming ASP.NET Core integration SHOULD expose:

```http
POST /query/{dataset}
GET  /query/capabilities
GET  /query/schema
GET  /openapi/v1.json
```

One query operation per dataset is preferred because it enables dataset-specific OpenAPI schemas and typed SDK operations.

A generic endpoint MAY be offered additionally:

```http
POST /query
```

The generic endpoint MUST NOT be the only documented interface when first-class OpenAPI SDK generation is a goal.

## 11. Query document

Example:

```json
{
  "version": "1.0",
  "select": ["id", "number", "status", "total"],
  "where": {
    "type": "logical",
    "operator": "and",
    "children": [
      {
        "type": "comparison",
        "field": "status",
        "operator": "in",
        "value": ["open", "processing"]
      },
      {
        "type": "comparison",
        "field": "createdAt",
        "operator": "gte",
        "value": "2026-01-01T00:00:00Z"
      }
    ]
  },
  "orderBy": [
    { "field": "createdAt", "direction": "desc" },
    { "field": "id", "direction": "asc" }
  ],
  "page": {
    "first": 100,
    "after": null
  }
}
```

Unknown properties MUST be rejected unless an extension namespace explicitly permits them.

## 12. Native JSON values

OpenFetch MUST preserve native JSON types:

- JSON string.
- JSON number.
- JSON Boolean.
- JSON null.
- JSON array for collection operands.

Dates, times, GUIDs, and other extended scalar types are encoded as strings and interpreted using trusted column metadata. Date and time values MUST use a documented ISO 8601 profile.

A string MUST NOT be interpreted as a field reference. Field-to-field comparison, if supported, MUST use a distinct operand object.

```json
{
  "type": "comparison",
  "field": "salePrice",
  "operator": "lt",
  "compareTo": { "field": "listPrice" }
}
```

## 13. Filter model

Filters MUST use a tagged union.

### 13.1 Comparison filter

```json
{
  "type": "comparison",
  "field": "status",
  "operator": "eq",
  "value": "open"
}
```

### 13.2 Logical filter

```json
{
  "type": "logical",
  "operator": "and",
  "children": [
    { "type": "comparison", "field": "isActive", "operator": "eq", "value": true },
    {
      "type": "logical",
      "operator": "or",
      "children": [
        { "type": "comparison", "field": "country", "operator": "eq", "value": "CZ" },
        { "type": "comparison", "field": "country", "operator": "eq", "value": "SK" }
      ]
    }
  ]
}
```

### 13.3 Negation filter

```json
{
  "type": "not",
  "operand": {
    "type": "comparison",
    "field": "isDeleted",
    "operator": "eq",
    "value": true
  }
}
```

## 14. Core operators

Version 1.0 SHOULD define:

### Comparison

- `eq`
- `ne`
- `gt`
- `gte`
- `lt`
- `lte`

### Collections and ranges

- `in`
- `notIn`
- `between`
- `notBetween`

### Strings

- `contains`
- `startsWith`
- `endsWith`
- `like`, only when provider semantics are explicitly documented

### Nulls

`eq` with `null` MUST compile to `IS NULL`. `ne` with `null` MUST compile to `IS NOT NULL`.

The allowed operator set MUST be further constrained by dataset, column, data type, authorization, and provider capabilities.

Case sensitivity and collation behavior MUST be explicit. Implementations MUST NOT promise provider-neutral case-insensitive behavior without defining how it is achieved.

## 15. Canonical AST

```csharp
public sealed record QueryDocument(
    QueryLanguageVersion Version,
    SourceNode Source,
    ImmutableArray<SelectNode> Select,
    FilterNode? Where,
    ImmutableArray<RelationshipNode> Relationships,
    ImmutableArray<OrderNode> OrderBy,
    PageNode Page,
    bool Distinct);

public abstract record FilterNode;

public sealed record LogicalFilter(
    LogicalOperator Operator,
    ImmutableArray<FilterNode> Children) : FilterNode;

public sealed record NotFilter(FilterNode Child) : FilterNode;

public sealed record ComparisonFilter(
    FieldOperand Left,
    ComparisonOperator Operator,
    QueryOperand Right) : FilterNode;

public abstract record QueryOperand;
public sealed record ConstantOperand(QueryValue Value) : QueryOperand;
public sealed record FieldOperand(string? SourceAlias, string Field) : QueryOperand;
public sealed record ListOperand(ImmutableArray<QueryValue> Values) : QueryOperand;
```

The core AST MUST be provider-neutral and MUST NOT contain raw SQL.

## 16. Security model

### 16.1 Defense in depth

A conforming implementation SHOULD combine:

1. API authentication.
2. Dataset authorization.
3. Per-capability column authorization.
4. Relationship authorization.
5. Trusted application row policies.
6. Query complexity controls.
7. Database-native row-level security.
8. Command timeout and cancellation.
9. Rate and concurrency limits.

### 16.2 Dataset authorization

A caller MUST be authorized before querying a dataset. Inaccessible datasets SHOULD be reported as unknown to avoid metadata disclosure.

### 16.3 Column authorization

Selection, filtering, sorting, grouping, aggregation, and cursor use MUST be authorized separately.

Denying selection while permitting filtering can disclose sensitive data through repeated Boolean queries. Therefore, a protected field MUST NOT automatically become filterable or sortable merely because it is not returned.

### 16.4 Row-policy injection

Mandatory predicates MUST be created by trusted server code and inserted into the secured AST.

```text
Client predicate:
    status = "open"

Server policy:
    tenantId = currentTenant
    AND departmentId IN permittedDepartments

Effective predicate:
    tenantId = @tenant
    AND departmentId IN (...)
    AND status = @status
```

Clients MUST NOT be able to remove, negate, shadow, or override trusted predicates.

```csharp
public interface IRowPolicyProvider
{
    ValueTask<FilterNode?> BuildPolicyAsync(
        QuerySecurityContext context,
        DatasetDescriptor dataset,
        CancellationToken cancellationToken);
}
```

### 16.5 Database-native RLS

Database-native RLS SHOULD be supported as a final enforcement boundary.

For SQL Server, the implementation MAY set trusted values with `sp_set_session_context` and use `SESSION_CONTEXT` from a security predicate.

For PostgreSQL, the implementation MAY set transaction-local settings and reference them from PostgreSQL row policies.

Security context setup and query execution MUST occur on the same physical connection. Session state MUST NOT leak through connection pooling. Implementations MUST test alternating tenants using a deliberately small connection pool.

Database connections used by the API SHOULD NOT use roles that bypass RLS.

### 16.6 Tenant policy

Tenant identity MUST come from trusted authentication or server configuration, never from an ordinary client query value.

### 16.7 Identifier security

SQL parameters protect values, not identifiers. Dataset, table, schema, column, alias, operator, relationship, and function identifiers MUST originate from trusted registered metadata or compiler-generated names.

## 17. Capability model

The capability model MUST describe:

- Supported language versions.
- Provider and compiler version.
- Global features.
- Query limits.
- Operators.
- Dataset capabilities.
- Column capabilities.
- Relationship capabilities.
- Pagination modes.
- Aggregation support.
- Error format.

### 17.1 Capability endpoint

```http
GET /query/capabilities
```

Example:

```json
{
  "language": "openfetch",
  "versions": ["1.0"],
  "features": {
    "relationships": true,
    "aggregates": false,
    "cursorPagination": true,
    "offsetPagination": true,
    "fieldComparison": false
  },
  "limits": {
    "maxPageSize": 500,
    "maxRelationships": 3,
    "maxFilterDepth": 6,
    "maxConditions": 50,
    "maxInValues": 200,
    "maxDocumentBytes": 65536,
    "timeoutSeconds": 10
  },
  "operators": {
    "comparison": ["eq", "ne", "gt", "gte", "lt", "lte"],
    "collections": ["in", "notIn", "between", "notBetween"],
    "strings": ["contains", "startsWith", "endsWith"],
    "logical": ["and", "or", "not"]
  },
  "provider": {
    "name": "sqlserver",
    "compilerVersion": "1"
  }
}
```

### 17.2 Schema endpoint

```http
GET /query/schema
```

The schema endpoint MUST be caller-specific and MUST omit inaccessible datasets, columns, relationships, and operations.

Example:

```json
{
  "modelVersion": "42",
  "entities": [
    {
      "name": "orders",
      "fields": [
        {
          "name": "id",
          "type": "guid",
          "nullable": false,
          "capabilities": ["select", "filter", "sort", "cursor"]
        },
        {
          "name": "total",
          "type": "decimal",
          "nullable": false,
          "precision": 18,
          "scale": 2,
          "capabilities": ["select", "filter", "sort", "aggregate"]
        }
      ],
      "relationships": [
        {
          "name": "customer",
          "target": "customers",
          "cardinality": "one",
          "expandable": true
        }
      ]
    }
  ]
}
```

Responses SHOULD use private caching and an ETag derived from model and policy-profile versions.

## 18. OpenAPI integration

### 18.1 General requirements

OpenAPI MUST be generated from the finalized `QueryModel`. The generator MUST NOT maintain a second independent exposure configuration.

The preferred contract is one operation per dataset:

```http
POST /query/orders
POST /query/customers
```

This enables dataset-specific request and response schemas.

### 18.2 OpenAPI version

Implementations SHOULD emit OpenAPI 3.1 or newer and use JSON Schema-compatible constructs where tooling permits.

### 18.3 Dataset-specific schemas

For each dataset, the generator SHOULD create:

```text
OrdersQuery
OrdersSelectableField
OrdersSortableField
OrdersFilter
OrdersOrder
OrdersPage
OrdersRow
OrdersResult
```

Selectable and sortable fields SHOULD be represented as enums.

```yaml
OrdersSelectableField:
  type: string
  enum:
    - id
    - number
    - status
    - total
    - createdAt
```

### 18.4 Filter polymorphism

Filters SHOULD be represented with `oneOf` and an OpenAPI discriminator.

```yaml
OrdersFilter:
  oneOf:
    - $ref: '#/components/schemas/OrdersComparisonFilter'
    - $ref: '#/components/schemas/OrdersLogicalFilter'
    - $ref: '#/components/schemas/OrdersNotFilter'
  discriminator:
    propertyName: type
    mapping:
      comparison: '#/components/schemas/OrdersComparisonFilter'
      logical: '#/components/schemas/OrdersLogicalFilter'
      not: '#/components/schemas/OrdersNotFilter'
```

Each branch MUST constrain `type` to a single value so validation does not rely only on the discriminator hint.

The generator SHOULD balance precision and document size. For large models, it MAY generate operator-family schemas instead of one schema per field and operator combination. Runtime validation remains authoritative.

### 18.5 Dynamic projection responses

OpenAPI cannot correlate a request's `select` array with exact response properties. Therefore, `OrdersRow` SHOULD be a superset schema with selectable properties optional.

The operation description MUST state that only selected fields, plus documented server-required fields, are returned.

For strongly typed fixed results, implementations MAY support named projections and separate operations:

```http
POST /query/orders/summary
POST /query/orders/financial
```

### 18.6 Vendor extensions

Capabilities that cannot be expressed in standard OpenAPI SHOULD use documented namespaced extensions:

```text
x-openfetch-version
x-openfetch-dataset
x-openfetch-capabilities
x-openfetch-operators
x-openfetch-limits
x-openfetch-relationships
x-openfetch-authorization
x-openfetch-complexity
x-openfetch-projection
x-openfetch-provider
```

Example:

```yaml
/query/orders:
  post:
    operationId: queryOrders
    x-openfetch-dataset: orders
    x-openfetch-capabilities:
      projection: true
      filtering: true
      sorting: true
      cursorPagination: true
      relationships: true
      aggregation: false
    x-openfetch-limits:
      maxPageSize: 500
      maxSelectedFields: 50
      maxConditions: 50
      maxFilterDepth: 6
      maxRelationships: 3
      maxComplexity: 150
```

### 18.7 Static and caller-specific contracts

A static OpenAPI document describes all deployable public capabilities and their authorization requirements. `/query/schema` describes capabilities available to the current caller.

An implementation SHOULD NOT generate an OpenAPI document per individual user. If the application has a small number of stable authorization profiles, it MAY generate separate documents such as:

```text
/openapi/public.json
/openapi/operations.json
/openapi/finance.json
/openapi/admin.json
```

### 18.8 ASP.NET Core integration

A reference implementation SHOULD use document, operation, and schema transformers.

```csharp
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<OpenFetchDocumentTransformer>();
    options.AddOperationTransformer<OpenFetchOperationTransformer>();
    options.AddSchemaTransformer<OpenFetchSchemaTransformer>();
});
```

Responsibilities:

- Document transformer: component schemas, common errors, security schemes, global extensions.
- Operation transformer: dataset-specific request and response references, limits, examples, policies, operation IDs.
- Schema transformer: CLR descriptions, formats, public names, deprecation, and column capability extensions.

Dataset-specific recursive query schemas SHOULD be created at document level where the complete dataset context is available.

### 18.9 SDK compatibility

CI SHOULD generate and compile clients using at least one C# and one TypeScript generator. Recursive `oneOf` support MUST be tested against supported generators.

## 19. SQL compilation

### 19.1 Compiler contract

```csharp
public interface IQueryCompiler
{
    string ProviderName { get; }

    QueryPlan Compile(
        SecuredQuery query,
        CompilationContext context);
}

public sealed record QueryPlan(
    string CommandText,
    ImmutableArray<ParameterDefinition> Parameters,
    ImmutableArray<ResultColumn> ResultColumns,
    PlanDiagnostics Diagnostics);
```

### 19.2 SQL safety requirements

A compiler MUST:

- Resolve all physical identifiers from trusted metadata.
- Generate aliases internally.
- Parameterize every client and policy value.
- Reject unsupported features.
- Emit deterministic result aliases.
- Preserve policy predicates.
- Enforce provider parameter limits.
- Enforce deterministic ordering for cursor pagination.
- Avoid concatenating client strings into SQL.

Example compiled command:

```sql
SELECT TOP (@__limit)
    [t0].[Id] AS [id],
    [t0].[Number] AS [number],
    [t0].[CreatedAt] AS [createdAt],
    [t0].[Total] AS [total],
    [t1].[Name] AS [customer_name]
FROM [sales].[Orders] AS [t0]
LEFT JOIN [crm].[Customers] AS [t1]
    ON [t0].[CustomerId] = [t1].[Id]
WHERE
    [t0].[TenantId] = @p0
    AND [t0].[IsDeleted] = @p1
    AND [t0].[Status] IN (@p2, @p3)
    AND [t0].[CreatedAt] >= @p4
ORDER BY
    [t0].[CreatedAt] DESC,
    [t0].[Id] ASC;
```

Physical identifiers and aliases are trusted. `@p0` and `@p1` are server-policy values. Remaining parameters are client values.

### 19.3 Execution

Dynamic projections SHOULD be executed using `DbConnection`, `DbCommand`, and `DbDataReader`, or a compatible abstraction. Results SHOULD be streamed to `Utf8JsonWriter` where practical.

Execution MUST support:

- Cancellation.
- Command timeout.
- Maximum rows.
- Maximum response bytes.
- Sequential access for large values where appropriate.
- Safe connection and transaction lifetime.
- Database security-context setup.

## 20. Pagination

### 20.1 Cursor pagination

Cursor pagination SHOULD be the preferred scalable mechanism.

```json
{
  "page": {
    "first": 100,
    "after": "opaque-server-generated-cursor"
  }
}
```

Ordering MUST be deterministic. The compiler MUST append a unique tie-breaker, normally the primary key, when needed.

A cursor SHOULD bind:

- Ordering values.
- Query-shape hash.
- Model version.
- Expiration.
- Relevant authorization or policy scope.

Cursor contents MUST be authenticated and SHOULD be opaque. A cursor issued under one tenant or incompatible query shape MUST be rejected in another context.

### 20.2 Offset pagination

Offset pagination MAY be supported for small datasets and dashboards. Implementations SHOULD restrict large offsets and disclose the configured limit through capabilities.

## 21. Aggregation

Aggregation MAY be introduced after the base query protocol is stable. A conforming aggregation extension SHOULD initially support only:

- `count`
- `countDistinct`
- `sum`
- `avg`
- `min`
- `max`

Grouping, aggregate filtering, and ordering MUST use explicitly authorized columns. Aggregate queries MUST still receive all mandatory row-policy predicates and database RLS enforcement.

## 22. Complexity controls

Result limits alone do not prevent expensive scans. Implementations MUST apply structural limits and SHOULD calculate a complexity score.

Recommended defaults:

```text
Maximum document size       65,536 bytes
Default page size           100 rows
Maximum page size           500 rows
Maximum relationships       3
Maximum relationship depth  2
Maximum filter depth        6
Maximum conditions          50
Maximum IN values           200
Maximum selected fields     50
Maximum complexity score    150
Default command timeout     10 seconds
```

Example scoring model:

```text
Root dataset                 1
Selected scalar field        1
Condition                    2
OR group                     5
Relationship expansion      15
Collection relationship     30
Leading-wildcard search     25
Group-by field              15
Aggregate                   10
Distinct                    20
Unindexed filter field      20
Unindexed sort field        30
Offset above configured max reject
```

The exact scoring algorithm MUST be documented and versioned. Capabilities MUST disclose the applicable limits.

Runtime protection SHOULD also include:

- ASP.NET Core rate limiting.
- Per-principal concurrency limits.
- Database timeout.
- Request cancellation.
- Response-byte limit.
- Query telemetry.
- Optional database cost estimation for administrative scenarios.

## 23. Query-shape cache

Implementations SHOULD cache normalized query shapes, not values.

These filters SHOULD share a plan:

```json
{ "type": "comparison", "field": "status", "operator": "eq", "value": "open" }
```

```json
{ "type": "comparison", "field": "status", "operator": "eq", "value": "closed" }
```

A cache key SHOULD include:

- Language version.
- Dataset.
- Selected fields.
- Resolved relationships.
- Filter structure and operand types.
- Ordering.
- Pagination mode.
- Provider and compiler version.
- Model version.
- Policy shape.

It SHOULD NOT contain tenant IDs or ordinary values unless they alter SQL structure.

A cached plan MAY contain SQL templates, parameter definitions, value-extraction delegates, result mappings, and cursor-binding metadata.

## 24. Error model

Errors SHOULD use Problem Details with a stable machine-readable `code` and JSON Pointer `path`.

```json
{
  "type": "https://openfetch.dev/problems/invalid-operator",
  "title": "Invalid query operator",
  "status": 400,
  "code": "invalid_operator",
  "detail": "Operator 'contains' cannot be used with field 'total'.",
  "path": "/where/operator",
  "dataset": "orders",
  "field": "total",
  "allowedOperators": ["eq", "ne", "gt", "gte", "lt", "lte", "between"]
}
```

Recommended codes:

```text
invalid_json
unsupported_version
unknown_dataset
unknown_field
unsupported_operator
invalid_value_type
invalid_relationship
query_too_complex
page_size_exceeded
invalid_cursor
cursor_scope_mismatch
unsupported_provider_feature
query_timeout
response_limit_exceeded
```

An inaccessible dataset or field SHOULD normally use the same response as an unknown one.

Recommended HTTP statuses:

```text
400 invalid syntax
401 unauthenticated
403 operation forbidden when disclosure is acceptable
408 or 504 query timeout according to hosting policy
413 query document too large
422 semantically invalid query
429 rate or concurrency limit
500 unexpected execution failure
```

## 25. Observability

Telemetry SHOULD record:

- Dataset logical name.
- Query-shape hash.
- Model and compiler versions.
- Complexity score.
- Compilation and execution duration.
- Rows and response bytes.
- Cache hit or miss.
- Timeout or cancellation.
- Provider error classification.

Logs MUST NOT include sensitive condition values by default. SQL text SHOULD be logged only under an explicit protected diagnostic mode.

## 26. Initialization and startup validation

At startup, an implementation MUST validate:

- Duplicate dataset and column names.
- Invalid public names.
- Missing physical mappings.
- Unsupported CLR or provider types.
- Conflicting attributes and fluent configuration.
- Exposed relationships to hidden datasets.
- Cursor fields that are nullable or non-deterministic where unsupported.
- Missing primary or stable ordering keys.
- Invalid operator/type combinations.
- Missing authorization policies.
- Invalid page-size and complexity limits.
- OpenAPI component-name collisions.

Initialization SHOULD fail rather than start with a partially exposed or ambiguous model.

## 27. Registration API

```csharp
builder.Services.AddOpenFetch<AppDbContext>(options =>
{
    options.DiscoverAttributedDatasets();
    options.UseSqlServer();

    options.Limits = new QueryLimits
    {
        MaximumDocumentBytes = 64 * 1024,
        MaximumPageSize = 500,
        MaximumRelationships = 3,
        MaximumRelationshipDepth = 2,
        MaximumFilterDepth = 6,
        MaximumConditions = 50,
        MaximumInValues = 200,
        MaximumSelectedFields = 50,
        MaximumComplexity = 150,
        CommandTimeout = TimeSpan.FromSeconds(10)
    };

    options.Security
        .RequireAuthenticatedUser()
        .UseDatabaseRowLevelSecurity();
});

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<OpenFetchDocumentTransformer>();
    options.AddOperationTransformer<OpenFetchOperationTransformer>();
    options.AddSchemaTransformer<OpenFetchSchemaTransformer>();
});

var app = builder.Build();

app.MapOpenFetch("/query");
app.MapOpenFetchCapabilities("/query/capabilities");
app.MapOpenFetchSchema("/query/schema");
app.MapOpenApi();
```

## 28. Public contracts

The first stable implementation SHOULD center on:

```csharp
IQueryParser
IQueryNormalizer
IQueryModelProvider
IQueryMetadataResolver
IQueryCapabilityValidator
IQueryAuthorizationService
IRowPolicyProvider
IQueryComplexityValidator
IQueryPlanner
IQueryCompiler
IQueryExecutor
IOpenFetchSchemaGenerator
```

Each stage SHOULD accept and return immutable data where practical.

## 29. Conformance and testing

### 29.1 Conformance corpus

The repository SHOULD include:

```text
conformance/
  valid/
  invalid/
  expected-ast/
  expected-plan/
  expected-results/
```

Every language feature MUST have valid and invalid examples.

### 29.2 Injection tests

Hostile strings MUST be tested in:

- Dataset names.
- Column names.
- Aliases.
- Relationship names.
- Operator names.
- Values.
- Sort directions.
- Cursor payloads.

Examples include:

```text
name]; DROP TABLE Users;--
' OR 1=1--
x UNION SELECT ...
dbo.Users
../../password
```

Identifiers MUST fail trusted metadata resolution. Values MUST remain parameters.

### 29.3 Tenant-isolation tests

Tests MUST verify that Tenant A cannot infer Tenant B data through:

- Selection.
- Filtering.
- Sorting.
- Counts.
- Aggregates.
- Relationship expansion.
- Grouping.
- Cursors.
- Error differences.
- Connection-pool reuse.

The suite MUST alternate tenants with a small connection pool to detect leaked session state.

### 29.4 Property-based and fuzz testing

The parser and validator SHOULD be fuzzed with:

- Deep filters.
- Large arrays.
- Malformed Unicode.
- Extreme numbers.
- Duplicate aliases.
- Circular relationship requests.
- Invalid dates.
- Unknown properties.
- Truncated JSON.

The only acceptable outcomes are a valid AST or a controlled validation error. Crashes, stack overflows, unbounded allocation, raw provider errors, and unintended SQL execution are failures.

### 29.5 OpenAPI contract tests

CI SHOULD:

1. Generate the OpenAPI document at build time.
2. Normalize component and path ordering.
3. Compare it to the committed contract.
4. Detect breaking changes.
5. Validate request and response examples.
6. Generate C# and TypeScript clients.
7. Compile generated clients.
8. Run representative queries against supported providers.

Breaking changes include removing a dataset, field, operator, relationship, or response property; changing a type; reducing a limit; strengthening authorization; or changing cursor compatibility.

## 30. Recommended release scope

### 30.1 Version 0.1

- PostgreSQL compiler.
- EF Core metadata adapter.
- Attribute-based dataset, column, and relationship exposure.
- Fluent overrides.
- Projection.
- Nested Boolean filters.
- Comparison, null, set, range, and string operators.
- Ordering.
- Cursor and bounded offset pagination.
- Application row-policy injection.
- PostgreSQL RLS session-context integration.
- Capability and schema endpoints.
- ADO.NET execution.
- OpenAPI 3.1 generation.
- Problem Details errors.
- Conformance, injection, isolation, and fuzz tests.

### 30.2 Version 0.2

- SQL Server compiler and RLS integration.
- Query-shape cache.
- Stable policy-profile documents.
- Dapper integration.
- Named fixed projections.
- Improved SDK generation.

### 30.3 Version 1.0

- Stable language and extension specification.
- Provider conformance requirements.
- Aggregation and grouping.
- Signed cursor profile.
- Reference TypeScript query builder.
- Compatibility and security certification suite.

## 31. Reference request and response

Request:

```http
POST /query/orders
Content-Type: application/json
Authorization: Bearer <token>
```

```json
{
  "version": "1.0",
  "select": ["id", "number", "status", "total", "createdAt"],
  "where": {
    "type": "logical",
    "operator": "and",
    "children": [
      {
        "type": "comparison",
        "field": "status",
        "operator": "in",
        "value": ["open", "processing"]
      },
      {
        "type": "comparison",
        "field": "createdAt",
        "operator": "gte",
        "value": "2026-01-01T00:00:00Z"
      }
    ]
  },
  "orderBy": [
    { "field": "createdAt", "direction": "desc" },
    { "field": "id", "direction": "asc" }
  ],
  "page": {
    "first": 100
  }
}
```

Response:

```json
{
  "data": [
    {
      "id": "c839ef28-7bdd-4e59-9c45-d407393c9519",
      "number": "SO-2026-1042",
      "status": "open",
      "total": 1250.50,
      "createdAt": "2026-10-27T12:30:00Z"
    }
  ],
  "page": {
    "count": 1,
    "hasNext": true,
    "endCursor": "opaque-authenticated-cursor"
  },
  "meta": {
    "dataset": "orders",
    "modelVersion": "42"
  }
}
```

## 32. Final design principles

1. The logical query model is the public contract.
2. EF Core discovers physical metadata but does not decide exposure.
3. Attributes provide concise static exposure declarations.
4. Fluent configuration handles contextual policy and advanced mapping.
5. Deny-by-default applies to datasets, columns, relationships, and operations.
6. The canonical AST separates syntax from execution.
7. Application policies and database RLS work together.
8. Values are parameters; identifiers come only from trusted metadata.
9. Capabilities and OpenAPI are generated from the same model as runtime validation.
10. Dataset-specific endpoints provide the best SDK and documentation experience.
11. Static OpenAPI describes deployable capabilities; caller-specific schema describes effective capabilities.
12. Performance comes primarily from direct projection, parameterized SQL, indexes, bounded complexity, streaming, and query-shape caching, not from JSON parsing alone.
