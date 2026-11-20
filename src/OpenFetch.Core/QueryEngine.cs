using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenFetch;

/// <summary>Authenticated, opaque cursor encryption (D4: IDataProtector in ASP.NET Core). Unprotect throws on tampering.</summary>
public interface ICursorProtector
{
    string Protect(byte[] payload);
    byte[] Unprotect(string token);
}

public sealed record PreparedQuery(ResolvedQuery Query, QueryPlan Plan, string ShapeHash, string Scope, int Complexity)
{
    /// <summary>Registered extension properties sent by the client (raw JSON), for application use.</summary>
    public IReadOnlyDictionary<string, string> Extensions { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The §5 pipeline: parse -> resolve/validate -> authorize -> row-policy injection -> complexity -> shape key -> compile -> execute.
/// Stateless and thread-safe; one instance per application.
/// </summary>
public sealed class QueryEngine(
    QueryModel model,
    IQueryCompiler compiler,
    ICursorProtector cursors,
    SecurityOptions security,
    IEnumerable<IRowPolicyProvider>? policyProviders = null,
    TimeProvider? time = null)
{
    public static readonly ActivitySource ActivitySource = new("OpenFetch");
    private static readonly Meter Meter = new("OpenFetch");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("openfetch.query.duration", "ms");
    private static readonly Counter<long> Rows = Meter.CreateCounter<long>("openfetch.query.rows");
    private static readonly Histogram<long> Bytes = Meter.CreateHistogram<long>("openfetch.response.bytes", "By");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("openfetch.query.errors");

    private readonly ImmutableArray<IRowPolicyProvider> _providers = [.. policyProviders ?? []];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public QueryModel Model => model;
    public IQueryCompiler Compiler => compiler;

    public async ValueTask<PreparedQuery> PrepareAsync(string dataset, ReadOnlyMemory<byte> body, QuerySecurityContext context, CancellationToken ct, string? projection = null)
    {
        // Unknown and inaccessible datasets (and projections) produce the identical error (§16.2).
        if (!model.Datasets.TryGetValue(dataset, out var root) || root.Internal || !await context.IsAuthorizedAsync(root.Policies, ct))
            throw QueryErrors.UnknownDataset(projection is null ? dataset : dataset + "/" + projection);
        var fixedFields = ImmutableArray<string>.Empty;
        if (projection is not null && !root.Projections.TryGetValue(projection, out fixedFields))
            throw QueryErrors.UnknownDataset(dataset + "/" + projection);

        var doc = QueryParser.Parse(body, model.Limits, fixedSelect: projection is not null, model.Features.Extensions);
        if (projection is not null) doc = WithProjection(doc, fixedFields);
        var allowed = await AllowedPoliciesAsync(context, ReachablePolicies(root), ct);
        ResolvedQuery resolved;
        try { resolved = new QueryResolver(model, root, p => p.All(allowed.Contains)).Resolve(doc); }
        catch (OpenFetchException ex) when (projection is not null && ex.Path?.StartsWith("/select", StringComparison.Ordinal) == true)
        {
            // A projection the caller cannot fully use is indistinguishable from a missing one; server field names are never echoed.
            throw QueryErrors.UnknownDataset(dataset + "/" + projection);
        }
        var secured = await SecureAsync(resolved, context, ct);

        var complexity = Complexity.Score(secured);
        if (complexity > model.Limits.MaximumComplexity)
            throw QueryErrors.TooComplex($"Query complexity {complexity} exceeds the maximum of {model.Limits.MaximumComplexity}.")
                .With("complexity", complexity).With("maxComplexity", model.Limits.MaximumComplexity);

        var shape = Keys.Shape(secured, compiler, model.Version);
        var scope = Keys.Scope(context, secured);
        if (doc.Page.After is { } after) secured = secured with { After = DecodeCursor(after, secured, shape, scope) };

        var plan = compiler.Compile(secured);
        return new PreparedQuery(secured, plan, shape, scope, complexity) { Extensions = doc.Extensions };
    }

    internal static QueryDocument WithProjection(QueryDocument doc, ImmutableArray<string> fields) =>
        doc with { Select = [.. fields.Select((f, i) => new FieldRef(f, $"/select/{i}"))] };

    /// <summary>Evaluates each named policy once; result is the set the caller satisfies.</summary>
    public static async ValueTask<HashSet<string>> AllowedPoliciesAsync(QuerySecurityContext context, IEnumerable<string> policies, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in policies.Distinct())
            if (await context.IsAuthorizedAsync(p, ct)) allowed.Add(p);
        return allowed;
    }

    private IEnumerable<string> ReachablePolicies(DatasetDescriptor root)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<DatasetDescriptor>([root]);
        while (queue.TryDequeue(out var d))
        {
            if (!seen.Add(d.PublicName)) continue;
            foreach (var p in d.Policies.Concat(d.Columns.Values.SelectMany(c => c.Policies)).Concat(d.Relationships.Values.SelectMany(r => r.Policies)))
                yield return p;
            foreach (var r in d.Relationships.Values) queue.Enqueue(model.Datasets[r.TargetDataset]);
        }
    }

