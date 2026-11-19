using System.Collections.Immutable;
using System.Text.Json;

namespace OpenFetch;

/// <summary>
/// Strict JSON -> canonical AST parser (§11-15). Unknown and duplicate properties are rejected; every error carries a JSON Pointer.
/// Recursion is bounded by <see cref="QueryLimits.MaximumFilterDepth"/> and the reader's MaxDepth, so hostile nesting cannot overflow the stack.
/// </summary>
public static class QueryParser
{
    public const string Version = "1.0";

    private static readonly string[] RootProperties = ["version", "select", "where", "orderBy", "page", "distinct"];

    public static QueryDocument Parse(ReadOnlyMemory<byte> utf8, QueryLimits limits)
    {
        if (utf8.Length > limits.MaximumDocumentBytes) throw QueryErrors.DocumentTooLarge(limits.MaximumDocumentBytes);
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                MaxDepth = limits.MaximumFilterDepth * 2 + 8,
                AllowDuplicateProperties = false,
            });
        }
        catch (JsonException ex)
        {
            throw QueryErrors.InvalidJson(ex.Message.Split(" Path:")[0].Split(" LineNumber:")[0]);
        }
        using (json) return new Parser(limits).Document(json.RootElement);
    }

    public static QueryDocument Parse(string json, QueryLimits limits) => Parse(System.Text.Encoding.UTF8.GetBytes(json), limits);

    private sealed class Parser(QueryLimits limits)
    {
        public QueryDocument Document(JsonElement root)
        {
            Object(root, "", RootProperties);
            var version = Version;
            if (root.TryGetProperty("version", out var v))
            {
                version = String(v, "/version");
                if (version != QueryParser.Version) throw QueryErrors.UnsupportedVersion(version, "/version");
            }

            if (!root.TryGetProperty("select", out var select)) throw QueryErrors.InvalidQuery("'select' is required.", "/select");
            var fields = Array(select, "/select", "a non-empty array of field names")
                .Select((e, i) => new FieldRef(String(e, $"/select/{i}"), $"/select/{i}")).ToImmutableArray();
            if (fields.IsEmpty) throw QueryErrors.InvalidQuery("'select' must contain at least one field.", "/select");

            var where = root.TryGetProperty("where", out var w) && w.ValueKind != JsonValueKind.Null ? Filter(w, "/where", 1) : null;

            var order = ImmutableArray<OrderNode>.Empty;
            if (root.TryGetProperty("orderBy", out var o))
                order = [.. Array(o, "/orderBy", "an array").Select((e, i) => Order(e, $"/orderBy/{i}"))];

            var page = new PageNode(null, null, null);
            if (root.TryGetProperty("page", out var p) && p.ValueKind != JsonValueKind.Null) page = Page(p);

            var distinct = false;
            if (root.TryGetProperty("distinct", out var d))
                distinct = d.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw QueryErrors.InvalidQuery("'distinct' must be a boolean.", "/distinct"),
                };

            return new QueryDocument(version, fields, where, order, page, distinct);
        }

        private static OrderNode Order(JsonElement e, string path)
        {
            Object(e, path, ["field", "direction"]);
            var field = Required(e, "field", path);
            var direction = SortDirection.Ascending;
            if (e.TryGetProperty("direction", out var dir))
                direction = String(dir, path + "/direction") switch
                {
                    "asc" => SortDirection.Ascending,
                    "desc" => SortDirection.Descending,
                    _ => throw QueryErrors.InvalidQuery("'direction' must be 'asc' or 'desc'.", path + "/direction"),
                };
            return new OrderNode(new FieldRef(String(field, path + "/field"), path + "/field"), direction);
        }

        private static PageNode Page(JsonElement e)
        {
            Object(e, "/page", ["first", "after", "offset"]);
            int? first = e.TryGetProperty("first", out var f) && f.ValueKind != JsonValueKind.Null ? Integer(f, "/page/first") : null;
            string? after = e.TryGetProperty("after", out var a) && a.ValueKind != JsonValueKind.Null ? String(a, "/page/after") : null;
            int? offset = e.TryGetProperty("offset", out var o) && o.ValueKind != JsonValueKind.Null ? Integer(o, "/page/offset") : null;
            if (after is not null && offset is not null) throw QueryErrors.InvalidQuery("'after' and 'offset' cannot be combined.", "/page");
            return new PageNode(first, after, offset);
        }

        private FilterNode Filter(JsonElement e, string path, int depth)
        {
            if (depth > limits.MaximumFilterDepth)
                throw QueryErrors.TooComplex($"Filter nesting exceeds the maximum depth of {limits.MaximumFilterDepth}.", path);
            if (e.ValueKind != JsonValueKind.Object) throw QueryErrors.InvalidQuery("A filter must be an object.", path);
            var type = String(Required(e, "type", path), path + "/type");
            return type switch
            {
                "comparison" => Comparison(e, path),
                "logical" => Logical(e, path, depth),
                "not" => Not(e, path, depth),
                _ => throw QueryErrors.InvalidQuery("'type' must be 'comparison', 'logical' or 'not'.", path + "/type"),
            };
        }

        private FilterNode Logical(JsonElement e, string path, int depth)
        {
            Object(e, path, ["type", "operator", "children"]);
            var op = String(Required(e, "operator", path), path + "/operator") switch
            {
                "and" => LogicalOperator.And,
                "or" => LogicalOperator.Or,
                _ => throw QueryErrors.InvalidQuery("Logical 'operator' must be 'and' or 'or'.", path + "/operator"),
            };
            var children = Array(Required(e, "children", path), path + "/children", "a non-empty array of filters")
                .Select((c, i) => Filter(c, $"{path}/children/{i}", depth + 1)).ToImmutableArray();
            if (children.IsEmpty) throw QueryErrors.InvalidQuery("'children' must contain at least one filter.", path + "/children");
            return new LogicalFilter(op, children) { Pointer = path };
        }

        private FilterNode Not(JsonElement e, string path, int depth)
        {
            Object(e, path, ["type", "operand"]);
            return new NotFilter(Filter(Required(e, "operand", path), path + "/operand", depth + 1)) { Pointer = path };
        }

        private static FilterNode Comparison(JsonElement e, string path)
        {
            Object(e, path, ["type", "field", "operator", "value", "compareTo"]);
            var field = new FieldRef(String(Required(e, "field", path), path + "/field"), path + "/field");
            var opName = String(Required(e, "operator", path), path + "/operator");
            if (!QueryOperators.TryParse(opName, out var op))
                throw new OpenFetchException("unsupported_operator", 400, "Invalid query operator", $"Operator '{opName}' is not defined.", path + "/operator");

            var hasValue = e.TryGetProperty("value", out var value);
            var hasCompare = e.TryGetProperty("compareTo", out var compare);
            QueryOperand right;
            if (hasValue && hasCompare) throw QueryErrors.InvalidQuery("'value' and 'compareTo' cannot be combined.", path);
            if (hasCompare)
            {
                if (!QueryOperators.Comparison.Contains(op))
                    throw QueryErrors.InvalidQuery($"'compareTo' is only valid with comparison operators.", path + "/compareTo");
                Object(compare, path + "/compareTo", ["field"]);
                right = new FieldOperand(new FieldRef(String(Required(compare, "field", path + "/compareTo"), path + "/compareTo/field"), path + "/compareTo/field"));
            }
            else if (!hasValue) throw QueryErrors.InvalidQuery("'value' is required.", path + "/value");
            else right = Operand(op, value, path + "/value");
            return new ComparisonFilter(field, op, right) { Pointer = path };
        }

        /// <summary>Arity rules (§14): in/notIn take a non-empty array, between/notBetween exactly two, null only with eq/ne.</summary>
        private static QueryOperand Operand(QueryOperator op, JsonElement value, string path)
        {
            switch (op)
            {
                case QueryOperator.In or QueryOperator.NotIn:
                {
                    var items = Array(value, path, "a non-empty array");
                    if (items.Length == 0) throw QueryErrors.InvalidValueType("The array must not be empty.", path);
                    return new ListOperand([.. items.Select((x, i) => Scalar(x, $"{path}/{i}", allowNull: false))]);
                }
                case QueryOperator.Between or QueryOperator.NotBetween:
                {
                    var items = Array(value, path, "a two-element array [low, high]");
                    if (items.Length != 2) throw QueryErrors.InvalidValueType("Range operators take exactly two values [low, high].", path);
                    return new ListOperand([.. items.Select((x, i) => Scalar(x, $"{path}/{i}", allowNull: false))]);
                }
                default:
                    return new ConstantOperand(Scalar(value, path, allowNull: op is QueryOperator.Equal or QueryOperator.NotEqual));
            }
        }

        private static QueryValue Scalar(JsonElement e, string path, bool allowNull) => e.ValueKind switch
        {
            JsonValueKind.String => new StringValue(e.GetString()!),
            JsonValueKind.Number => new NumberValue(e.GetRawText()),
            JsonValueKind.True => new BooleanValue(true),
            JsonValueKind.False => new BooleanValue(false),
            JsonValueKind.Null when allowNull => QueryValue.Null,
            JsonValueKind.Null => throw QueryErrors.InvalidValueType("null is only allowed with 'eq' and 'ne'.", path),
            _ => throw QueryErrors.InvalidValueType("Expected a JSON scalar value.", path),
        };

        private static void Object(JsonElement e, string path, string[] allowed)
        {
            if (e.ValueKind != JsonValueKind.Object) throw QueryErrors.InvalidQuery("Expected a JSON object.", path is "" ? "/" : path);
            foreach (var p in e.EnumerateObject())
                if (System.Array.IndexOf(allowed, p.Name) < 0)
                    throw QueryErrors.InvalidQuery($"Unknown property '{p.Name}'.", $"{path}/{Escape(p.Name)}");
        }

        private static JsonElement Required(JsonElement e, string name, string path) =>
            e.TryGetProperty(name, out var v) ? v : throw QueryErrors.InvalidQuery($"'{name}' is required.", $"{path}/{name}");

        private static string String(JsonElement e, string path) =>
            e.ValueKind == JsonValueKind.String ? e.GetString()! : throw QueryErrors.InvalidQuery("Expected a string.", path);

        private static int Integer(JsonElement e, string path) =>
            e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : throw QueryErrors.InvalidQuery("Expected an integer.", path);

        private static JsonElement[] Array(JsonElement e, string path, string expected) =>
            e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray()] : throw QueryErrors.InvalidQuery($"Expected {expected}.", path);

        private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");
    }
}

