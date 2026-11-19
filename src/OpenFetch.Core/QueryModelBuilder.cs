using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenFetch;

/// <summary>
/// Merges physical metadata, attributes and fluent configuration into the immutable <see cref="QueryModel"/> (§7.6, §9, §26).
/// Precedence per setting: Hide (deny) &gt; fluent &gt; attribute &gt; convention &gt; secure default (not exposed).
/// Every validation error is collected; any error fails startup.
/// </summary>
public static partial class QueryModelBuilder
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex PublicName();

    public static QueryModel Build(IEnumerable<EntityMapping> entities, OpenFetchOptions options, ICollection<string>? warnings = null)
    {
        var errors = new List<string>();
        var limits = options.Limits;
        ValidateLimits(limits, errors);

        var mappings = entities.ToList();
        foreach (var type in options.Datasets.Keys.Where(t => mappings.All(m => m.ClrType != t)))
            errors.Add($"Fluent configuration targets {type.Name}, which is not a mapped entity.");

        // Pass 1: datasets and columns.
        var built = new List<(EntityMapping Mapping, DatasetConfig? Config, DatasetDescriptor Dataset)>();
        foreach (var mapping in mappings)
        {
            var config = options.Datasets.GetValueOrDefault(mapping.ClrType);
            if (config is not null) errors.AddRange(config.Conflicts);
            var dataset = BuildDataset(mapping, config, options, errors);
            if (dataset is null) continue;
            // EF global query filters often carry tenant isolation; direct SQL does not apply them (§8). Fail closed unless the
            // dataset has its own row policy or the gap is explicitly acknowledged.
            if (mapping.HasQueryFilter && config?.RowPolicies.Count is null or 0 && config?.QueryFilterAcknowledged != true)
                errors.Add($"Dataset '{dataset.PublicName}' has an EF global query filter that OpenFetch SQL does not apply. " +
                           "Add ApplyRowPolicy(...) or call AcknowledgeEfQueryFilterNotApplied().");
            else if (mapping.HasQueryFilter)
                warnings?.Add($"Dataset '{dataset.PublicName}' has an EF global query filter; OpenFetch relies on its row policies instead.");
            built.Add((mapping, config, dataset));
        }

        foreach (var dup in built.GroupBy(b => b.Dataset.PublicName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Duplicate dataset name '{dup.Key}'.");
        foreach (var dup in built.GroupBy(b => ComponentName(b.Dataset.PublicName), StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"Datasets {string.Join(", ", dup.Select(d => $"'{d.Dataset.PublicName}'"))} collide on OpenAPI component name '{dup.Key}'.");

        // Pass 2: relationships (need every dataset) and row policies.
        var byType = built.ToDictionary(b => b.Mapping.ClrType, b => b.Dataset);
        var final = new List<DatasetDescriptor>();
        foreach (var (mapping, config, dataset) in built)
        {
            var relationships = BuildRelationships(mapping, config, dataset, byType, limits, errors);
            foreach (var name in relationships.Keys.Where(dataset.Columns.ContainsKey))
                errors.Add($"Dataset '{dataset.PublicName}': '{name}' is both a column and a relationship.");

            var policies = ImmutableArray.CreateBuilder<RowPolicy>();
            foreach (var lambda in config?.RowPolicies ?? [])
            {
                try
                {
                    var contextType = lambda.Parameters.Count > 1 ? lambda.Parameters[1].Type : typeof(object);
                    policies.Add(new RowPolicy(contextType, RowPolicyTranslator.Translate(lambda, dataset)));
                }
                catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            }
            foreach (var (name, fields) in config?.Projections ?? [])
            {
                if (!PublicName().IsMatch(name)) errors.Add($"Dataset '{dataset.PublicName}' projection '{name}': invalid name.");
                if (fields.Length == 0) errors.Add($"Dataset '{dataset.PublicName}' projection '{name}': no fields.");
            }
            final.Add(dataset with
            {
                Relationships = relationships,
                RowPolicies = policies.ToImmutable(),
                Projections = (config?.Projections ?? []).ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal),
            });
        }

        foreach (var dup in final.SelectMany(d => d.Projections.Keys.Select(p => (Name: ComponentName(d.PublicName) + ComponentName(p), Owner: $"{d.PublicName}/{p}")))
                     .Concat(final.Select(d => (Name: ComponentName(d.PublicName), Owner: d.PublicName)))
                     .GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"{string.Join(", ", dup.Select(d => $"'{d.Owner}'"))} collide on OpenAPI component name '{dup.Key}'.");
        if (errors.Count > 0) throw new OpenFetchConfigurationException(errors);

        var datasets = final.ToFrozenDictionary(d => d.PublicName, StringComparer.Ordinal);
        var model = new QueryModel(datasets, limits, options.Features, ModelHash.Compute(datasets.Values, limits, options.Features));

        // Projection fields must resolve like a client select would (capabilities, relationships, depth).
        foreach (var d in datasets.Values)
            foreach (var (name, fields) in d.Projections)
            {
                try { new QueryResolver(model, d, _ => true).Resolve(QueryEngine.WithProjection(QueryParser.Empty, fields)); }
                catch (OpenFetchException ex) { errors.Add($"Dataset '{d.PublicName}' projection '{name}': {ex.Detail}"); }
            }
        if (errors.Count > 0) throw new OpenFetchConfigurationException(errors);
        return model;
    }

    /// <summary>OpenAPI component prefix for a dataset (§18.3): PascalCase, underscores dropped (orders -> Orders).</summary>
    public static string ComponentName(string dataset) =>
        string.Concat(dataset.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static DatasetDescriptor? BuildDataset(EntityMapping m, DatasetConfig? cfg, OpenFetchOptions options, List<string> errors)
    {
        var attr = m.ClrType.GetCustomAttribute<QueryDatasetAttribute>(inherit: false);
        var hidden = m.ClrType.IsDefined(typeof(QueryHiddenAttribute)) || cfg?.Hidden == true;
        if (hidden || (attr is null && cfg?.Exposed != true)) return null;

        var name = cfg?.Name ?? attr!.Name;
        var where = $"Dataset '{name}' ({m.ClrType.Name})";
        if (!PublicName().IsMatch(name)) errors.Add($"{where}: invalid public name.");
        if (m.UnsupportedReason is not null) errors.Add($"{where}: unsupported entity shape: {m.UnsupportedReason}.");

        var limits = options.Limits;
        int Pick(int? fluent, int attribute, int fallback) => fluent ?? (attribute > 0 ? attribute : fallback);
        var defaultPage = Pick(cfg?.DefaultPageSize, attr?.DefaultPageSize ?? 0, limits.DefaultPageSize);
        var maxPage = Pick(cfg?.MaximumPageSize, attr?.MaximumPageSize ?? 0, limits.MaximumPageSize);
        if (defaultPage < 1 || defaultPage > maxPage || maxPage > limits.MaximumPageSize)
            errors.Add($"{where}: page sizes must satisfy 1 <= default ({defaultPage}) <= maximum ({maxPage}) <= global maximum ({limits.MaximumPageSize}).");

        var capabilities = ((attr?.Capabilities ?? DatasetCapabilities.Default) | (cfg?.Enabled ?? 0)) & ~(cfg?.Disabled ?? 0);

        var indexed = m.IndexedProperties.Union(m.Key.Take(1));
        var columns = ImmutableArray.CreateBuilder<ColumnDescriptor>();
        foreach (var p in m.Properties)
        {
            var column = BuildColumn(m, p, cfg?.Members.GetValueOrDefault(p.Name), options, indexed.Contains(p.Name), where, errors);
            if (column is not null) columns.Add(column);
        }
        var all = columns.ToImmutable();

        foreach (var dup in all.Where(c => c.Exposed).GroupBy(c => c.PublicName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"{where}: duplicate column name '{dup.Key}'.");

        var key = m.Key.Select(k => all.FirstOrDefault(c => c.PropertyName == k)).ToImmutableArray();
        if (key.IsEmpty || key.Any(k => k is null))
            errors.Add($"{where}: a dataset needs a primary key of supported scalar types (deterministic ordering, cursors).");

        return new DatasetDescriptor
        {
            PublicName = name,
            ClrType = m.ClrType,
            Description = cfg?.Description ?? attr?.Description,
            DefaultPageSize = defaultPage,
            MaximumPageSize = maxPage,
            MaximumRelationshipDepth = Math.Min(Pick(cfg?.MaximumRelationshipDepth, attr?.MaximumRelationshipDepth ?? 0, limits.MaximumRelationshipDepth), limits.MaximumRelationshipDepth),
            Capabilities = capabilities,
            Policies = Policies(attr?.AuthorizationPolicy, cfg?.Policies),
            Deprecated = cfg?.Deprecated == true || attr?.Deprecated == true,
            DeprecationMessage = cfg?.DeprecationMessage ?? attr?.DeprecationMessage,
            Columns = all.Where(c => c.Exposed).GroupBy(c => c.PublicName).ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            AllColumns = all,
            Relationships = FrozenDictionary<string, RelationshipDescriptor>.Empty,
            Key = key.Any(k => k is null) ? [] : [.. key.OfType<ColumnDescriptor>()],
            Table = m.Table,
        };
    }

    private static ColumnDescriptor? BuildColumn(EntityMapping m, PropertyMapping p, MemberConfig? cfg, OpenFetchOptions options, bool indexed, string where, List<string> errors)
    {
        var prop = p.IsShadow ? null : m.ClrType.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
        var attr = prop?.GetCustomAttribute<QueryColumnAttribute>();
        var hidden = prop?.IsDefined(typeof(QueryHiddenAttribute)) == true || cfg?.Hidden == true;
        var convention = options.ExposeColumnsByConvention && prop is not null;
        var exposed = !hidden && (attr is not null || cfg?.Exposed == true || convention);
        var kind = QueryOperators.KindOf(p.ClrType);
        var name = cfg?.Name ?? attr?.Name ?? (exposed ? JsonNamingPolicy.CamelCase.ConvertName(p.Name) : p.Name);
        var at = $"{where} column '{name}'";

        if (kind is null)
        {
            if (exposed) errors.Add($"{at}: CLR type {p.ClrType.Name} is not supported.");
            return null; // Unsupported, unexposed members never enter the model.
        }

        var caps = ColumnCapabilities.None;
        if (exposed)
        {
            // Base layer: attribute, else convention, else plain fluent opt-in (select only). Fluent then overrides; repeated fluent calls only narrow.
            bool Layer(bool? fluent, bool? attribute, bool conventionValue, bool optIn) =>
                fluent ?? attribute ?? (convention ? conventionValue : optIn);
            if (Layer(cfg?.Select, attr?.Select, true, true)) caps |= ColumnCapabilities.Select;
            if (Layer(cfg?.Filter, attr?.Filter, true, false)) caps |= ColumnCapabilities.Filter;
            if (Layer(cfg?.Sort, attr?.Sort, true, false)) caps |= ColumnCapabilities.Sort;
            if (Layer(cfg?.Group, attr?.Group, false, false)) caps |= ColumnCapabilities.Group;
            if (Layer(cfg?.Aggregate, attr?.Aggregate, false, false)) caps |= ColumnCapabilities.Aggregate;
            if (Layer(cfg?.Cursor, attr?.Cursor, false, false)) caps |= ColumnCapabilities.Cursor;
            if (!PublicName().IsMatch(name)) errors.Add($"{at}: invalid public name.");
            if (caps.HasFlag(ColumnCapabilities.Cursor) && p.Nullable) errors.Add($"{at}: cursor columns must be non-nullable.");
            if (caps.HasFlag(ColumnCapabilities.Cursor) && !caps.HasFlag(ColumnCapabilities.Sort)) errors.Add($"{at}: cursor columns must be sortable.");
        }

        var operators = FrozenSet<QueryOperator>.Empty;
        if (caps.HasFlag(ColumnCapabilities.Filter))
        {
            IEnumerable<QueryOperator> requested = (IEnumerable<QueryOperator>?)cfg?.Operators
                ?? (attr is { AllowedOperators.Length: > 0 } ? attr.AllowedOperators : QueryOperators.DefaultsFor(kind.Value));
            var valid = QueryOperators.ValidFor(kind.Value);
            foreach (var op in requested.Where(o => !valid.Contains(o)))
                errors.Add($"{at}: operator '{QueryOperators.Name(op)}' is not valid for type {QueryOperators.TypeName(kind.Value)}.");
            operators = requested.Where(valid.Contains).ToFrozenSet();
        }

        var clr = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        return new ColumnDescriptor
        {
            PublicName = exposed ? name : p.Name,
            PropertyName = p.Name,
            ClrType = clr,
            Kind = kind.Value,
            Nullable = p.Nullable,
            Exposed = exposed,
            Capabilities = caps,
            Operators = operators,
            Policies = exposed ? Policies(attr?.AuthorizationPolicy, cfg?.Policies) : [],
            Description = cfg?.Description ?? attr?.Description,
            Sensitivity = attr?.Sensitivity ?? Sensitivity.Public,
            Deprecated = cfg?.Deprecated == true || attr?.Deprecated == true,
            DeprecationMessage = cfg?.DeprecationMessage ?? attr?.DeprecationMessage,
            Format = attr?.Format,
            MaxLength = attr is { MaximumLength: > 0 } ? attr.MaximumLength : p.MaxLength,
            Precision = attr is { Precision: > 0 } ? attr.Precision : p.Precision,
            Scale = p.Scale,
            Indexed = indexed,
            EnumValues = clr.IsEnum ? [.. Enum.GetNames(clr).Select(JsonNamingPolicy.CamelCase.ConvertName)] : [],
            Physical = p.Column,
        };
    }

    private static FrozenDictionary<string, RelationshipDescriptor> BuildRelationships(
        EntityMapping m, DatasetConfig? cfg, DatasetDescriptor dataset, Dictionary<Type, DatasetDescriptor> byType, QueryLimits limits, List<string> errors)
    {
        var result = new Dictionary<string, RelationshipDescriptor>(StringComparer.Ordinal);
        foreach (var nav in m.Navigations)
        {
            var mc = cfg?.Members.GetValueOrDefault(nav.Name);
            var prop = m.ClrType.GetProperty(nav.Name, BindingFlags.Public | BindingFlags.Instance);
            var attr = prop?.GetCustomAttribute<QueryRelationshipAttribute>();
            var hidden = prop?.IsDefined(typeof(QueryHiddenAttribute)) == true || mc?.Hidden == true;
            if (hidden || (attr is null && mc?.Exposed != true)) continue;

            var name = mc?.Name ?? attr?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(nav.Name);
            var at = $"Dataset '{dataset.PublicName}' relationship '{name}'";
            if (!PublicName().IsMatch(name)) errors.Add($"{at}: invalid public name.");
            if (!byType.TryGetValue(nav.TargetClrType, out var target))
            {
                errors.Add($"{at}: target entity {nav.TargetClrType.Name} is not an exposed dataset.");
                continue;
            }
            if (nav.IsCollection)
            {
                errors.Add($"{at}: collection relationships are not supported in this version.");
                continue;
            }

            var join = nav.Join.Select(j => (Source: dataset.FindProperty(j.Source), Target: target.FindProperty(j.Target))).ToList();
            if (join.Count == 0 || join.Any(j => j.Source is null || j.Target is null))
            {
                errors.Add($"{at}: join columns are missing or of unsupported types.");
                continue;
            }

            var caps = RelationshipCapabilities.None;
            if (mc?.Expand ?? attr?.Expand ?? true) caps |= RelationshipCapabilities.Expand;
            if (mc?.Select ?? attr?.Select ?? true) caps |= RelationshipCapabilities.Select;
            if (mc?.Filter ?? attr?.Filter ?? false) caps |= RelationshipCapabilities.Filter;
            if (mc?.Sort ?? attr?.Sort ?? false) caps |= RelationshipCapabilities.Sort;

            result[name] = new RelationshipDescriptor
            {
                PublicName = name,
                PropertyName = nav.Name,
                TargetDataset = target.PublicName,
                Cardinality = Cardinality.One,
                Capabilities = caps,
                MaximumDepth = Math.Min(mc?.MaximumDepth ?? (attr is { MaximumDepth: > 0 } ? attr.MaximumDepth : dataset.MaximumRelationshipDepth), limits.MaximumRelationshipDepth),
                Policies = Policies(attr?.AuthorizationPolicy, mc?.Policies),
                Description = mc?.Description ?? attr?.Description,
                Deprecated = attr?.Deprecated == true,
                DeprecationMessage = attr?.DeprecationMessage,
                Join = [.. join.Select(j => (j.Source!, j.Target!))],
            };
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Policies accumulate: attribute and every fluent call are all required.</summary>
    private static ImmutableArray<string> Policies(string? attribute, List<string>? fluent) =>
        [.. new[] { attribute }.Concat(fluent ?? []).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static void ValidateLimits(QueryLimits l, List<string> errors)
    {
        int[] positive = [l.MaximumDocumentBytes, l.DefaultPageSize, l.MaximumPageSize, l.MaximumRelationshipDepth, l.MaximumFilterDepth,
            l.MaximumConditions, l.MaximumInValues, l.MaximumSelectedFields, l.MaximumComplexity, l.MaximumResponseBytes];
        if (positive.Any(v => v < 1) || l.MaximumOffset < 0 || l.MaximumRelationships < 0) errors.Add("Query limits must be positive.");
        if (l.DefaultPageSize > l.MaximumPageSize) errors.Add("DefaultPageSize exceeds MaximumPageSize.");
        if (l.CommandTimeout <= TimeSpan.Zero || l.CursorLifetime <= TimeSpan.Zero) errors.Add("CommandTimeout and CursorLifetime must be positive.");
    }
}

/// <summary>Structural model hash (§9): canonical JSON of everything externally observable, sorted, SHA-256. Registration order never matters.</summary>
internal static class ModelHash
{
    public static string Compute(IEnumerable<DatasetDescriptor> datasets, QueryLimits limits, QueryFeatures features)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("limits", limits.ToString());
            w.WriteString("features", features.ToString());
            w.WriteStartArray("datasets");
            foreach (var d in datasets.OrderBy(d => d.PublicName, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", d.PublicName);
                w.WriteString("description", d.Description);
                w.WriteString("shape", $"{d.DefaultPageSize}/{d.MaximumPageSize}/{d.MaximumRelationshipDepth}/{d.Capabilities}/{d.Deprecated}/{string.Join(',', d.Policies)}");
                w.WriteString("rowPolicies", string.Join(',', d.RowPolicies.Select(p => p.ContextType.FullName)));
                foreach (var p in d.Projections.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString("p:" + p.Key, string.Join(',', p.Value));
                foreach (var c in d.Columns.Values.OrderBy(c => c.PublicName, StringComparer.Ordinal))
                    w.WriteString("c:" + c.PublicName, $"{c.Kind}/{c.Nullable}/{c.Capabilities}/{string.Join(',', c.Operators.Order())}/{string.Join(',', c.Policies)}/{c.Deprecated}/{c.Format}/{c.MaxLength}/{c.Precision}/{c.Scale}/{string.Join(',', c.EnumValues)}/{c.Description}");
                foreach (var r in d.Relationships.Values.OrderBy(r => r.PublicName, StringComparer.Ordinal))
                    w.WriteString("r:" + r.PublicName, $"{r.TargetDataset}/{r.Cardinality}/{r.Capabilities}/{r.MaximumDepth}/{string.Join(',', r.Policies)}/{r.Description}");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan))[..16];
    }
}