    /// <summary>Injects trusted predicates (§16.4): root policy outside the client filter, joined-dataset policies in ON clauses.</summary>
    private async ValueTask<ResolvedQuery> SecureAsync(ResolvedQuery q, QuerySecurityContext context, CancellationToken ct)
    {
        var joins = ImmutableArray.CreateBuilder<ResolvedJoin>();
        foreach (var j in q.Joins) joins.Add(j with { Policy = await PolicyAsync(j.Target, j.Alias, context, ct) });
        var expansions = ImmutableArray.CreateBuilder<ResolvedExpansion>();
        foreach (var e in q.Expansions)
            expansions.Add(e with { Policy = await PolicyAsync(e.Target, e.OrderBy[0].Field.Alias, context, ct), Where = await SecureFilterAsync(e.Where, context, ct), Secured = true });
        return q with
        {
            Policy = await PolicyAsync(q.Dataset, "t0", context, ct),
            Joins = joins.ToImmutable(),
            Where = await SecureFilterAsync(q.Where, context, ct),
            Having = await SecureFilterAsync(q.Having, context, ct), // the resolver already rejects 'any' here; defense in depth
            Expansions = expansions.ToImmutable(),
            Secured = true,
        };
    }

    /// <summary>Puts each related dataset's trusted policy inside its EXISTS subquery (nested 'any' included).</summary>
    private async ValueTask<ResolvedFilter?> SecureFilterAsync(ResolvedFilter? f, QuerySecurityContext context, CancellationToken ct)
    {
        switch (f)
        {
            case ResolvedLogical l:
                var children = ImmutableArray.CreateBuilder<ResolvedFilter>();
                foreach (var c in l.Children) children.Add((await SecureFilterAsync(c, context, ct))!);
                return l with { Children = children.ToImmutable() };
            case ResolvedNot n:
                return n with { Operand = (await SecureFilterAsync(n.Operand, context, ct))! };
            case ResolvedExists x:
                return x with { Policy = await PolicyAsync(x.Target, x.Alias, context, ct), Where = await SecureFilterAsync(x.Where, context, ct), Secured = true };
            default:
                return f;
        }
    }

    private async ValueTask<ResolvedFilter?> PolicyAsync(DatasetDescriptor dataset, string alias, QuerySecurityContext context, CancellationToken ct)
    {
        var nodes = new List<FilterNode>();
        foreach (var policy in dataset.RowPolicies)
        {
            var instance = policy.ContextType == typeof(object) ? context
                : policy.ContextType == typeof(QuerySecurityContext) ? context
                : context.Services.GetService(policy.ContextType)
                  ?? throw new InvalidOperationException($"Row-policy context {policy.ContextType.Name} is not registered in DI.");
            nodes.Add(policy.Build(instance));
        }
        foreach (var provider in _providers)
            if (await provider.BuildPolicyAsync(context, dataset, ct) is { } node) nodes.Add(node);
        return nodes.Count switch
        {
            0 => null,
            1 => QueryResolver.Trusted(nodes[0], dataset, alias),
            _ => new ResolvedLogical(LogicalOperator.And, [.. nodes.Select(n => QueryResolver.Trusted(n, dataset, alias))]),
        };
    }

    // ---- Cursors (§20.1): bind ordering values, shape, model version, expiry and caller/policy scope. ----

