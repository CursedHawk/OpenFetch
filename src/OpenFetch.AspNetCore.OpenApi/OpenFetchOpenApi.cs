using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using OpenFetch.AspNetCore;

namespace OpenFetch.AspNetCore.OpenApi;

public static class OpenFetchOpenApiExtensions
{
    /// <summary>
    /// Adds the OpenFetch transformers (§18.8). Everything is generated from the finalized QueryModel; there is no second exposure config.
    /// With <paramref name="profile"/> = null the document describes every deployable capability (policies annotated via
    /// x-openfetch-authorization). With a profile (§18.7), only datasets/fields/relationships whose policies are all in the
    /// profile are included, e.g. <c>AddOpenApi("public", o =&gt; o.AddOpenFetch(profile: []))</c>.
    /// </summary>
    public static OpenApiOptions AddOpenFetch(this OpenApiOptions options, IReadOnlyCollection<string>? profile = null)
    {
        Func<IEnumerable<string>, bool> allowed = profile is null ? _ => true : p => p.All(profile.Contains);
        options.AddOperationTransformer((op, ctx, ct) =>
            new OpenFetchOperationTransformer(ctx.ApplicationServices.GetRequiredService<QueryEngine>(), allowed).TransformAsync(op, ctx, ct));
        options.AddDocumentTransformer((doc, ctx, ct) =>
            new OpenFetchDocumentTransformer(ctx.ApplicationServices.GetRequiredService<QueryEngine>(), allowed, profile).TransformAsync(doc, ctx, ct));
        return options;
    }
}

