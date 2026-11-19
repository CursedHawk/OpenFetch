using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenFetch;

/// <summary>
/// Converts native JSON values to a column's CLR type using trusted metadata (§12). Profile (docs/spec-profile.md):
/// date-time = RFC 3339 with mandatory offset (normalized to UTC), date = yyyy-MM-dd, time = HH:mm:ss[.fffffff],
/// guid = 36-char hyphenated, enum = camelCase member name. Strings are never interpreted as field references.
/// </summary>
public static partial class ValueCoercion
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339();

    private static readonly string[] TimeFormats = ["HH:mm:ss", "HH:mm:ss.FFFFFFF"];

    public static object Coerce(QueryValue value, ColumnDescriptor column, string path)
    {
        if (value is ClrValue clr) return FromClr(clr.Value, column, path);
        return column.Kind switch
        {
            ScalarKind.String when value is StringValue s => s.Value,
            ScalarKind.Boolean when value is BooleanValue b => b.Value,
            ScalarKind.Integer when value is NumberValue n && long.TryParse(n.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) => Integer(l, column, path),
            ScalarKind.Decimal when value is NumberValue n && decimal.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            ScalarKind.Number when value is NumberValue n && double.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => Real(d, column),
            ScalarKind.Guid when value is StringValue s && Guid.TryParseExact(s.Value, "D", out var g) => g,
            ScalarKind.DateTime when value is StringValue s && Rfc3339().IsMatch(s.Value)
                && DateTimeOffset.TryParse(s.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) => DateTime(dto.ToUniversalTime(), column),
            ScalarKind.Date when value is StringValue s && DateOnly.TryParseExact(s.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date,
            ScalarKind.Time when value is StringValue s && TimeOnly.TryParseExact(s.Value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) => time,
            ScalarKind.Enum when value is StringValue s && column.EnumValues.IndexOf(s.Value) is >= 0 and var i => Enum.GetValues(column.ClrType).GetValue(i)!,
            _ => throw Invalid(column, path),
        };
    }

    /// <summary>Reads a JSON element (cursor payloads) as a QueryValue.</summary>
    public static QueryValue FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => new StringValue(e.GetString()!),
        JsonValueKind.Number => new NumberValue(e.GetRawText()),
        JsonValueKind.True => new BooleanValue(true),
        JsonValueKind.False => new BooleanValue(false),
        _ => QueryValue.Null,
    };

    private static object Integer(long value, ColumnDescriptor column, string path)
    {
        try { return Convert.ChangeType(value, column.ClrType, CultureInfo.InvariantCulture); }
        catch (OverflowException) { throw QueryErrors.InvalidValueType($"Value is out of range for field '{column.PublicName}'.", path); }
    }

    private static object Real(double value, ColumnDescriptor column) => column.ClrType == typeof(float) ? (float)value : value;

    private static object DateTime(DateTimeOffset utc, ColumnDescriptor column) => column.ClrType == typeof(DateTimeOffset) ? utc : utc.UtcDateTime;

    /// <summary>Trusted server values: exact type, enum from integral, or a string parsed with the public profile.</summary>
    private static object FromClr(object value, ColumnDescriptor column, string path)
    {
        var type = column.ClrType;
        if (type.IsInstanceOfType(value)) return value is DateTimeOffset dto ? dto.ToUniversalTime() : value;
        if (type.IsEnum && value is IConvertible && value.GetType().IsPrimitive) return Enum.ToObject(type, value);
        if (value is string s && column.Kind != ScalarKind.String) return Coerce(column.Kind == ScalarKind.Enum ? new StringValue(s) : Textual(s, column), column, path);
        if (column.Kind is ScalarKind.Integer or ScalarKind.Decimal or ScalarKind.Number && value is IConvertible)
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        throw new InvalidOperationException($"Trusted value of type {value.GetType().Name} cannot be used with column '{column.PropertyName}'.");
    }

    private static QueryValue Textual(string s, ColumnDescriptor column) =>
        column.Kind is ScalarKind.Integer or ScalarKind.Decimal or ScalarKind.Number ? new NumberValue(s)
        : column.Kind == ScalarKind.Boolean ? new BooleanValue(bool.Parse(s))
        : new StringValue(s);

    private static OpenFetchException Invalid(ColumnDescriptor column, string path) =>
        QueryErrors.InvalidValueType($"Value is not a valid {QueryOperators.TypeName(column.Kind)} for field '{column.PublicName}'.", path)
            .With("field", column.PublicName).With("expectedType", QueryOperators.TypeName(column.Kind));
}
