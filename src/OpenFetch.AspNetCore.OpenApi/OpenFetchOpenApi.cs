using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using OpenFetch.AspNetCore;

namespace OpenFetch.AspNetCore.OpenApi;

public static class OpenFetchOpenApiExtensions
{
    /// <summary>Adds the OpenFetch transformers (§18.8). Everything is generated from the finalized QueryModel; there is no second exposure config.</summary>
    public static OpenApiOptions AddOpenFetch(this OpenApiOptions options)
    {
        options.AddDocumentTransformer<OpenFetchDocumentTransformer>();
        options.AddOperationTransformer<OpenFetchOperationTransformer>();
        return options;
    }
}

/// <summary>
/// Per-dataset component schemas (§18.3-18.5): XQuery, XSelectableField, XSortableField, XFilterableField, XFilter (oneOf +
/// discriminator, each branch with a const type), XComparisonFilter, XLogicalFilter, XNotFilter, XOrder, XPage, XRow, XResult.
/// Comparison filters use operator-family form (field enum x operator enum); runtime validation stays authoritative.
/// </summary>
public sealed class OpenFetchDocumentTransformer(QueryEngine engine) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var model = engine.Model;
        document.Components ??= new OpenApiComponents();
        var schemas = document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-openfetch-version"] = Ext(QueryParser.Version);
        document.Extensions["x-openfetch-provider"] = Ext(new JsonObject { ["name"] = engine.Compiler.ProviderName, ["compilerVersion"] = engine.Compiler.CompilerVersion });
        document.Extensions["x-openfetch-model-version"] = Ext(model.Version);

        schemas["OpenFetchProblem"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "RFC 9457 Problem Details with a stable machine-readable code and a JSON Pointer path (§24).",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = Scalar(JsonSchemaType.String),
                ["title"] = Scalar(JsonSchemaType.String),
                ["status"] = Scalar(JsonSchemaType.Integer),
                ["detail"] = Scalar(JsonSchemaType.String),
                ["code"] = Scalar(JsonSchemaType.String),
                ["path"] = Scalar(JsonSchemaType.String),
            },
            Required = new HashSet<string> { "type", "title", "status", "code" },
        };
        schemas["OpenFetchPageInfo"] = Object(new()
        {
            ["count"] = Scalar(JsonSchemaType.Integer),
            ["hasNext"] = Scalar(JsonSchemaType.Boolean),
            ["endCursor"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Description = "Opaque authenticated cursor; null on the last page or when the ordering is not cursor-capable." },
        }, "count", "hasNext", "endCursor");
        schemas["OpenFetchMeta"] = Object(new() { ["dataset"] = Scalar(JsonSchemaType.String), ["modelVersion"] = Scalar(JsonSchemaType.String) }, "dataset", "modelVersion");
        schemas["OpenFetchScalar"] = new OpenApiSchema
        {
            Description = "Native JSON scalar. Dates, times and GUIDs are strings (RFC 3339 date-time with offset, yyyy-MM-dd, HH:mm:ss, 36-char GUID).",
            Type = JsonSchemaType.String | JsonSchemaType.Number | JsonSchemaType.Boolean | JsonSchemaType.Null,
        };

        foreach (var d in model.Datasets.Values.OrderBy(d => d.PublicName, StringComparer.Ordinal))
            AddDataset(schemas, model, d);
        return Task.CompletedTask;
    }

    private static void AddDataset(IDictionary<string, IOpenApiSchema> schemas, QueryModel model, DatasetDescriptor d)
    {
        var x = QueryModelBuilder.ComponentName(d.PublicName);
        var selectable = Paths(model, d, ColumnCapabilities.Select, RelationshipCapabilities.Select);
        var sortable = Paths(model, d, ColumnCapabilities.Sort, RelationshipCapabilities.Sort);
        var filterable = Paths(model, d, ColumnCapabilities.Filter, RelationshipCapabilities.Filter);
        var limits = model.Limits;

        schemas[x + "SelectableField"] = Enum(selectable.Select(p => p.Path));
        schemas[x + "SortableField"] = Enum(sortable.Select(p => p.Path));

        var query = new Dictionary<string, IOpenApiSchema>
        {
            ["version"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [QueryParser.Version] },
            ["select"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array, Items = Ref(x + "SelectableField"), MinItems = 1, MaxItems = limits.MaximumSelectedFields, UniqueItems = true,
                Description = "Fields to return. Relationship fields use dotted paths and are returned as nested objects.",
            },
            ["orderBy"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(x + "Order") },
            ["page"] = Ref(x + "Page"),
        };

        if (d.Capabilities.HasFlag(DatasetCapabilities.Filter) && filterable.Count > 0)
        {
            query["where"] = Ref(x + "Filter");
            schemas[x + "FilterableField"] = Enum(filterable.Select(p => p.Path));
            var operators = filterable.SelectMany(p => p.Column.Operators).Distinct().Order().Select(QueryOperators.Name);
            var comparison = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = Const("comparison"),
                ["field"] = Ref(x + "FilterableField"),
                ["operator"] = Enum(operators),
                ["value"] = new OpenApiSchema
                {
                    Description = "Scalar for comparison/string operators; array for in/notIn; two-element array for between/notBetween; null only with eq/ne.",
                    OneOf = [Ref("OpenFetchScalar"), new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref("OpenFetchScalar"), MaxItems = limits.MaximumInValues }],
                },
            };
            if (model.Features.FieldComparison)
                comparison["compareTo"] = Object(new() { ["field"] = Ref(x + "FilterableField") }, "field");
            var cmp = Object(comparison, "type", "field", "operator");
            cmp.Extensions = new Dictionary<string, IOpenApiExtension>
            {
                ["x-openfetch-operators"] = Ext(new JsonObject(filterable.Select(p => KeyValuePair.Create(p.Path,
                    (JsonNode?)new JsonArray([.. p.Column.Operators.Order().Select(o => (JsonNode)QueryOperators.Name(o))]))))),
            };
            schemas[x + "ComparisonFilter"] = cmp;
            schemas[x + "LogicalFilter"] = Object(new()
            {
                ["type"] = Const("logical"),
                ["operator"] = Enum(["and", "or"]),
                ["children"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(x + "Filter"), MinItems = 1 },
            }, "type", "operator", "children");
            schemas[x + "NotFilter"] = Object(new() { ["type"] = Const("not"), ["operand"] = Ref(x + "Filter") }, "type", "operand");
            schemas[x + "Filter"] = new OpenApiSchema
            {
                OneOf = [Ref(x + "ComparisonFilter"), Ref(x + "LogicalFilter"), Ref(x + "NotFilter")],
                Discriminator = new OpenApiDiscriminator
                {
                    PropertyName = "type",
                    Mapping = new Dictionary<string, OpenApiSchemaReference>
                    {
                        ["comparison"] = Ref(x + "ComparisonFilter"),
                        ["logical"] = Ref(x + "LogicalFilter"),
                        ["not"] = Ref(x + "NotFilter"),
                    },
                },
            };
        }

        schemas[x + "Order"] = Object(new()
        {
            ["field"] = Ref(x + "SortableField"),
            ["direction"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = ["asc", "desc"], Default = "asc" },
        }, "field");

        var page = new Dictionary<string, IOpenApiSchema>
        {
            ["first"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = d.MaximumPageSize.ToString(), Default = d.DefaultPageSize },
        };
        if (d.Capabilities.HasFlag(DatasetCapabilities.CursorPagination))
            page["after"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Description = "endCursor of the previous page. Cannot be combined with offset." };
        if (d.Capabilities.HasFlag(DatasetCapabilities.OffsetPagination))
            page["offset"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0", Maximum = limits.MaximumOffset.ToString() };
        schemas[x + "Page"] = Object(page);

        if (d.Capabilities.HasFlag(DatasetCapabilities.Distinct)) query["distinct"] = Scalar(JsonSchemaType.Boolean);
        schemas[x + "Query"] = Object(query, "select");
        schemas[x + "Query"].Description = d.Description;

        // §18.5: a superset row; every property optional because only selected fields are returned.
        var row = new Dictionary<string, IOpenApiSchema>();
        foreach (var c in d.ExposedColumns.Where(c => c.Can(ColumnCapabilities.Select))) row[c.PublicName] = Column(c);
        foreach (var r in d.Relationships.Values.Where(r => r.Can(RelationshipCapabilities.Select)).OrderBy(r => r.PublicName, StringComparer.Ordinal))
        {
            var target = QueryModelBuilder.ComponentName(r.TargetDataset);
            row[r.PublicName] = new OpenApiSchema
            {
                Description = r.Description ?? $"Related {r.TargetDataset} row (null when absent or not visible).",
                OneOf = [Ref(target + "Row"), new OpenApiSchema { Type = JsonSchemaType.Null }],
                Extensions = r.Policies.IsEmpty ? null : new Dictionary<string, IOpenApiExtension> { ["x-openfetch-authorization"] = Policies(r.Policies) },
            };
        }
        schemas[x + "Row"] = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = row, Description = $"A {d.PublicName} row. Only selected fields are present." };

        schemas[x + "Result"] = Object(new()
        {
            ["data"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(x + "Row") },
            ["page"] = Ref("OpenFetchPageInfo"),
            ["meta"] = Ref("OpenFetchMeta"),
        }, "data", "page", "meta");
    }

    internal sealed record PathColumn(string Path, ColumnDescriptor Column);

    /// <summary>All field paths usable for a capability, following relationships up to their allowed depth.</summary>
    internal static List<PathColumn> Paths(QueryModel model, DatasetDescriptor root, ColumnCapabilities cap, RelationshipCapabilities rel)
    {
        var result = new List<PathColumn>();
        void Walk(DatasetDescriptor d, string prefix, int depth)
        {
            foreach (var c in d.ExposedColumns.Where(c => c.Can(cap))) result.Add(new(prefix + c.PublicName, c));
            foreach (var r in d.Relationships.Values.Where(r => r.Can(rel) && depth + 1 <= Math.Min(r.MaximumDepth, root.MaximumRelationshipDepth)).OrderBy(r => r.PublicName, StringComparer.Ordinal))
                Walk(model.Datasets[r.TargetDataset], prefix + r.PublicName + ".", depth + 1);
        }
        Walk(root, "", 0);
        return result;
    }

    private static OpenApiSchema Column(ColumnDescriptor c)
    {
        var (type, format) = c.Kind switch
        {
            ScalarKind.String => (JsonSchemaType.String, null),
            ScalarKind.Integer => (JsonSchemaType.Integer, c.ClrType == typeof(long) ? "int64" : "int32"),
            ScalarKind.Decimal => (JsonSchemaType.Number, "decimal"),
            ScalarKind.Number => (JsonSchemaType.Number, "double"),
            ScalarKind.Boolean => (JsonSchemaType.Boolean, null),
            ScalarKind.Guid => (JsonSchemaType.String, "uuid"),
            ScalarKind.DateTime => (JsonSchemaType.String, "date-time"),
            ScalarKind.Date => (JsonSchemaType.String, "date"),
            ScalarKind.Time => (JsonSchemaType.String, "time"),
            _ => (JsonSchemaType.String, (string?)null),
        };
        var schema = new OpenApiSchema
        {
            Type = c.Nullable ? type | JsonSchemaType.Null : type,
            Format = c.Format ?? format,
            Description = c.Description,
            Deprecated = c.Deprecated,
            MaxLength = c.Kind == ScalarKind.String ? c.MaxLength : null,
            Extensions = new Dictionary<string, IOpenApiExtension>
            {
                ["x-openfetch-capabilities"] = Ext(new JsonArray([.. MetadataDocuments.CapabilityNames(c.Capabilities).Select(n => (JsonNode)n)])),
            },
        };
        if (!c.EnumValues.IsEmpty) schema.Enum = [.. c.EnumValues.Select(v => (JsonNode)v)];
        if (c.Can(ColumnCapabilities.Filter)) schema.Extensions["x-openfetch-operators"] = Ext(new JsonArray([.. c.Operators.Order().Select(o => (JsonNode)QueryOperators.Name(o))]));
        if (!c.Policies.IsEmpty) schema.Extensions["x-openfetch-authorization"] = Policies(c.Policies);
        if (c.Sensitivity != Sensitivity.Public) schema.Extensions["x-openfetch-sensitivity"] = Ext(c.Sensitivity.ToString().ToLowerInvariant());
        return schema;
    }

    internal static IOpenApiExtension Policies(IEnumerable<string> policies) => Ext(new JsonObject { ["policies"] = new JsonArray([.. policies.Select(p => (JsonNode)p)]) });
    internal static IOpenApiExtension Ext(JsonNode node) => new JsonNodeExtension(node);
    internal static OpenApiSchemaReference Ref(string id) => new(id);
    private static OpenApiSchema Scalar(JsonSchemaType type) => new() { Type = type };
    private static OpenApiSchema Const(string value) => new() { Type = JsonSchemaType.String, Const = value, Enum = [value] };
    private static OpenApiSchema Enum(IEnumerable<string> values) => new() { Type = JsonSchemaType.String, Enum = [.. values.Select(v => (JsonNode)v)] };

    private static OpenApiSchema Object(Dictionary<string, IOpenApiSchema> properties, params string[] required) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = properties,
        Required = required.Length == 0 ? null : new HashSet<string>(required),
        AdditionalPropertiesAllowed = false,
    };
}