    private string EncodeCursor(PreparedQuery p, object?[] row, ImmutableArray<int> orderIndexes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("m", model.Version);
            w.WriteString("s", p.ShapeHash);
            w.WriteString("c", p.Scope);
            w.WriteNumber("x", _time.GetUtcNow().Add(model.Limits.CursorLifetime).ToUnixTimeSeconds());
            w.WriteStartArray("k");
            for (var i = 0; i < orderIndexes.Length; i++) ValueWriter.Write(w, row[orderIndexes[i]], p.Query.OrderBy[i].Field.Column);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return cursors.Protect(buffer.WrittenSpan.ToArray());
    }

    private ImmutableArray<object?> DecodeCursor(string token, ResolvedQuery q, string shape, string scope)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(cursors.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException) { throw QueryErrors.InvalidCursor(); }
        using (doc)
        {
            try
            {
                var r = doc.RootElement;
                if (DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("x").GetInt64()) < _time.GetUtcNow()) throw QueryErrors.InvalidCursor();
                if (r.GetProperty("m").GetString() != model.Version || r.GetProperty("s").GetString() != shape || r.GetProperty("c").GetString() != scope)
                    throw QueryErrors.CursorScopeMismatch();
                var values = r.GetProperty("k").EnumerateArray().ToArray();
                if (values.Length != q.OrderBy.Length) throw QueryErrors.InvalidCursor();
                return [.. values.Select((v, i) => (object?)ValueCoercion.Coerce(ValueCoercion.FromJson(v), q.OrderBy[i].Field.Column, "/page/after"))];
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { throw QueryErrors.InvalidCursor(); }
            catch (OpenFetchException ex) when (ex.Code == "invalid_value_type") { throw QueryErrors.InvalidCursor(); }
        }
    }

    // ---- Execution (§19.3) ----

    /// <summary>
    /// Runs the plan in one read-only transaction on one connection: session setup (RLS settings) then the query (§16.5).
    /// The response is buffered up to MaximumResponseBytes so a limit breach still yields a clean error status.
    /// </summary>
    // ponytail: buffer-then-flush caps memory at MaximumResponseBytes per request; true streaming needs a mid-stream abort protocol.
    public async Task<int> ExecuteAsync(DbConnection connection, PreparedQuery prepared, QuerySecurityContext context, IBufferWriter<byte> output, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("openfetch.query");
        activity?.SetTag("openfetch.dataset", prepared.Query.Dataset.PublicName);
        activity?.SetTag("openfetch.shape", prepared.ShapeHash);
        activity?.SetTag("openfetch.model_version", model.Version);
        activity?.SetTag("openfetch.compiler", $"{compiler.ProviderName}/{compiler.CompilerVersion}");
        activity?.SetTag("openfetch.complexity", prepared.Complexity);
        var tags = new TagList { { "openfetch.dataset", prepared.Query.Dataset.PublicName } };
        var started = Stopwatch.GetTimestamp();
        var opened = connection.State != ConnectionState.Open;
        var settings = security.DatabaseRowLevelSecurity ? security.DatabaseSessionValues!(context).ToList() : [];
        try
        {
            if (opened) await connection.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            var setupCommand = compiler.CompileSessionSetup(settings);
            if (setupCommand.CommandText.Length > 0)
                await using (var setup = Command(connection, tx, setupCommand))
                    await setup.ExecuteNonQueryAsync(ct);

            var columns = prepared.Plan.ResultColumns;
            var buffered = new List<object?[]>();
            var hasNext = false;
            await using (var command = Command(connection, tx, prepared.Plan.Command))
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
                while (await reader.ReadAsync(ct))
                {
                    if (buffered.Count == prepared.Query.PageSize) { hasNext = true; break; }
                    var row = new object?[columns.Length];
                    for (var i = 0; i < columns.Length; i++) row[i] = ValueReader.Read(reader, i, columns[i].Field.Column);
                    buffered.Add(row);
                }

            var expanded = new List<Dictionary<object, List<object?[]>>>();
            foreach (var e in prepared.Query.Expansions)
                expanded.Add(await ExpandAsync(connection, tx, e, columns, buffered, ct));

            var rows = Write(prepared, buffered, hasNext, expanded, output);

            await tx.CommitAsync(ct);
            Rows.Add(rows, tags);
            activity?.SetTag("openfetch.rows", rows);
            return rows;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            var timeout = Chain(ex).Any(e => e is TimeoutException) || ex is DbException { SqlState: "57014" };
            Failures.Add(1, [.. tags, new("openfetch.error", timeout ? "query_timeout" : "execution_failed")]);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw timeout ? QueryErrors.Timeout() : new QueryExecutionException(QueryErrors.ExecutionFailed(), ex);
        }
        finally
        {
            await TeardownAsync(connection, settings);
            if (opened) await connection.CloseAsync();
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        }
    }

    /// <summary>Clears session-scoped security values (SQL Server) so they never outlive the query on this connection.
    /// If clearing fails, the connection is closed: a pooled connection is reset before its next use.</summary>
    private async Task TeardownAsync(DbConnection connection, List<KeyValuePair<string, string?>> settings)
    {
        var teardown = compiler.CompileSessionTeardown(settings);
        if (teardown.CommandText.Length == 0 || connection.State != ConnectionState.Open) return;
        try
        {
            await using var command = Command(connection, null, teardown);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (DbException)
        {
            await connection.CloseAsync();
        }
    }

    private static IEnumerable<Exception> Chain(Exception? e) { for (; e is not null; e = e.InnerException) yield return e; }

    private DbCommand Command(DbConnection connection, DbTransaction? tx, CompiledCommand compiled)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = compiled.CommandText;
        command.CommandTimeout = (int)Math.Ceiling(model.Limits.CommandTimeout.TotalSeconds);
        foreach (var p in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = p.Name;
            parameter.Value = p.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    /// <summary>Second query for one expansion over all parent keys of the page (same transaction, same security context).</summary>
    private async Task<Dictionary<object, List<object?[]>>> ExpandAsync(DbConnection connection, DbTransaction tx, ResolvedExpansion e,
        ImmutableArray<ResultColumn> columns, List<object?[]> rows, CancellationToken ct)
    {
        var keyIndex = columns.ToList().FindIndex(c => c.Role == ResultRole.ExpandKey && c.Field.Path == e.ParentKey.Path);
        var keys = rows.Select(r => r[keyIndex]).OfType<object>().Distinct().ToList();
        var result = new Dictionary<object, List<object?[]>>();
        if (keys.Count == 0) return result;
        var budget = model.Limits.MaximumExpandedRows;
        var read = 0;
        await using var command = Command(connection, tx, compiler.CompileExpansion(e, keys));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            if (++read > budget) throw QueryErrors.ResponseTooLarge(model.Limits.MaximumResponseBytes);
            var key = ValueReader.Read(reader, 0, e.ForeignKey)!;
            var values = new object?[e.Select.Length];
            for (var i = 0; i < values.Length; i++) values[i] = ValueReader.Read(reader, i + 1, e.Select[i].Column);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(values);
        }
        return result;
    }

    private int Write(PreparedQuery p, List<object?[]> rows, bool hasNext, List<Dictionary<object, List<object?[]>>> expanded, IBufferWriter<byte> output)
    {
        var columns = p.Plan.ResultColumns;
        var tree = OutputTree.Build(columns);
        var expansions = p.Query.Expansions;
        var keyIndexes = expansions.Select(e => columns.ToList().FindIndex(c => c.Role == ResultRole.ExpandKey && c.Field.Path == e.ParentKey.Path)).ToArray();
        var orderIndexes = p.Query.CursorCapable
            ? p.Query.OrderBy.Select(o => columns.ToList().FindIndex(c => c.Role != ResultRole.JoinKey && ResultColumn.Same(c.Field, o.Field))).ToImmutableArray()
            : [];
        var max = model.Limits.MaximumResponseBytes;
        var counter = new CountingWriter(output);
        using var w = new Utf8JsonWriter(counter);
        var count = rows.Count;
        var last = rows.LastOrDefault();

        w.WriteStartObject();
        w.WriteStartArray("data");
        foreach (var row in rows)
        {
            tree.Write(w, row, expansions.IsEmpty ? null : writer =>
            {
                for (var x = 0; x < expansions.Length; x++)
                {
                    writer.WriteStartArray(expansions[x].Name);
                    if (row[keyIndexes[x]] is { } key && expanded[x].TryGetValue(key, out var children))
                        foreach (var child in children)
                        {
                            writer.WriteStartObject();
                            for (var i = 0; i < child.Length; i++)
                            {
                                writer.WritePropertyName(expansions[x].Select[i].Path);
                                ValueWriter.Write(writer, child[i], expansions[x].Select[i].Column);
                            }
                            writer.WriteEndObject();
                        }
                    writer.WriteEndArray();
                }
            });
            if (w.BytesCommitted + w.BytesPending > max) throw QueryErrors.ResponseTooLarge(max);
        }
        w.WriteEndArray();
        w.WriteStartObject("page");
        w.WriteNumber("count", count);
        w.WriteBoolean("hasNext", hasNext);
        if (p.Query.CursorCapable && last is not null && hasNext) w.WriteString("endCursor", EncodeCursor(p, last, orderIndexes));
        else w.WriteNull("endCursor");
        w.WriteEndObject();
        w.WriteStartObject("meta");
        w.WriteString("dataset", p.Query.Dataset.PublicName);
        w.WriteString("modelVersion", model.Version);
        w.WriteEndObject();
        w.WriteEndObject();
        w.Flush();
        if (counter.Written > max) throw QueryErrors.ResponseTooLarge(max);
        Bytes.Record(counter.Written, new TagList { { "openfetch.dataset", p.Query.Dataset.PublicName } });
        return count;
    }

    private sealed class CountingWriter(IBufferWriter<byte> inner) : IBufferWriter<byte>
    {
        public long Written { get; private set; }
        public void Advance(int count) { Written += count; inner.Advance(count); }
        public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }

    /// <summary>Writes result columns as nested JSON: <c>customer.name</c> becomes <c>{"customer":{"name":...}}</c>.</summary>
    private sealed class OutputTree
    {
        private readonly List<(string Name, int Index, OutputTree? Child)> _items = [];
        private int _keyIndex = -1;

        public static OutputTree Build(ImmutableArray<ResultColumn> columns)
        {
            var root = new OutputTree();
            for (var i = 0; i < columns.Length; i++)
            {
                var c = columns[i];
                if (c.Role is ResultRole.Cursor or ResultRole.ExpandKey) continue;
                var segments = c.Field.Path.Split('.');
                var node = root;
                var isKey = c.Role is ResultRole.JoinKey or ResultRole.OwnedKey;
                var depth = isKey ? segments.Length : segments.Length - 1;
                for (var s = 0; s < depth; s++)
                {
                    var existing = node._items.FirstOrDefault(x => x.Name == segments[s] && x.Child is not null);
                    if (existing.Child is null) node._items.Add(existing = (segments[s], -1, new OutputTree()));
                    node = existing.Child!;
                }
                if (isKey) node._keyIndex = i;
                else node._items.Add((segments[^1], i, null));
            }
            return root;
        }

        public void Write(Utf8JsonWriter w, object?[] row, Action<Utf8JsonWriter>? extra = null)
        {
            w.WriteStartObject();
            WriteBody(w, row);
            extra?.Invoke(w);
            w.WriteEndObject();
        }

        private void WriteBody(Utf8JsonWriter w, object?[] row)
        {
            foreach (var (name, index, child) in _items)
            {
                w.WritePropertyName(name);
                if (child is null) ValueWriter.Write(w, row[index], null);
                else if (child._keyIndex >= 0 && row[child._keyIndex] is null) w.WriteNullValue();
                else { w.WriteStartObject(); child.WriteBody(w, row); w.WriteEndObject(); }
            }
        }
    }
}

