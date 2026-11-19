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

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SettingKey();

    [GeneratedRegex(@"^[a-zA-Z0-9]+(\s*\(\s*(\d+|max)\s*(,\s*\d+\s*)?\))?$")]
    private static partial Regex StoreType();

    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    protected override void In(SqlBuilder b, ResolvedField field, ImmutableArray<object> values, bool negate)
    {
        var c = field.Column;
        // The store type comes from EF metadata (trusted) and is still shape-checked before it is spliced in.
        var storeType = c.Physical.StoreType is { } t && StoreType().IsMatch(t) ? t
            : throw QueryErrors.UnsupportedFeature($"Field '{c.PublicName}' cannot be used with in/notIn on this provider.");
        var json = JsonSerializer.Serialize(values.Select(v => Json(SqlBuilder.ToProvider(c, v))).ToArray());
        b.Column(field);
        b.Sql.Append(negate ? " NOT IN (SELECT v FROM OPENJSON(" : " IN (SELECT v FROM OPENJSON(")
             .Append(b.Parameter(json)).Append(") WITH (v ").Append(storeType).Append(" '$'))");
    }

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
}

public static class SqlServerOptionsExtensions
{
    public static OpenFetchOptions UseSqlServer(this OpenFetchOptions options)
    {
        options.Compiler = new SqlServerCompiler();
        return options;
    }
}
