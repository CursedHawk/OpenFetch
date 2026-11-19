using System.Collections.Immutable;

namespace OpenFetch;

/// <summary>
/// Logical-name resolution + capability, operator, value and structural-limit validation (§5, §14, §17, §22).
/// Anything unknown, hidden, unauthorized or not enabled for the usage site resolves as "unknown" so nothing is disclosed (§16.2, §24).
/// </summary>
internal sealed class QueryResolver(QueryModel model, DatasetDescriptor root, Func<ImmutableArray<string>, bool> allowed)
{
    private enum Usage { Select, Filter, Sort }

    private readonly QueryLimits _limits = model.Limits;
    private readonly List<ResolvedJoin> _joins = [];
    private int _conditions;

    public ResolvedQuery Resolve(QueryDocument doc)
    {
        if (doc.Select.Length > _limits.MaximumSelectedFields)
            throw QueryErrors.TooComplex($"At most {_limits.MaximumSelectedFields} fields may be selected.", "/select");
        var select = doc.Select.Select(f => Field(f, Usage.Select)).ToImmutableArray();
        foreach (var dup in doc.Select.GroupBy(f => f.Path).Where(g => g.Count() > 1))
            throw QueryErrors.InvalidQuery($"Field '{dup.Key}' is selected more than once.", dup.Last().Pointer);

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

        if (_joins.Count > _limits.MaximumRelationships)
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
        };
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
            _ => (ColumnCapabilities.Sort, RelationshipCapabilities.Sort),
        };
        var segments = f.Path.Split('.');
        var dataset = root;
        var alias = "t0";
        for (var depth = 1; depth < segments.Length; depth++)
        {
            if (!dataset.Relationships.TryGetValue(segments[depth - 1], out var rel) || !rel.Can(relationCap) || !allowed(rel.Policies)
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

    private ResolvedFilter Filter(FilterNode node) => node switch
    {
        LogicalFilter l => new ResolvedLogical(l.Operator, [.. l.Children.Select(Filter)]),
        NotFilter n => new ResolvedNot(Filter(n.Operand)),
        ComparisonFilter c => Comparison(c),
        _ => throw new InvalidOperationException(),
    };

    private ResolvedComparison Comparison(ComparisonFilter c)
    {
        _conditions++;
        var field = Field(c.Left, Usage.Filter);
        var column = field.Column;
        if (!column.Operators.Contains(c.Operator))
            throw QueryErrors.UnsupportedOperator(root.PublicName, c.Left.Path, c.Operator, column.Operators, c.Pointer + "/operator");
        return new ResolvedComparison(field, c.Operator, Operand(c, column));
    }

    private ResolvedOperand Operand(ComparisonFilter c, ColumnDescriptor column)
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
                if (!model.Features.FieldComparison) throw QueryErrors.UnsupportedFeature("Field comparison is not enabled.", c.Pointer + "/compareTo");
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