/// <summary>Wraps a provider failure as a public 500 while keeping the original for server-side logs only.</summary>
public sealed class QueryExecutionException(OpenFetchException error, Exception inner) : Exception(error.Message, inner)
{
    public OpenFetchException Error { get; } = error;
}

/// <summary>Reads provider values with typed getters, then applies the EF value converter (enums, custom conversions).</summary>
internal static class ValueReader
{
    private static readonly ConcurrentDictionary<Type, Func<DbDataReader, int, object>> Getters = new();

    public static object? Read(DbDataReader reader, int ordinal, ColumnDescriptor column)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var getter = Getters.GetOrAdd(column.Physical.ProviderClrType, static t =>
        {
            var method = typeof(ValueReader).GetMethod(nameof(Get), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(t);
            return method.CreateDelegate<Func<DbDataReader, int, object>>();
        });
        var value = getter(reader, ordinal);
        return column.Physical.FromProvider is { } convert ? convert(value) : value;
    }

    private static object Get<T>(DbDataReader reader, int ordinal) => reader.GetFieldValue<T>(ordinal)!;
}

/// <summary>Writes CLR values with the public JSON profile (RFC 3339 UTC date-times, camelCase enums, exact decimals).</summary>
public static class ValueWriter
{
    public static void Write(Utf8JsonWriter w, object? value, ColumnDescriptor? column)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case decimal d: w.WriteNumberValue(d); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            case double or float: w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            case Guid g: w.WriteStringValue(g); break;
            case DateTimeOffset dto: w.WriteStringValue(Rfc3339(dto.UtcDateTime)); break;
            case DateTime dt: w.WriteStringValue(Rfc3339(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt)); break;
            case DateOnly d: w.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case TimeOnly t: w.WriteStringValue(t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.')); break;
            case Enum e: w.WriteStringValue(Enum.GetName(e.GetType(), e) is { } n ? JsonNamingPolicy.CamelCase.ConvertName(n) : Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); break;
            case IConvertible c when value.GetType().IsPrimitive: w.WriteNumberValue(c.ToInt64(CultureInfo.InvariantCulture)); break;
            default: throw new InvalidOperationException($"No JSON mapping for {value.GetType().Name}.");
        }
    }

    private static string Rfc3339(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "Z";
}