/// <summary>Dataset operations: operationId, typed request/response refs, errors, limits, policies, example (§18.6, §18.8).</summary>
public sealed class OpenFetchOperationTransformer(QueryEngine engine) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<OpenFetchDatasetMetadata>().FirstOrDefault() is not { } meta) return Task.CompletedTask;
        var model = engine.Model;
        var d = model.Datasets[meta.Dataset];
        var x = QueryModelBuilder.ComponentName(d.PublicName);
        var l = model.Limits;

        operation.OperationId = OpenFetchEndpoints.OperationId(d.PublicName);
        operation.Summary = $"Query {d.PublicName}";
        operation.Description = (d.Description is null ? "" : d.Description + "\n\n") +
            "Only the fields listed in `select` are returned; every other row property is absent. Relationship fields are nested objects. " +
            "Errors are Problem Details with a stable `code` and a JSON Pointer `path`.";
        operation.Deprecated = d.Deprecated;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Schema = OpenFetchDocumentTransformer.Ref(x + "Query"), Example = Example(model, d) },
            },
        };
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "A page of rows.",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = OpenFetchDocumentTransformer.Ref(x + "Result") } },
            },
        };
        foreach (var (status, text) in new[] { ("400", "Invalid JSON or query syntax."), ("401", "Authentication required."), ("403", "Forbidden."),
                     ("404", "Unknown or inaccessible dataset."), ("413", "Query document too large."), ("422", "Semantically invalid query."),
                     ("429", "Rate or concurrency limit."), ("500", "Unexpected execution failure."), ("504", "Query timeout.") })
            operation.Responses[status] = new OpenApiResponse
            {
                Description = text,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = OpenFetchDocumentTransformer.Ref("OpenFetchProblem") } },
            };

        var ext = operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        ext["x-openfetch-dataset"] = OpenFetchDocumentTransformer.Ext(d.PublicName);
        ext["x-openfetch-capabilities"] = OpenFetchDocumentTransformer.Ext(new JsonObject
        {
            ["projection"] = true,
            ["filtering"] = d.Capabilities.HasFlag(DatasetCapabilities.Filter),
            ["sorting"] = d.Capabilities.HasFlag(DatasetCapabilities.Sort),
            ["cursorPagination"] = d.Capabilities.HasFlag(DatasetCapabilities.CursorPagination),
            ["offsetPagination"] = d.Capabilities.HasFlag(DatasetCapabilities.OffsetPagination),
            ["distinct"] = d.Capabilities.HasFlag(DatasetCapabilities.Distinct),
            ["relationships"] = d.Relationships.Count > 0,
            ["aggregation"] = false,
        });
        ext["x-openfetch-limits"] = OpenFetchDocumentTransformer.Ext(new JsonObject
        {
            ["maxPageSize"] = d.MaximumPageSize,
            ["defaultPageSize"] = d.DefaultPageSize,
            ["maxSelectedFields"] = l.MaximumSelectedFields,
            ["maxConditions"] = l.MaximumConditions,
            ["maxFilterDepth"] = l.MaximumFilterDepth,
            ["maxRelationships"] = l.MaximumRelationships,
            ["maxRelationshipDepth"] = d.MaximumRelationshipDepth,
            ["maxInValues"] = l.MaximumInValues,
            ["maxOffset"] = l.MaximumOffset,
            ["maxComplexity"] = l.MaximumComplexity,
        });
        ext["x-openfetch-complexity"] = OpenFetchDocumentTransformer.Ext(new JsonObject { ["version"] = Complexity.Version, ["max"] = l.MaximumComplexity });
        ext["x-openfetch-projection"] = OpenFetchDocumentTransformer.Ext("dynamic");
        ext["x-openfetch-relationships"] = OpenFetchDocumentTransformer.Ext(new JsonArray([.. d.Relationships.Values.OrderBy(r => r.PublicName, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonObject { ["name"] = r.PublicName, ["target"] = r.TargetDataset, ["cardinality"] = "one", ["maxDepth"] = r.MaximumDepth })]));
        if (!d.Policies.IsEmpty) ext["x-openfetch-authorization"] = OpenFetchDocumentTransformer.Policies(d.Policies);
        return Task.CompletedTask;
    }

    /// <summary>A small valid request: the first selectable fields, ordered by the first sortable field. Validated by tests through the engine.</summary>
    public static JsonNode Example(QueryModel model, DatasetDescriptor d)
    {
        var select = OpenFetchDocumentTransformer.Paths(model, d, ColumnCapabilities.Select, RelationshipCapabilities.Select)
            .Where(p => p.Column.Policies.IsEmpty).Take(3).Select(p => (JsonNode)p.Path);
        var example = new JsonObject { ["version"] = QueryParser.Version, ["select"] = new JsonArray([.. select]), ["page"] = new JsonObject { ["first"] = Math.Min(10, d.MaximumPageSize) } };
        var sort = d.ExposedColumns.FirstOrDefault(c => c.Can(ColumnCapabilities.Sort) && c.Policies.IsEmpty);
        if (sort is not null && d.Capabilities.HasFlag(DatasetCapabilities.Sort)) example["orderBy"] = new JsonArray(new JsonObject { ["field"] = sort.PublicName, ["direction"] = "asc" });
        return example;
    }
}