/// <summary>Deterministic JSON dump of the AST (conformance <c>expected-ast</c>). Pointers are omitted.</summary>
public static class AstWriter
{
    public static string ToJson(QueryDocument doc)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", doc.Version);
            w.WriteStartArray("select");
            foreach (var f in doc.Select) w.WriteStringValue(f.Path);
            w.WriteEndArray();
            w.WritePropertyName("where");
            Filter(w, doc.Where);
            w.WriteStartArray("orderBy");
            foreach (var o in doc.OrderBy) w.WriteStringValue($"{o.Field.Path} {(o.Direction == SortDirection.Ascending ? "asc" : "desc")}");
            w.WriteEndArray();
            w.WriteStartObject("page");
            if (doc.Page.First is { } first) w.WriteNumber("first", first);
            if (doc.Page.After is { } after) w.WriteString("after", after);
            if (doc.Page.Offset is { } offset) w.WriteNumber("offset", offset);
            w.WriteEndObject();
            w.WriteBoolean("distinct", doc.Distinct);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Filter(Utf8JsonWriter w, FilterNode? node)
    {
        switch (node)
        {
            case null: w.WriteNullValue(); break;
            case LogicalFilter l:
                w.WriteStartObject();
                w.WriteString("logical", l.Operator == LogicalOperator.And ? "and" : "or");
                w.WriteStartArray("children");
                foreach (var c in l.Children) Filter(w, c);
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case NotFilter n:
                w.WriteStartObject();
                w.WritePropertyName("not");
                Filter(w, n.Operand);
                w.WriteEndObject();
                break;
            case ComparisonFilter c:
                w.WriteStartObject();
                w.WriteString("field", c.Left.Path);
                w.WriteString("operator", QueryOperators.Name(c.Operator));
                switch (c.Right)
                {
                    case ConstantOperand k: w.WritePropertyName("value"); Value(w, k.Value); break;
                    case ListOperand l:
                        w.WriteStartArray("values");
                        foreach (var v in l.Values) Value(w, v);
                        w.WriteEndArray();
                        break;
                    case FieldOperand f: w.WriteString("compareTo", f.Field.Path); break;
                }
                w.WriteEndObject();
                break;
        }
    }

    private static void Value(Utf8JsonWriter w, QueryValue v)
    {
        switch (v)
        {
            case NullValue: w.WriteStringValue("null:"); break;
            case StringValue s: w.WriteStringValue("string:" + s.Value); break;
            case NumberValue n: w.WriteStringValue("number:" + n.Text); break;
            case BooleanValue b: w.WriteStringValue("boolean:" + (b.Value ? "true" : "false")); break;
            case ClrValue c: w.WriteStringValue("clr:" + c.Value); break;
        }
    }
}