/// <summary>Complexity scoring v1 (§22, docs/complexity-v1.md). Runs on the secured query, so policy conditions count too.</summary>
public static class Complexity
{
    public const int Version = 1;

    public static int Score(ResolvedQuery q)
    {
        var score = 1 + q.Select.Count(f => f.Aggregate is null) + q.Joins.Length * 15 + (q.Distinct ? 20 : 0);
        score += q.GroupBy.Length * 15 + q.Select.Count(f => f.Aggregate is not null) * 10;
        score += Filter(q.Where, client: true) + Filter(q.Having, client: true) + Filter(q.Policy, client: false) + q.Joins.Sum(j => Filter(j.Policy, client: false));
        score += q.Expansions.Sum(e => 30 + e.Select.Length + Filter(e.Where, client: true) + Filter(e.Policy, client: false));
        // Root key columns are served by the primary-key index (and are the appended tie-breaker), so they are never charged.
        // Grouped results are sorted after aggregation, so ordering adds no per-row index cost there.
        if (!q.IsAggregate)
            score += q.OrderBy.Count(o => !o.Field.Column.Indexed && !(o.Field.Alias == "t0" && q.Dataset.Key.Contains(o.Field.Column))) * 30;
        return score;
    }

    private static int Filter(ResolvedFilter? f, bool client) => f switch
    {
        null => 0,
        ResolvedLogical l => (l.Operator == LogicalOperator.Or ? 5 : 0) + l.Children.Sum(c => Filter(c, client)),
        ResolvedNot n => Filter(n.Operand, client),
        ResolvedExists x => 30 + Filter(x.Where, client) + Filter(x.Policy, client: false),
        ResolvedComparison c => 2
            + (c.Operator is QueryOperator.Contains or QueryOperator.EndsWith ? 25 : 0)
            + (client && c.Field.Aggregate is null && !c.Field.Column.Indexed ? 20 : 0),
        _ => 0,
    };
}

