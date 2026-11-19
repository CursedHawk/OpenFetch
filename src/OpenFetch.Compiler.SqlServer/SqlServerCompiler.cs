using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenFetch.Compiler.SqlServer;

/// <summary>
/// SQL Server dialect (§30.2). Identifiers are bracket-quoted. IN lists are one JSON parameter expanded with
/// <c>OPENJSON ... WITH (v &lt;store type&gt;)</c>, so the SQL text does not depend on list length and the 2100-parameter
/// limit is never approached. NULL ordering is emulated to match PostgreSQL (ASC NULLS LAST, DESC NULLS FIRST).
/// RLS values are set with <c>sp_set_session_context</c>; SqlClient's pooled-connection reset clears them.
/// </summary>
public sealed partial class SqlServerCompiler : SqlCompiler
{
    public override string ProviderName => "sqlserver";
    public override string CompilerVersion => "1";
    protected override int MaximumParameters => 2_100;
    protected override string True => "1 = 1";
    protected override string False => "1 = 0";
    protected override string LikeWildcards => "%_[";
    protected override string CountFunction => "COUNT_BIG";
    protected override string? EmptyOrderBy => "(SELECT NULL)"; // OFFSET/FETCH requires ORDER BY
    protected override string AverageDecimalType => "decimal(38,10)";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SettingKey();


    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    protected override void In(SqlBuilder b, ResolvedField field, ImmutableArray<object> values, bool negate)
    {
        var c = field.Column;
        var storeType = LosslessType(c.Physical.StoreType)
            ?? throw QueryErrors.UnsupportedFeature($"Field '{c.PublicName}' cannot be used with in/notIn on this provider.");
        var provider = values.Select(v => SqlBuilder.ToProvider(c, v)).ToArray();
        if (storeType.StartsWith("decimal", StringComparison.Ordinal) && provider.Any(v => v is decimal d && !FitsDecimal38_18(d)))
            throw QueryErrors.InvalidValueType($"Values for '{c.PublicName}' must have at most 20 integer and 18 fractional digits.", "/where");
        var json = JsonSerializer.Serialize(provider.Select(Json).ToArray());
        b.Column(field);
        b.Sql.Append(negate ? " NOT IN (SELECT v FROM OPENJSON(" : " IN (SELECT v FROM OPENJSON(")
             .Append(b.Parameter(json)).Append(") WITH (v ").Append(storeType).Append(" '$'))");
    }

    /// <summary>
    /// OPENJSON target type that can hold every client value without truncation or rounding, so the comparison against
    /// the column happens on the real value (never on a value shortened to the column's declared length/precision).
    /// Only a fixed whitelist of base types is accepted; nothing from metadata is spliced in verbatim.
    /// </summary>
    internal static string? LosslessType(string? storeType)
    {
        var name = storeType?.Split('(')[0].Trim().ToLowerInvariant();
        return name switch
        {
            "char" or "varchar" => "varchar(max)",
            "nchar" or "nvarchar" => "nvarchar(max)",
            "decimal" or "numeric" or "money" or "smallmoney" => "decimal(38,18)",
            "datetime" or "datetime2" or "smalldatetime" => "datetime2(7)",
            "datetimeoffset" => "datetimeoffset(7)",
            "time" => "time(7)",
            "float" or "real" => "float",
            "date" or "int" or "bigint" or "smallint" or "tinyint" or "bit" or "uniqueidentifier" => name,
            _ => null,
        };
    }

    private static bool FitsDecimal38_18(decimal d) => d.Scale <= 18 && Math.Abs(decimal.Truncate(d)) < 100_000_000_000_000_000_000m;

    private static object? Json(object? value) => value switch
    {
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        _ => value,
    };

    protected override void OrderTerm(SqlBuilder b, ResolvedOrder o, string? distinctAlias)
    {
        var asc = o.Direction == SortDirection.Ascending;
        if (distinctAlias is not null)
        {
            // ponytail: DISTINCT only allows select-list items in ORDER BY, so NULL placement uses the native order here.
            b.Sql.Append(distinctAlias).Append(asc ? " ASC" : " DESC");
            return;
        }
        if (o.Field.Column.Nullable)
        {
            b.Sql.Append("CASE WHEN ");
            b.Column(o.Field);
            b.Sql.Append(asc ? " IS NULL THEN 1 ELSE 0 END, " : " IS NULL THEN 0 ELSE 1 END, ");
        }
        b.Column(o.Field);
        b.Sql.Append(asc ? " ASC" : " DESC");
    }

    protected override void Paging(SqlBuilder b, int limit, int offset) =>
        b.Sql.Append("\nOFFSET ").Append(b.Parameter(offset)).Append(" ROWS FETCH NEXT ").Append(b.Parameter(limit)).Append(" ROWS ONLY");

    public override CompiledCommand CompileSessionSetup(IReadOnlyList<KeyValuePair<string, string?>> settings)
    {
        var b = new SqlBuilder(this);
        for (var i = 0; i < settings.Count; i++)
        {
            var (key, value) = settings[i];
            if (!SettingKey().IsMatch(key)) throw new InvalidOperationException($"Invalid RLS setting key '{key}'.");
            if (i > 0) b.Sql.Append(";\n");
            b.Sql.Append("EXEC sp_set_session_context @key = ").Append(b.Parameter("openfetch." + key))
                 .Append(", @value = ").Append(b.Parameter(value ?? "")).Append(", @read_only = 0");
        }
        return new CompiledCommand(b.Sql.ToString(), [.. b.Parameters]);
    }

    /// <summary>Clears every key set by <see cref="CompileSessionSetup"/> (NULL value) once the query is done.</summary>
    public override CompiledCommand CompileSessionTeardown(IReadOnlyList<KeyValuePair<string, string?>> settings)
    {
        var b = new SqlBuilder(this);
        for (var i = 0; i < settings.Count; i++)
        {
            if (i > 0) b.Sql.Append(";\n");
            b.Sql.Append("EXEC sp_set_session_context @key = ").Append(b.Parameter("openfetch." + settings[i].Key)).Append(", @value = NULL");
        }
        return new CompiledCommand(b.Sql.ToString(), [.. b.Parameters]);
    }
}

public static class SqlServerOptionsExtensions
{
    public static OpenFetchOptions UseSqlServer(this OpenFetchOptions options)
    {
        options.Compiler = new SqlServerCompiler();
        return options;
    }
}