/// <summary>
/// Per-dataset component schemas (§18.3-18.5): XQuery, XSelectableField, XSortableField, XFilterableField, XFilter (oneOf +
/// discriminator, each branch with a const type), XComparisonFilter, XLogicalFilter, XNotFilter, XOrder, XPage, XRow, XResult.
/// Comparison filters use operator-family form (field enum x operator enum); runtime validation stays authoritative.
/// </summary>
public sealed class OpenFetchDocumentTransformer(QueryEngine engine, Func<IEnumerable<string>, bool> allowed, IReadOnlyCollection<string>? profile = null) : IOpenApiDocumentTransformer
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
        if (profile is not null) document.Extensions["x-openfetch-profile"] = Policies(profile.Order(StringComparer.Ordinal));

        // Operation transformers run first; drop operations for datasets outside this profile.
        foreach (var (path, item) in document.Paths?.ToList() ?? [])
            if (item.Operations?.Values.Any(o => o.Extensions?.TryGetValue("x-openfetch-dataset", out var d) == true
                    && d is JsonNodeExtension { Node: var n } && (!Visible(model, (string)n!)
                        || (o.Extensions.TryGetValue("x-openfetch-projection", out var p) && p is JsonNodeExtension { Node: JsonObject proj }
                            && !ProjectionVisible(model, model.Datasets[(string)n!], (string)proj["name"]!)))) == true)
                document.Paths!.Remove(path);

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

        if (model.Datasets.Values.Any(d => d.Capabilities.HasFlag(DatasetCapabilities.Aggregation)))
        {
            schemas["OpenFetchHavingComparison"] = Object(new()
            {
                ["type"] = Const("comparison"),
                ["field"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "An aggregate alias or a groupBy field." },
                ["operator"] = Enum(QueryOperators.Comparison.Concat(QueryOperators.Collections).Order().Select(QueryOperators.Name)),
                ["value"] = new OpenApiSchema { OneOf = [Ref("OpenFetchScalar"), new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref("OpenFetchScalar") }] },
            }, "type", "field", "operator");
            schemas["OpenFetchHavingLogical"] = Object(new()
            {
                ["type"] = Const("logical"),
                ["operator"] = Enum(["and", "or"]),
                ["children"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref("OpenFetchHavingFilter"), MinItems = 1 },
            }, "type", "operator", "children");
            schemas["OpenFetchHavingNot"] = Object(new() { ["type"] = Const("not"), ["operand"] = Ref("OpenFetchHavingFilter") }, "type", "operand");
            schemas["OpenFetchHavingFilter"] = new OpenApiSchema
            {
                OneOf = [Ref("OpenFetchHavingComparison"), Ref("OpenFetchHavingLogical"), Ref("OpenFetchHavingNot")],
                Discriminator = new OpenApiDiscriminator
                {
                    PropertyName = "type",
                    Mapping = new Dictionary<string, OpenApiSchemaReference>
                    {
                        ["comparison"] = Ref("OpenFetchHavingComparison"), ["logical"] = Ref("OpenFetchHavingLogical"), ["not"] = Ref("OpenFetchHavingNot"),
                    },
                },
            };
        }

        foreach (var d in model.Datasets.Values.Where(d => Visible(model, d.PublicName)).OrderBy(d => d.PublicName, StringComparer.Ordinal))
            AddDataset(schemas, model, d);
        return Task.CompletedTask;
    }

    private bool Visible(QueryModel model, string dataset) => allowed(model.Datasets[dataset].Policies);

    /// <summary>A projection is published only if every field on it (and every relationship on the way) is allowed.</summary>
    private bool ProjectionVisible(QueryModel model, DatasetDescriptor d, string name) =>
        d.Projections[name].All(f => Resolve(model, d, f) is var (rels, col) && allowed(col.Policies) && rels.All(r => allowed(r.Policies) && Visible(model, r.TargetDataset)));

    /// <summary>Walks a validated dotted path (projections are checked at startup).</summary>
    internal static (List<RelationshipDescriptor> Relationships, ColumnDescriptor Column) Resolve(QueryModel model, DatasetDescriptor d, string path)
    {
        var segments = path.Split('.');
        var rels = new List<RelationshipDescriptor>();
        for (var i = 0; i < segments.Length; i++)
        {
            var rest = string.Join('.', segments[i..]);
            if (d.Columns.TryGetValue(rest, out var column)) return (rels, column); // owned members are dotted columns
            var r = d.Relationships[segments[i]];
            rels.Add(r);
            d = model.Datasets[r.TargetDataset];
        }
        throw new InvalidOperationException($"Unresolvable projection path '{path}'.");
    }

    private void AddDataset(IDictionary<string, IOpenApiSchema> schemas, QueryModel model, DatasetDescriptor d)
    {
        var x = QueryModelBuilder.ComponentName(d.PublicName);
        var selectable = Paths(model, d, ColumnCapabilities.Select, RelationshipCapabilities.Select, allowed);
        var sortable = Paths(model, d, ColumnCapabilities.Sort, RelationshipCapabilities.Sort, allowed);
        var filterable = Paths(model, d, ColumnCapabilities.Filter, RelationshipCapabilities.Filter, allowed);
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

        var collections = d.Relationships.Values
            .Where(r => r.Cardinality == Cardinality.Many && allowed(r.Policies) && Visible(model, r.TargetDataset))
            .OrderBy(r => r.PublicName, StringComparer.Ordinal).ToList();
        var anyCollections = collections.Where(r => r.Can(RelationshipCapabilities.Filter)).ToList();
        var expandable = collections.Where(r => r.Can(RelationshipCapabilities.Select)).ToList();
        // Nested filters/expansions use the target's own fields; a single target gets a precise $ref, several get a loose schema.
        IOpenApiSchema TargetFilter(List<RelationshipDescriptor> rels) =>
            rels.Select(r => r.TargetDataset).Distinct().Count() == 1 && model.Datasets[rels[0].TargetDataset].Capabilities.HasFlag(DatasetCapabilities.Filter)
                ? Ref(QueryModelBuilder.ComponentName(rels[0].TargetDataset) + "Filter")
                : new OpenApiSchema { Type = JsonSchemaType.Object, Description = "A filter over the related dataset's own fields." };

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
            var branches = new Dictionary<string, OpenApiSchemaReference>
            {
                ["comparison"] = Ref(x + "ComparisonFilter"),
                ["logical"] = Ref(x + "LogicalFilter"),
                ["not"] = Ref(x + "NotFilter"),
            };
            if (anyCollections.Count > 0)
            {
                schemas[x + "AnyFilter"] = Object(new()
                {
                    ["type"] = Const("any"),
                    ["relationship"] = Enum(anyCollections.Select(r => r.PublicName)),
                    ["where"] = TargetFilter(anyCollections),
                }, "type", "relationship");
                schemas[x + "AnyFilter"].Description = "True when at least one related row matches 'where' (none = not any; all = not any(not ...)).";
                branches["any"] = Ref(x + "AnyFilter");
            }
            schemas[x + "Filter"] = new OpenApiSchema
            {
                OneOf = [.. branches.Values],
                Discriminator = new OpenApiDiscriminator { PropertyName = "type", Mapping = branches },
            };
        }

        if (expandable.Count > 0)
        {
            schemas[x + "Expand"] = Object(new()
            {
                ["relationship"] = Enum(expandable.Select(r => r.PublicName)),
                ["select"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Scalar(JsonSchemaType.String), MinItems = 1, Description = "Fields of the related dataset (no dotted paths)." },
                ["where"] = TargetFilter(expandable),
                ["orderBy"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Object(new() { ["field"] = Scalar(JsonSchemaType.String), ["direction"] = Enum(["asc", "desc"]) }, "field") },
                ["first"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Description = "Rows per parent (default: 10, at most the related dataset's maximum page size)." },
            }, "relationship", "select");
            query["expand"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(x + "Expand") };
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
        foreach (var extension in model.Features.Extensions.Order(StringComparer.Ordinal))
            query[extension] = new OpenApiSchema { Description = "Registered extension; passed to the application, never interpreted by OpenFetch." };

        var aggregation = d.Capabilities.HasFlag(DatasetCapabilities.Aggregation);
        if (aggregation)
        {
            // §21: groupBy/aggregates replace select; having filters aggregate aliases and group fields.
            var groupable = Paths(model, d, ColumnCapabilities.Group, RelationshipCapabilities.Select, allowed);
            var aggregatable = Paths(model, d, ColumnCapabilities.Aggregate, RelationshipCapabilities.Select, allowed);
            schemas[x + "GroupableField"] = Enum(groupable.Select(p => p.Path));
            schemas[x + "AggregatableField"] = Enum(aggregatable.Select(p => p.Path));
            schemas[x + "Aggregate"] = Object(new()
            {
                ["function"] = Enum(QueryParser.FunctionNames),
                ["field"] = Ref(x + "AggregatableField"),
                ["alias"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[A-Za-z][A-Za-z0-9_]{0,63}$" },
            }, "function", "alias");
            query["groupBy"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(x + "GroupableField"), UniqueItems = true };
            query["aggregates"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(x + "Aggregate") };
            query["having"] = Ref("OpenFetchHavingFilter");
            ((OpenApiSchema)query["select"]).Description += " Not allowed in aggregate queries (groupBy/aggregates).";
        }
        schemas[x + "Query"] = aggregation ? Object(query) : Object(query, "select");
        schemas[x + "Query"].Description = d.Description;

        // §18.5: a superset row; every property optional because only selected fields are returned.
        var row = new Dictionary<string, IOpenApiSchema>();
        foreach (var c in d.ExposedColumns.Where(c => c.Can(ColumnCapabilities.Select) && allowed(c.Policies))) Nest(row, c.PublicName.Split('.'), Column(c));
        foreach (var r in expandable)
            row[r.PublicName] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = Ref(QueryModelBuilder.ComponentName(r.TargetDataset) + "Row"),
                Description = $"Present when expanded: related {r.TargetDataset} rows (only the expansion's selected fields).",
            };
        foreach (var r in d.Relationships.Values.Where(r => r.Cardinality == Cardinality.One && r.Can(RelationshipCapabilities.Select) && allowed(r.Policies) && Visible(model, r.TargetDataset)).OrderBy(r => r.PublicName, StringComparer.Ordinal))
        {
            var target = QueryModelBuilder.ComponentName(r.TargetDataset);
            row[r.PublicName] = new OpenApiSchema
            {
                Description = r.Description ?? $"Related {r.TargetDataset} row (null when absent or not visible).",
                OneOf = [Ref(target + "Row"), new OpenApiSchema { Type = JsonSchemaType.Null }],
                Extensions = r.Policies.IsEmpty ? null : new Dictionary<string, IOpenApiExtension> { ["x-openfetch-authorization"] = Policies(r.Policies) },
            };
        }
        schemas[x + "Row"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = row,
            Description = $"A {d.PublicName} row. Only selected fields are present." + (aggregation ? " Aggregate queries add one property per aggregate alias." : ""),
            AdditionalProperties = aggregation ? Ref("OpenFetchScalar") : null,
        };

        schemas[x + "Result"] = Result(x + "Row");

        // §18.5 named projections: the server fixes select, so rows have required properties and are strongly typed.
        foreach (var (name, fields) in d.Projections.Where(p => ProjectionVisible(model, d, p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var px = x + QueryModelBuilder.ComponentName(name);
            // Projections have a fixed shape: no select, aggregation or expansion (security review).
            string[] notInProjection = ["select", "groupBy", "aggregates", "having", "expand"];
            var fixedQuery = query.Where(p => !notInProjection.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            schemas[px + "Query"] = Object(fixedQuery);
            schemas[px + "Query"].Description = $"Query for the fixed '{name}' projection of {d.PublicName}; 'select' is defined by the server.";
            schemas[px + "Row"] = ProjectionRow(model, d, fields.Select(f => (Path: f, Segments: f.Split('.'))).ToList(), 0);
            schemas[px + "Result"] = Result(px + "Row");
        }
    }

    private static OpenApiSchema Result(string row) => Object(new()
    {
        ["data"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = Ref(row) },
        ["page"] = Ref("OpenFetchPageInfo"),
        ["meta"] = Ref("OpenFetchMeta"),
    }, "data", "page", "meta");

    /// <summary>Places a dotted owned column ("home.city") as a nested object property of the row schema.</summary>
    private static void Nest(IDictionary<string, IOpenApiSchema> properties, string[] path, IOpenApiSchema leaf)
    {
        if (path.Length == 1) { properties[path[0]] = leaf; return; }
        if (!properties.TryGetValue(path[0], out var existing) || existing is not OpenApiSchema { Properties: { } } nested)
            properties[path[0]] = nested = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema>(), Description = "Owned value object." };
        Nest(nested.Properties!, path[1..], leaf);
    }

    private static OpenApiSchema ProjectionRow(QueryModel model, DatasetDescriptor d, List<(string Path, string[] Segments)> fields, int depth, string owned = "")
    {
        var properties = new Dictionary<string, IOpenApiSchema>();
        foreach (var group in fields.GroupBy(f => f.Segments[depth]))
        {
            var full = owned + group.Key;
            if (group.First().Segments.Length == depth + 1) { properties[group.Key] = Column(d.Columns[full]); continue; }
            if (owned.Length == 0 && d.Relationships.TryGetValue(group.Key, out var rel) && !d.Columns.Keys.Any(c => c.StartsWith(full + ".", StringComparison.Ordinal)))
            {
                var nested = ProjectionRow(model, model.Datasets[rel.TargetDataset], [.. group], depth + 1);
                nested.Type = JsonSchemaType.Object | JsonSchemaType.Null;
                nested.Description = "Null when the related row is absent or not visible.";
                properties[group.Key] = nested;
            }
            else properties[group.Key] = ProjectionRow(model, d, [.. group], depth + 1, full + "."); // owned value object, same row
        }
        return Object(properties, [.. properties.Keys]);
    }

    internal sealed record PathColumn(string Path, ColumnDescriptor Column);

    /// <summary>All field paths usable for a capability, following relationships up to their allowed depth.</summary>
    internal static List<PathColumn> Paths(QueryModel model, DatasetDescriptor root, ColumnCapabilities cap, RelationshipCapabilities rel, Func<IEnumerable<string>, bool> allowed)
    {
        var result = new List<PathColumn>();
        void Walk(DatasetDescriptor d, string prefix, int depth)
        {
            foreach (var c in d.ExposedColumns.Where(c => c.Can(cap) && allowed(c.Policies))) result.Add(new(prefix + c.PublicName, c));
            foreach (var r in d.Relationships.Values.Where(r => r.Cardinality == Cardinality.One && r.Can(rel) && allowed(r.Policies) && allowed(model.Datasets[r.TargetDataset].Policies)
                         && depth + 1 <= Math.Min(r.MaximumDepth, root.MaximumRelationshipDepth)).OrderBy(r => r.PublicName, StringComparer.Ordinal))
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
public sealed class OpenFetchOperationTransformer(QueryEngine engine, Func<IEnumerable<string>, bool> allowed) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<OpenFetchDatasetMetadata>().FirstOrDefault() is not { } meta) return Task.CompletedTask;
        var model = engine.Model;
        var d = model.Datasets[meta.Dataset];
        var x = QueryModelBuilder.ComponentName(d.PublicName) + (meta.Projection is null ? "" : QueryModelBuilder.ComponentName(meta.Projection));
        var l = model.Limits;
        var relationships = d.Relationships.Values.Where(r => allowed(r.Policies) && allowed(model.Datasets[r.TargetDataset].Policies)).ToList();

        operation.OperationId = OpenFetchEndpoints.OperationId(d.PublicName, meta.Projection);
        operation.Summary = meta.Projection is null ? $"Query {d.PublicName}" : $"Query {d.PublicName} ({meta.Projection} projection)";
        operation.Description = (d.Description is null ? "" : d.Description + "\n\n") + (meta.Projection is null
            ? "Only the fields listed in `select` are returned; every other row property is absent. Relationship fields are nested objects. "
            : $"Returns exactly the fields of the fixed `{meta.Projection}` projection; `select` must not be sent. ") +
            "Errors are Problem Details with a stable `code` and a JSON Pointer `path`.";
        operation.Deprecated = d.Deprecated;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Schema = OpenFetchDocumentTransformer.Ref(x + "Query"), Example = Example(model, d, allowed, meta.Projection) },
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
            ["relationships"] = relationships.Count > 0,
            ["aggregation"] = d.Capabilities.HasFlag(DatasetCapabilities.Aggregation),
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
        ext["x-openfetch-projection"] = meta.Projection is null
            ? OpenFetchDocumentTransformer.Ext("dynamic")
            : OpenFetchDocumentTransformer.Ext(new JsonObject { ["name"] = meta.Projection, ["fields"] = new JsonArray([.. d.Projections[meta.Projection].Select(f => (JsonNode)f)]) });
        ext["x-openfetch-relationships"] = OpenFetchDocumentTransformer.Ext(new JsonArray([.. relationships.OrderBy(r => r.PublicName, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonObject { ["name"] = r.PublicName, ["target"] = r.TargetDataset, ["cardinality"] = r.Cardinality == Cardinality.One ? "one" : "many", ["maxDepth"] = r.MaximumDepth })]));
        if (!d.Policies.IsEmpty) ext["x-openfetch-authorization"] = OpenFetchDocumentTransformer.Policies(d.Policies);
        return Task.CompletedTask;
    }

    /// <summary>A small valid request: the first selectable fields, ordered by the first sortable field. Validated by tests through the engine.</summary>
    public static JsonNode Example(QueryModel model, DatasetDescriptor d, Func<IEnumerable<string>, bool> allowed, string? projection = null)
    {
        var select = OpenFetchDocumentTransformer.Paths(model, d, ColumnCapabilities.Select, RelationshipCapabilities.Select, allowed)
            .Where(p => p.Column.Policies.IsEmpty).Take(3).Select(p => (JsonNode)p.Path);
        var example = new JsonObject { ["version"] = QueryParser.Version, ["page"] = new JsonObject { ["first"] = Math.Min(10, d.MaximumPageSize) } };
        if (projection is null) example["select"] = new JsonArray([.. select]);
        var sort = d.ExposedColumns.FirstOrDefault(c => c.Can(ColumnCapabilities.Sort) && c.Policies.IsEmpty);
        if (sort is not null && d.Capabilities.HasFlag(DatasetCapabilities.Sort)) example["orderBy"] = new JsonArray(new JsonObject { ["field"] = sort.PublicName, ["direction"] = "asc" });
        return example;
    }
}