/// <summary>Shape and scope keys. Shape = structure without values (cursor binding, telemetry, v0.2 plan cache).</summary>
internal static class Keys
{
    public static string Shape(ResolvedQuery q, IQueryCompiler compiler, string modelVersion)
    {
        var sb = new StringBuilder();
        sb.Append(QueryParser.Version).Append('|').Append(compiler.ProviderName).Append('/').Append(compiler.CompilerVersion).Append('|').Append(modelVersion)
          .Append('|').Append(q.Dataset.PublicName).Append('|').Append(q.Distinct);
        foreach (var j in q.Joins) sb.Append("|j:").Append(j.Path);
        foreach (var p in q.OwnedPresence) sb.Append("|op:").Append(p.Path).Append('=').Append(string.Join(',', p.Presence.Select(c => c.PublicName)));
        foreach (var f in q.Select) sb.Append("|s:").Append(f.Path).Append(f.Aggregate is { } a ? $"={a.Function}({a.Argument?.Path})" : "");
        foreach (var g in q.GroupBy) sb.Append("|g:").Append(g.Path);
        sb.Append("|h:"); Filter(sb, q.Having);
        sb.Append("|w:"); Filter(sb, q.Where);
        sb.Append("|p:"); Filter(sb, q.Policy);
        foreach (var j in q.Joins) { sb.Append("|jp:"); Filter(sb, j.Policy); }
        foreach (var e in q.Expansions)
        {
            sb.Append("|x:").Append(e.Name).Append(':').Append(string.Join(',', e.Select.Select(f => f.Path))).Append(':').Append(e.Limit).Append("|xw:");
            Filter(sb, e.Where);
            sb.Append("|xp:");
            Filter(sb, e.Policy);
            foreach (var o in e.OrderBy) sb.Append("|xo:").Append(o.Field.Path).Append(o.Direction == SortDirection.Ascending ? '+' : '-');
        }
        foreach (var o in q.OrderBy) sb.Append("|o:").Append(o.Field.Alias).Append('.').Append(o.Field.Path).Append(o.Direction == SortDirection.Ascending ? '+' : '-');
        return Hash(sb.ToString());
    }

