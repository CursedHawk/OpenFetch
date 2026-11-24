using System.Collections.Immutable;

namespace OpenFetch;

/// <summary>
/// Logical-name resolution + capability, operator, value and structural-limit validation (§5, §14, §17, §22).
/// Anything unknown, hidden, unauthorized or not enabled for the usage site resolves as "unknown" so nothing is disclosed (§16.2, §24).
/// </summary>
internal sealed class QueryResolver(QueryModel model, DatasetDescriptor root, Func<ImmutableArray<string>, bool> allowed)
{
    private enum Usage { Select, Filter, Sort, Group, Aggregate }

    private readonly QueryLimits _limits = model.Limits;
    private readonly List<ResolvedJoin> _joins = [];
    private int _conditions;
    private int _subqueries;
    private long _expandedRows;

    public ResolvedQuery Resolve(QueryDocument doc)
    {
        if (doc.IsAggregate) return ResolveAggregate(doc);
        if (doc.Select.Length > _limits.MaximumSelectedFields)
            throw QueryErrors.TooComplex($"At most {_limits.MaximumSelectedFields} fields may be selected.", "/select");
        var select = doc.Select.Select(f => Field(f, Usage.Select)).ToImmutableArray();
        foreach (var dup in doc.Select.GroupBy(f => f.Path).Where(g => g.Count() > 1))
            throw QueryErrors.InvalidQuery($"Field '{dup.Key}' is selected more than once.", dup.Last().Pointer);
        var expansions = doc.Expand.Select(Expansion).ToImmutableArray();
        foreach (var (e, i) in expansions.Select((e, i) => (e, i)))
            if (expansions.Take(i).Any(x => x.Name == e.Name) || select.Any(s => s.Path.Split('.')[0] == e.Name))
                throw QueryErrors.InvalidQuery($"'{e.Name}' is already part of the result.", doc.Expand[i].Pointer + "/relationship");
        // Worst case rows fetched by expansions: parent page size × per-parent limit, summed (bounded before execution).
        if (_expandedRows * PageSize(doc.Page) > _limits.MaximumExpandedRows)
            throw QueryErrors.TooComplex($"Expansions could return more than {_limits.MaximumExpandedRows} rows; lower page.first or expand 'first'.", "/expand");

        ResolvedFilter? where = null;
        if (doc.Where is not null)
        {
            if (!root.Capabilities.HasFlag(DatasetCapabilities.Filter)) throw QueryErrors.UnsupportedFeature("Filtering is not enabled for this dataset.", "/where");
            where = Filter(doc.Where);
            if (_conditions > _limits.MaximumConditions)
                throw QueryErrors.TooComplex($"At most {_limits.MaximumConditions} conditions are allowed.", "/where");
        }

        if (!doc.OrderBy.IsEmpty && !root.Capabilities.HasFlag(DatasetCapabilities.Sort))
            throw QueryErrors.UnsupportedFeature("Sorting is not enabled for this dataset.", "/orderBy");
        var order = new List<ResolvedOrder>();
        foreach (var o in doc.OrderBy)
        {
            var field = Field(o.Field, Usage.Sort);
            if (order.Any(x => x.Field.Path == field.Path)) throw QueryErrors.InvalidQuery($"Field '{field.Path}' is ordered more than once.", o.Field.Pointer);
            if (doc.Distinct && select.All(s => s.Path != field.Path))
                throw QueryErrors.InvalidQuery("With 'distinct', every ordered field must also be selected.", o.Field.Pointer);
            order.Add(new ResolvedOrder(field, o.Direction));
        }

        if (_joins.Count + _subqueries > _limits.MaximumRelationships)
            throw QueryErrors.TooComplex($"At most {_limits.MaximumRelationships} relationships may be used.");

        // Deterministic ordering (§20.1): distinct rows are unique over the selected fields, otherwise the key breaks ties.
        IEnumerable<ResolvedField> tieBreakers = doc.Distinct ? select : root.Key.Select(k => new ResolvedField("t0", k.PublicName, k));
        foreach (var t in tieBreakers.Where(t => !order.Any(o => o.Field.Alias == t.Alias && o.Field.Column == t.Column)).ToList())
            order.Add(new ResolvedOrder(t, SortDirection.Ascending));

        var cursorCapable = !doc.Distinct && order.All(o =>
            o.Field.Alias == "t0" && !o.Field.Column.Nullable && (o.Field.Column.Can(ColumnCapabilities.Cursor) || root.Key.Contains(o.Field.Column)));

        return new ResolvedQuery
        {
            Dataset = root,
            Joins = [.. _joins],
            Select = select,
            Where = where,
            OrderBy = [.. order],
            PageSize = PageSize(doc.Page),
            Offset = Offset(doc.Page),
            Distinct = Distinct(doc),
            CursorCapable = CursorAllowed(doc.Page, cursorCapable) && cursorCapable,
            Expansions = expansions,
            OwnedPresence = OwnedPresence(select),
        };
    }

