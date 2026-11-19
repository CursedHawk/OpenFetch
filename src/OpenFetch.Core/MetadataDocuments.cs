using System.Buffers;
using System.Text.Json;

namespace OpenFetch;

/// <summary>Capability (§17.1) and caller-specific schema (§17.2) documents, generated from the same QueryModel as validation.</summary>
public static class MetadataDocuments
{
    public static byte[] Capabilities(QueryModel model, IQueryCompiler compiler)
    {
        var l = model.Limits;
        return Write(w =>
        {
            w.WriteString("language", "openfetch");
            w.WriteStartArray("versions"); w.WriteStringValue(QueryParser.Version); w.WriteEndArray();
            w.WriteStartObject("features");
            w.WriteBoolean("relationships", model.Datasets.Values.Any(d => d.Relationships.Count > 0));
            w.WriteBoolean("aggregates", false);
            w.WriteBoolean("cursorPagination", model.Datasets.Values.Any(d => d.Capabilities.HasFlag(DatasetCapabilities.CursorPagination)));
            w.WriteBoolean("offsetPagination", model.Datasets.Values.Any(d => d.Capabilities.HasFlag(DatasetCapabilities.OffsetPagination)));
            w.WriteBoolean("fieldComparison", model.Features.FieldComparison);
            w.WriteBoolean("distinct", model.Datasets.Values.Any(d => d.Capabilities.HasFlag(DatasetCapabilities.Distinct)));
            w.WriteString("stringComparison", "case-sensitive (column collation)");
            w.WriteEndObject();
            w.WriteStartObject("limits");
            w.WriteNumber("maxPageSize", l.MaximumPageSize);
            w.WriteNumber("defaultPageSize", l.DefaultPageSize);
            w.WriteNumber("maxOffset", l.MaximumOffset);
            w.WriteNumber("maxRelationships", l.MaximumRelationships);
            w.WriteNumber("maxRelationshipDepth", l.MaximumRelationshipDepth);
            w.WriteNumber("maxFilterDepth", l.MaximumFilterDepth);
            w.WriteNumber("maxConditions", l.MaximumConditions);
            w.WriteNumber("maxInValues", l.MaximumInValues);
            w.WriteNumber("maxSelectedFields", l.MaximumSelectedFields);
            w.WriteNumber("maxComplexity", l.MaximumComplexity);
            w.WriteNumber("maxDocumentBytes", l.MaximumDocumentBytes);
            w.WriteNumber("maxResponseBytes", l.MaximumResponseBytes);
            w.WriteNumber("timeoutSeconds", l.CommandTimeout.TotalSeconds);
            w.WriteNumber("cursorLifetimeSeconds", l.CursorLifetime.TotalSeconds);
            w.WriteEndObject();
            w.WriteStartObject("operators");
            Names(w, "comparison", QueryOperators.Comparison);
            Names(w, "collections", QueryOperators.Collections);
            Names(w, "strings", QueryOperators.Strings);
            w.WriteStartArray("logical"); w.WriteStringValue("and"); w.WriteStringValue("or"); w.WriteStringValue("not"); w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("complexity"); w.WriteNumber("version", Complexity.Version); w.WriteEndObject();
            w.WriteStartObject("provider");
            w.WriteString("name", compiler.ProviderName);
            w.WriteString("compilerVersion", compiler.CompilerVersion);
            w.WriteEndObject();
            w.WriteString("errorFormat", "application/problem+json");
        });
    }

    /// <summary>Only what the caller may use: inaccessible datasets, columns and relationships are omitted entirely.</summary>
    public static byte[] Schema(QueryModel model, Func<IEnumerable<string>, bool> allowed) => Write(w =>
    {
        w.WriteString("modelVersion", model.Version);
        w.WriteStartArray("entities");
        foreach (var d in model.Datasets.Values.Where(d => allowed(d.Policies)).OrderBy(d => d.PublicName, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("name", d.PublicName);
            if (d.Description is not null) w.WriteString("description", d.Description);
            if (d.Deprecated) { w.WriteBoolean("deprecated", true); w.WriteString("deprecationMessage", d.DeprecationMessage); }
            w.WriteNumber("defaultPageSize", d.DefaultPageSize);
            w.WriteNumber("maxPageSize", d.MaximumPageSize);
            w.WriteStartArray("capabilities");
            foreach (var c in Enum.GetValues<DatasetCapabilities>().Where(c => c is not DatasetCapabilities.None and not DatasetCapabilities.Default && d.Capabilities.HasFlag(c)))
                w.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(c.ToString()));
            w.WriteEndArray();
            w.WriteStartArray("fields");
            foreach (var c in d.ExposedColumns.Where(c => allowed(c.Policies))) Field(w, c);
            w.WriteEndArray();
            w.WriteStartArray("relationships");
            foreach (var r in d.Relationships.Values.Where(r => allowed(r.Policies) && allowed(model.Datasets[r.TargetDataset].Policies)).OrderBy(r => r.PublicName, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", r.PublicName);
                w.WriteString("target", r.TargetDataset);
                w.WriteString("cardinality", r.Cardinality == Cardinality.One ? "one" : "many");
                w.WriteBoolean("expandable", r.Can(RelationshipCapabilities.Expand));
                w.WriteNumber("maxDepth", r.MaximumDepth);
                w.WriteStartArray("capabilities");
                foreach (var c in new[] { RelationshipCapabilities.Select, RelationshipCapabilities.Filter, RelationshipCapabilities.Sort }.Where(r.Can))
                    w.WriteStringValue(c.ToString().ToLowerInvariant());
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private static void Field(Utf8JsonWriter w, ColumnDescriptor c)
    {
        w.WriteStartObject();
        w.WriteString("name", c.PublicName);
        w.WriteString("type", QueryOperators.TypeName(c.Kind));
        w.WriteBoolean("nullable", c.Nullable);
        if (c.Description is not null) w.WriteString("description", c.Description);
        if (c.Format is not null) w.WriteString("format", c.Format);
        if (c.MaxLength is { } max) w.WriteNumber("maxLength", max);
        if (c.Precision is { } p) w.WriteNumber("precision", p);
        if (c.Scale is { } s) w.WriteNumber("scale", s);
        if (c.Deprecated) { w.WriteBoolean("deprecated", true); w.WriteString("deprecationMessage", c.DeprecationMessage); }
        if (!c.EnumValues.IsEmpty) { w.WriteStartArray("enum"); foreach (var v in c.EnumValues) w.WriteStringValue(v); w.WriteEndArray(); }
        w.WriteStartArray("capabilities");
        foreach (var cap in CapabilityNames(c.Capabilities)) w.WriteStringValue(cap);
        w.WriteEndArray();
        if (c.Can(ColumnCapabilities.Filter)) Names(w, "operators", c.Operators);
        w.WriteEndObject();
    }

    public static IEnumerable<string> CapabilityNames(ColumnCapabilities caps) =>
        Enum.GetValues<ColumnCapabilities>().Where(c => c != ColumnCapabilities.None && caps.HasFlag(c)).Select(c => c.ToString().ToLowerInvariant());

    private static void Names(Utf8JsonWriter w, string name, IEnumerable<QueryOperator> ops)
    {
        w.WriteStartArray(name);
        foreach (var op in ops.Order()) w.WriteStringValue(QueryOperators.Name(op));
        w.WriteEndArray();
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