    /// <summary>Caller scope: identity, tenant, and the concrete policy values. A cursor from another tenant or policy profile is rejected.</summary>
    public static string Scope(QuerySecurityContext ctx, ResolvedQuery q)
    {
        var sb = new StringBuilder();
        sb.Append(ctx.TenantId).Append('|').Append(ctx.User.Identity?.Name).Append('|')
          .Append(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        Values(sb, q.Policy);
        foreach (var j in q.Joins) Values(sb, j.Policy);
        ExistsPolicies(sb, q.Where);
        ExistsPolicies(sb, q.Having);
        foreach (var e in q.Expansions) { Values(sb, e.Policy); ExistsPolicies(sb, e.Where); }
        return Hash(sb.ToString());
    }

    private static void Filter(StringBuilder sb, ResolvedFilter? f)
    {
        switch (f)
        {
            case null: sb.Append('-'); break;
            case ResolvedLogical l:
                sb.Append(l.Operator).Append('(');
                foreach (var c in l.Children) { Filter(sb, c); sb.Append(','); }
                sb.Append(')');
                break;
            case ResolvedNot n: sb.Append("not("); Filter(sb, n.Operand); sb.Append(')'); break;
            case ResolvedExists x: sb.Append("any:").Append(x.Relationship.PublicName).Append('('); Filter(sb, x.Policy); sb.Append(';'); Filter(sb, x.Where); sb.Append(')'); break;
            case ResolvedComparison c:
                sb.Append(c.Field.Alias).Append('.').Append(c.Field.Column.PropertyName).Append(' ').Append(c.Operator).Append(' ')
                  .Append(c.Right switch
                  {
                      ResolvedConstant { Value: null } => "null",
                      ResolvedConstant => "v",
                      ResolvedList => "list",
                      ResolvedFieldOperand o => o.Field.Alias + "." + o.Field.Column.PropertyName,
                      _ => "?",
                  });
                break;
        }
    }

    /// <summary>Trusted policy values inside EXISTS subqueries (client values never enter the scope).</summary>
    private static void ExistsPolicies(StringBuilder sb, ResolvedFilter? f)
    {
        switch (f)
        {
            case ResolvedLogical l: foreach (var c in l.Children) ExistsPolicies(sb, c); break;
            case ResolvedNot n: ExistsPolicies(sb, n.Operand); break;
            case ResolvedExists x: Values(sb, x.Policy); ExistsPolicies(sb, x.Where); break;
        }
    }

    private static void Values(StringBuilder sb, ResolvedFilter? f)
    {
        switch (f)
        {
            case ResolvedLogical l: foreach (var c in l.Children) Values(sb, c); break;
            case ResolvedNot n: Values(sb, n.Operand); break;
            case ResolvedExists x: Values(sb, x.Policy); break;
            case ResolvedComparison { Right: ResolvedConstant k }: sb.Append('|').Append(Convert.ToString(k.Value, CultureInfo.InvariantCulture)); break;
            case ResolvedComparison { Right: ResolvedList l }: foreach (var v in l.Values) sb.Append('|').Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
}