    /// <summary>
    /// One presence flag per optional owned reference in the selection, over the owned columns this caller may read:
    /// the object is written as null only when all of those are null, and hidden columns never show through.
    /// </summary>
    private ImmutableArray<ResolvedField> OwnedPresence(ImmutableArray<ResolvedField> select, DatasetDescriptor? expansionTarget = null)
    {
        var result = new List<ResolvedField>();
        foreach (var f in select)
        {
            var dataset = expansionTarget ?? (f.Alias == "t0" ? root : _joins.First(j => j.Alias == f.Alias).Target);
            var basePath = f.Path[..^f.Column.PublicName.Length];
            foreach (var (owned, columns) in dataset.OptionalOwned)
            {
                var path = basePath + owned;
                if (!f.Column.PublicName.StartsWith(owned + ".", StringComparison.Ordinal) || result.Any(r => r.Path == path)) continue;
                result.Add(new ResolvedField(f.Alias, path, ResolvedField.PresenceFlag)
                {
                    Presence = [.. columns.Where(c => c.Can(ColumnCapabilities.Select) && allowed(c.Policies))],
                });
            }
        }
        return [.. result];
    }

    private int PageSize(PageNode page)
    {
        var first = page.First ?? root.DefaultPageSize;
        if (first < 1 || first > root.MaximumPageSize) throw QueryErrors.PageSizeExceeded(root.MaximumPageSize, "/page/first");
        return first;
    }

    private int Offset(PageNode page)
    {
        if (page.Offset is not { } offset) return 0;
        if (!root.Capabilities.HasFlag(DatasetCapabilities.OffsetPagination)) throw QueryErrors.UnsupportedFeature("Offset pagination is not enabled for this dataset.", "/page/offset");
        if (offset < 0 || offset > _limits.MaximumOffset)
            throw QueryErrors.TooComplex($"'offset' must be between 0 and {_limits.MaximumOffset}; use cursor pagination.", "/page/offset");
        return offset;
    }

    private bool CursorAllowed(PageNode page, bool cursorCapable)
    {
        var enabled = root.Capabilities.HasFlag(DatasetCapabilities.CursorPagination);
        if (page.After is not null && !(enabled && cursorCapable))
            throw QueryErrors.UnsupportedFeature("Cursor pagination requires ordering only by cursor-eligible fields.", "/page/after");
        return enabled && page.Offset is null;
    }

    private bool Distinct(QueryDocument doc)
    {
        if (!doc.Distinct) return false;
        if (!root.Capabilities.HasFlag(DatasetCapabilities.Distinct)) throw QueryErrors.UnsupportedFeature("Distinct is not enabled for this dataset.", "/distinct");
        if (doc.Page.After is not null) throw QueryErrors.UnsupportedFeature("Distinct queries support offset pagination only.", "/page/after");
        return true;
    }

    private ResolvedField Field(FieldRef f, Usage usage)
    {
        var (columnCap, relationCap) = usage switch
        {
            Usage.Select => (ColumnCapabilities.Select, RelationshipCapabilities.Select),
            Usage.Filter => (ColumnCapabilities.Filter, RelationshipCapabilities.Filter),
            Usage.Group => (ColumnCapabilities.Group, RelationshipCapabilities.Select),
            Usage.Aggregate => (ColumnCapabilities.Aggregate, RelationshipCapabilities.Select),
            _ => (ColumnCapabilities.Sort, RelationshipCapabilities.Sort),
        };
        var segments = f.Path.Split('.');
        var dataset = root;
        var alias = "t0";
        for (var depth = 1; depth < segments.Length; depth++)
        {
            // Owned (table-split) members are dotted column names: they win over relationship traversal.
            if (dataset.Columns.ContainsKey(string.Join('.', segments[(depth - 1)..]))) { segments = [.. segments[..(depth - 1)], string.Join('.', segments[(depth - 1)..])]; break; }
            // Dotted paths are to-one only; collections are reached through 'any' filters and 'expand'.
            if (!dataset.Relationships.TryGetValue(segments[depth - 1], out var rel) || rel.Cardinality == Cardinality.Many || !rel.Can(relationCap) || !allowed(rel.Policies)
                || depth > rel.MaximumDepth || depth > root.MaximumRelationshipDepth
                || !model.Datasets.TryGetValue(rel.TargetDataset, out var target) || !allowed(target.Policies))
                throw QueryErrors.UnknownField(root.PublicName, f.Path, f.Pointer);

            var path = string.Join('.', segments[..depth]);
            var join = _joins.FirstOrDefault(j => j.Path == path);
            if (join is null) _joins.Add(join = new ResolvedJoin($"t{_joins.Count + 1}", alias, path, rel, target));
            (dataset, alias) = (target, join.Alias);
        }
        if (!dataset.Columns.TryGetValue(segments[^1], out var column) || !column.Can(columnCap) || !allowed(column.Policies))
            throw QueryErrors.UnknownField(root.PublicName, f.Path, f.Pointer);
        return new ResolvedField(alias, f.Path, column);
    }

    private ResolvedFilter Filter(FilterNode node) => Filter(node, f => Field(f, Usage.Filter), root, "t0");

    private ResolvedFilter Filter(FilterNode node, Func<FieldRef, ResolvedField> field, DatasetDescriptor scope, string alias) => node switch
    {
        LogicalFilter l => new ResolvedLogical(l.Operator, [.. l.Children.Select(c => Filter(c, field, scope, alias))]),
        NotFilter n => new ResolvedNot(Filter(n.Operand, field, scope, alias)),
        ComparisonFilter c => Comparison(c, field),
        AnyFilter a => Any(a, scope, alias),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Resolves a one-to-many relationship of <paramref name="scope"/> for 'any' or 'expand'; unusable ones look unknown.</summary>
    private (RelationshipDescriptor Relationship, DatasetDescriptor Target) Collection(DatasetDescriptor scope, string name, RelationshipCapabilities cap, string pointer)
    {
        if (!scope.Relationships.TryGetValue(name, out var rel) || rel.Cardinality != Cardinality.Many || !rel.Can(cap) || !allowed(rel.Policies)
            || !model.Datasets.TryGetValue(rel.TargetDataset, out var target) || !allowed(target.Policies))
            throw QueryErrors.InvalidRelationship(root.PublicName, name, pointer);
        if (++_subqueries + _joins.Count > _limits.MaximumRelationships)
            throw QueryErrors.TooComplex($"At most {_limits.MaximumRelationships} relationships may be used.", pointer);
        return (rel, target);
    }

    /// <summary>A field of a collection target: its own columns only (no dotted paths inside subqueries).</summary>
    private ResolvedField LocalField(DatasetDescriptor dataset, string alias, FieldRef f, ColumnCapabilities cap)
    {
        if (!dataset.Columns.TryGetValue(f.Path, out var column) || !column.Can(cap) || !allowed(column.Policies))
            throw QueryErrors.UnknownField(dataset.PublicName, f.Path, f.Pointer);
        return new ResolvedField(alias, f.Path, column);
    }

    private ResolvedExists Any(AnyFilter a, DatasetDescriptor scope, string parentAlias)
    {
        var (rel, target) = Collection(scope, a.Relationship, RelationshipCapabilities.Filter, a.Pointer + "/relationship");
        if (a.Where is not null && !target.Capabilities.HasFlag(DatasetCapabilities.Filter))
            throw QueryErrors.UnsupportedFeature("Filtering is not enabled for the related dataset.", a.Pointer + "/where");
        _conditions++;
        var alias = "s" + _subqueries;
        var where = a.Where is null ? null : Filter(a.Where, f => LocalField(target, alias, f, ColumnCapabilities.Filter), target, alias);
        return new ResolvedExists(alias, parentAlias, rel, target, where);
    }

    private ResolvedExpansion Expansion(ExpandNode e)
    {
        var (rel, target) = Collection(root, e.Relationship, RelationshipCapabilities.Select, e.Pointer + "/relationship");
        var alias = "e" + _subqueries;
        var select = e.Select.Select(f => LocalField(target, alias, f, ColumnCapabilities.Select)).ToImmutableArray();
        foreach (var dup in e.Select.GroupBy(f => f.Path).Where(g => g.Count() > 1))
            throw QueryErrors.InvalidQuery($"Field '{dup.Key}' is selected more than once.", dup.Last().Pointer);

        ResolvedFilter? where = null;
        if (e.Where is not null)
        {
            if (!target.Capabilities.HasFlag(DatasetCapabilities.Filter)) throw QueryErrors.UnsupportedFeature("Filtering is not enabled for the related dataset.", e.Pointer + "/where");
            where = Filter(e.Where, f => LocalField(target, alias, f, ColumnCapabilities.Filter), target, alias);
        }
        if (!e.OrderBy.IsEmpty && !target.Capabilities.HasFlag(DatasetCapabilities.Sort))
            throw QueryErrors.UnsupportedFeature("Sorting is not enabled for the related dataset.", e.Pointer + "/orderBy");
        var order = e.OrderBy.Select(o => new ResolvedOrder(LocalField(target, alias, o.Field, ColumnCapabilities.Sort), o.Direction)).ToList();
        foreach (var k in target.Key.Where(k => order.All(o => o.Field.Column != k))) order.Add(new ResolvedOrder(new ResolvedField(alias, k.PublicName, k), SortDirection.Ascending));

        var limit = e.First ?? Math.Min(10, target.DefaultPageSize); // small default: expansions multiply the parent page
        if (limit < 1 || limit > target.MaximumPageSize) throw QueryErrors.PageSizeExceeded(target.MaximumPageSize, e.Pointer + "/first");
        _expandedRows += limit;
        var (source, foreignKey) = rel.Join[0];
        return new ResolvedExpansion(rel.PublicName, rel, target, new ResolvedField("t0", "$expand:" + rel.PublicName, source), foreignKey,
            select, where, [.. order], limit) { OwnedPresence = OwnedPresence(select, target) };
    }

    // ---- Aggregation (§21): only Group/Aggregate-enabled, authorized columns; row policies still apply to WHERE. ----

    private ResolvedQuery ResolveAggregate(QueryDocument doc)
    {
        if (!root.Capabilities.HasFlag(DatasetCapabilities.Aggregation))
            throw QueryErrors.UnsupportedFeature("Aggregation is not enabled for this dataset.", doc.Aggregates.IsEmpty ? "/groupBy" : "/aggregates");
        if (doc.Distinct) throw QueryErrors.InvalidQuery("'distinct' cannot be combined with aggregation.", "/distinct");
        if (doc.Page.After is not null) throw QueryErrors.UnsupportedFeature("Aggregate queries support offset pagination only.", "/page/after");
        if (doc.GroupBy.Length + doc.Aggregates.Length > _limits.MaximumSelectedFields)
            throw QueryErrors.TooComplex($"At most {_limits.MaximumSelectedFields} group fields and aggregates are allowed.", "/aggregates");

        var groupBy = new List<ResolvedField>();
        foreach (var g in doc.GroupBy)
        {
            if (groupBy.Any(x => x.Path == g.Path)) throw QueryErrors.InvalidQuery($"Field '{g.Path}' is grouped more than once.", g.Pointer);
            groupBy.Add(Field(g, Usage.Group));
        }

        var aggregates = new List<ResolvedField>();
        foreach (var a in doc.Aggregates)
        {
            if (!QueryModelBuilder.IsPublicName(a.Alias))
                throw QueryErrors.InvalidQuery("Aliases must start with a letter and contain only letters, digits and '_'.", a.Pointer + "/alias");
            if (aggregates.Any(x => x.Path == a.Alias) || groupBy.Any(g => g.Path.Split('.')[0] == a.Alias))
                throw QueryErrors.InvalidQuery($"Alias '{a.Alias}' is already used.", a.Pointer + "/alias");
            var argument = a.Field is null ? null : Field(a.Field, Usage.Aggregate);
            var column = AggregateColumn(a, argument?.Column);
            aggregates.Add(new ResolvedField(argument?.Alias ?? "t0", a.Alias, column) { Aggregate = new ResolvedAggregate(a.Function, argument) });
        }

        ResolvedFilter? where = null;
        if (doc.Where is not null)
        {
            if (!root.Capabilities.HasFlag(DatasetCapabilities.Filter)) throw QueryErrors.UnsupportedFeature("Filtering is not enabled for this dataset.", "/where");
            where = Filter(doc.Where);
        }

        // HAVING and ORDER BY refer to aggregate aliases or grouped fields only; a grouped field still needs its own
        // Filter (having) or Sort (orderBy) capability: Group alone never implies them.
        ResolvedField Output(FieldRef f, ColumnCapabilities cap) =>
            aggregates.FirstOrDefault(x => x.Path == f.Path)
            ?? groupBy.FirstOrDefault(x => x.Path == f.Path && x.Column.Can(cap))
            ?? throw QueryErrors.UnknownField(root.PublicName, f.Path, f.Pointer);
        if (doc.Having is not null && !root.Capabilities.HasFlag(DatasetCapabilities.Filter))
            throw QueryErrors.UnsupportedFeature("Filtering is not enabled for this dataset.", "/having");
        if (doc.Having is not null && Contains<AnyFilter>(doc.Having, out var any))
            throw QueryErrors.InvalidQuery("'having' can only reference aggregate aliases and grouped fields; use 'any' in 'where'.", any.Pointer);
        var having = doc.Having is null ? null : Filter(doc.Having, f => Output(f, ColumnCapabilities.Filter), root, "t0");
        if (_conditions > _limits.MaximumConditions) throw QueryErrors.TooComplex($"At most {_limits.MaximumConditions} conditions are allowed.", "/having");

        if (!doc.OrderBy.IsEmpty && !root.Capabilities.HasFlag(DatasetCapabilities.Sort))
            throw QueryErrors.UnsupportedFeature("Sorting is not enabled for this dataset.", "/orderBy");
        var order = new List<ResolvedOrder>();
        foreach (var o in doc.OrderBy)
        {
            var field = Output(o.Field, ColumnCapabilities.Sort);
            if (order.Any(x => x.Field.Path == field.Path)) throw QueryErrors.InvalidQuery($"Field '{field.Path}' is ordered more than once.", o.Field.Pointer);
            order.Add(new ResolvedOrder(field, o.Direction));
        }
        // Group keys are unique per row: they make the order deterministic.
        foreach (var g in groupBy.Where(g => order.All(o => o.Field.Path != g.Path))) order.Add(new ResolvedOrder(g, SortDirection.Ascending));

        if (_joins.Count + _subqueries > _limits.MaximumRelationships) throw QueryErrors.TooComplex($"At most {_limits.MaximumRelationships} relationships may be used.");

        return new ResolvedQuery
        {
            Dataset = root,
            Joins = [.. _joins],
            Select = [.. groupBy, .. aggregates],
            GroupBy = [.. groupBy],
            Where = where,
            Having = having,
            OrderBy = [.. order],
            PageSize = PageSize(doc.Page),
            Offset = Offset(doc.Page),
            IsAggregate = true,
        };
    }

    private static bool Contains<T>(FilterNode node, out T found) where T : FilterNode
    {
        found = null!;
        switch (node)
        {
            case T t: found = t; return true;
            case LogicalFilter l: foreach (var c in l.Children) if (Contains(c, out found)) return true; return false;
            case NotFilter n: return Contains(n.Operand, out found);
            default: return false;
        }
    }

    /// <summary>Synthetic descriptor for an aggregate result; types are normalized so every provider returns the same values.</summary>
    private static ColumnDescriptor AggregateColumn(AggregateNode a, ColumnDescriptor? arg)
    {
        var kind = arg?.Kind;
        var path = a.Pointer + "/function";
        ScalarKind resultKind = a.Function switch
        {
            AggregateFunction.Count or AggregateFunction.CountDistinct => ScalarKind.Integer,
            AggregateFunction.Sum when kind is ScalarKind.Integer or ScalarKind.Decimal or ScalarKind.Number => kind.Value,
            AggregateFunction.Avg when kind is ScalarKind.Integer or ScalarKind.Decimal => ScalarKind.Decimal,
            AggregateFunction.Avg when kind is ScalarKind.Number => ScalarKind.Number,
            AggregateFunction.Min or AggregateFunction.Max when kind is ScalarKind.Integer or ScalarKind.Decimal or ScalarKind.Number
                or ScalarKind.DateTime or ScalarKind.Date or ScalarKind.Time or ScalarKind.String => kind.Value,
            _ => throw QueryErrors.InvalidValueType($"'{QueryParser.FunctionName(a.Function)}' cannot be applied to a {QueryOperators.TypeName(kind ?? ScalarKind.String)} field.", path),
        };
        var operators = QueryOperators.DefaultsFor(resultKind);
        if (a.Function is AggregateFunction.Min or AggregateFunction.Max)
            return arg! with
            {
                PublicName = a.Alias, Nullable = true, Exposed = true, Operators = operators, Policies = [],
                Capabilities = ColumnCapabilities.Select | ColumnCapabilities.Filter | ColumnCapabilities.Sort,
            };
        var clr = resultKind switch { ScalarKind.Integer => typeof(long), ScalarKind.Decimal => typeof(decimal), _ => typeof(double) };
        return new ColumnDescriptor
        {
            PublicName = a.Alias,
            PropertyName = a.Alias,
            ClrType = clr,
            Kind = resultKind,
            Nullable = a.Function is not (AggregateFunction.Count or AggregateFunction.CountDistinct),
            Exposed = true,
            Capabilities = ColumnCapabilities.Select | ColumnCapabilities.Filter | ColumnCapabilities.Sort,
            Operators = operators,
            Physical = new PhysicalColumn(a.Alias, null, clr, null, null),
        };
    }

    private ResolvedComparison Comparison(ComparisonFilter c, Func<FieldRef, ResolvedField> resolve)
    {
        _conditions++;
        var field = resolve(c.Left);
        var column = field.Column;
        if (!column.Operators.Contains(c.Operator))
            throw QueryErrors.UnsupportedOperator(root.PublicName, c.Left.Path, c.Operator, column.Operators, c.Pointer + "/operator");
        return new ResolvedComparison(field, c.Operator, Operand(c, column, c.Pointer.StartsWith("/having", StringComparison.Ordinal)));
    }

    private ResolvedOperand Operand(ComparisonFilter c, ColumnDescriptor column, bool having = false)
    {
        var path = c.Pointer + "/value";
        switch (c.Right)
        {
            case ConstantOperand { Value: NullValue }:
                if (!column.Nullable) throw QueryErrors.InvalidValueType($"Field '{column.PublicName}' is not nullable.", path);
                return new ResolvedConstant(null);
            case ConstantOperand k:
                return new ResolvedConstant(ValueCoercion.Coerce(k.Value, column, path));
            case ListOperand l:
                if (l.Values.Length > _limits.MaximumInValues)
                    throw QueryErrors.TooComplex($"At most {_limits.MaximumInValues} values are allowed.", path);
                return new ResolvedList([.. l.Values.Select((v, i) => ValueCoercion.Coerce(v, column, $"{path}/{i}"))]);
            case FieldOperand f:
                if (!model.Features.FieldComparison || having) throw QueryErrors.UnsupportedFeature("Field comparison is not enabled here.", c.Pointer + "/compareTo");
                var other = Field(f.Field, Usage.Filter);
                if (other.Column.ClrType != column.ClrType)
                    throw QueryErrors.InvalidValueType("Compared fields must have the same type.", c.Pointer + "/compareTo");
                return new ResolvedFieldOperand(other);
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Resolves a trusted policy filter against one table: property or public names, hidden columns allowed, no capability checks.</summary>
    public static ResolvedFilter Trusted(FilterNode node, DatasetDescriptor dataset, string alias) => node switch
    {
        LogicalFilter l => new ResolvedLogical(l.Operator, [.. l.Children.Select(c => Trusted(c, dataset, alias))]),
        NotFilter n => new ResolvedNot(Trusted(n.Operand, dataset, alias)),
        ComparisonFilter c => TrustedComparison(c, dataset, alias),
        _ => throw new InvalidOperationException(),
    };

    private static ResolvedComparison TrustedComparison(ComparisonFilter c, DatasetDescriptor dataset, string alias)
    {
        var column = dataset.FindProperty(c.Left.Path) ?? dataset.Columns.GetValueOrDefault(c.Left.Path)
            ?? throw new InvalidOperationException($"Row policy references unknown field '{c.Left.Path}' on '{dataset.PublicName}'.");
        var field = new ResolvedField(alias, column.PublicName, column);
        ResolvedOperand right = c.Right switch
        {
            ConstantOperand { Value: NullValue } => new ResolvedConstant(null),
            ConstantOperand k => new ResolvedConstant(ValueCoercion.Coerce(k.Value, column, "")),
            ListOperand l => new ResolvedList([.. l.Values.Select(v => ValueCoercion.Coerce(v, column, ""))]),
            _ => throw new InvalidOperationException("Row policies cannot use field comparison."),
        };
        if (right is ResolvedConstant { Value: null } && c.Operator is not (QueryOperator.Equal or QueryOperator.NotEqual))
            throw new InvalidOperationException($"Row policy compares '{column.PropertyName}' with null using '{QueryOperators.Name(c.Operator)}'.");
        return new ResolvedComparison(field, c.Operator, right);
    }
}
