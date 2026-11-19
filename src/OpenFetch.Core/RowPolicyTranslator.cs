using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;

namespace OpenFetch;

/// <summary>
/// Translates a fluent row-policy lambda into a <see cref="FilterNode"/> factory at startup. Only mapped members, comparisons,
/// &amp;&amp;, ||, !, and <c>collection.Contains(member)</c> are allowed; anything else fails startup. Context-derived values become
/// <see cref="ClrValue"/> constants, so they are always parameters.
/// </summary>
internal static class RowPolicyTranslator
{
    public static Func<object, FilterNode> Translate(LambdaExpression lambda, DatasetDescriptor dataset)
    {
        var entity = lambda.Parameters[0];
        var context = lambda.Parameters.Count > 1 ? lambda.Parameters[1] : null;
        var contextObject = Expression.Parameter(typeof(object), "ctx");
        return new Translator(dataset, entity, context, contextObject).Node(lambda.Body);
    }

    private sealed class Translator(DatasetDescriptor dataset, ParameterExpression entity, ParameterExpression? context, ParameterExpression contextObject)
    {
        public Func<object, FilterNode> Node(Expression e)
        {
            switch (e)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } b:
                {
                    var (l, r) = (Node(b.Left), Node(b.Right));
                    var op = b.NodeType == ExpressionType.AndAlso ? LogicalOperator.And : LogicalOperator.Or;
                    return ctx => new LogicalFilter(op, [l(ctx), r(ctx)]);
                }
                case UnaryExpression { NodeType: ExpressionType.Not } u:
                {
                    var inner = Node(u.Operand);
                    return ctx => new NotFilter(inner(ctx));
                }
                case BinaryExpression b when Comparison(b.NodeType) is { } op:
                    return Compare(b, op);
                case MethodCallExpression { Method.Name: "Contains" } call:
                    return Contains(call);
                case MemberExpression when Column(e) is { Kind: ScalarKind.Boolean } c:
                {
                    var node = new ComparisonFilter(new FieldRef(c.PropertyName), QueryOperator.Equal, new ConstantOperand(new BooleanValue(true)));
                    return _ => node;
                }
                default:
                    throw Unsupported(e);
            }
        }

        private Func<object, FilterNode> Compare(BinaryExpression b, QueryOperator op)
        {
            var (left, right) = (Column(b.Left), Column(b.Right));
            if (left is not null && right is not null) throw Unsupported(b);
            if (left is null && right is null) throw Unsupported(b);
            var column = left ?? right!;
            if (left is null) op = Flip(op);
            var value = Value(left is null ? b.Left : b.Right);
            var field = new FieldRef(column.PropertyName);
            return ctx => new ComparisonFilter(field, op, new ConstantOperand(Wrap(value(ctx))));
        }

        private Func<object, FilterNode> Contains(MethodCallExpression call)
        {
            // list.Contains(x.P), Enumerable.Contains(list, x.P), or MemoryExtensions.Contains(span(list), x.P) (C# 14 first-class spans).
            var (source, item) = call.Object is not null ? (call.Object, call.Arguments[0]) : (call.Arguments[0], call.Arguments[1]);
            if (source is MethodCallExpression { Method.Name: "op_Implicit" } conv) source = conv.Arguments[0];
            var column = Column(item) ?? throw Unsupported(call);
            if (Column(source) is not null) throw Unsupported(call);
            var values = Value(source);
            var field = new FieldRef(column.PropertyName);
            return ctx =>
            {
                var list = ((IEnumerable?)values(ctx) ?? Array.Empty<object>()).Cast<object?>().Select(Wrap).ToImmutableArray();
                // Empty set = deny all: an empty OR renders as FALSE.
                return list.IsEmpty ? new LogicalFilter(LogicalOperator.Or, []) : new ComparisonFilter(field, QueryOperator.In, new ListOperand(list));
            };
        }

        private ColumnDescriptor? Column(Expression e)
        {
            while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u) e = u.Operand;
            if (e is not MemberExpression { Member: PropertyInfo p } m || m.Expression != entity) return null;
            return dataset.FindProperty(p.Name) ?? throw new InvalidOperationException(
                $"Row policy on '{dataset.PublicName}' references '{p.Name}', which is not a mapped scalar property.");
        }

        private Func<object, object?> Value(Expression e)
        {
            if (new Finder(entity).Has(e)) throw Unsupported(e);
            Expression body = context is null ? e : new Replacer(context, Expression.Convert(contextObject, context.Type)).Visit(e);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(body, typeof(object)), contextObject).Compile();
        }

        private static QueryValue Wrap(object? value) => value is null ? QueryValue.Null : new ClrValue(value);

        private InvalidOperationException Unsupported(Expression e) =>
            new($"Row policy on '{dataset.PublicName}' contains an untranslatable expression: {e}");

        private static QueryOperator? Comparison(ExpressionType type) => type switch
        {
            ExpressionType.Equal => QueryOperator.Equal,
            ExpressionType.NotEqual => QueryOperator.NotEqual,
            ExpressionType.GreaterThan => QueryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => QueryOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => QueryOperator.LessThan,
            ExpressionType.LessThanOrEqual => QueryOperator.LessThanOrEqual,
            _ => null,
        };

        private static QueryOperator Flip(QueryOperator op) => op switch
        {
            QueryOperator.GreaterThan => QueryOperator.LessThan,
            QueryOperator.GreaterThanOrEqual => QueryOperator.LessThanOrEqual,
            QueryOperator.LessThan => QueryOperator.GreaterThan,
            QueryOperator.LessThanOrEqual => QueryOperator.GreaterThanOrEqual,
            _ => op,
        };
    }

    private sealed class Finder(ParameterExpression target) : ExpressionVisitor
    {
        private bool _found;
        public bool Has(Expression e) { Visit(e); return _found; }
        protected override Expression VisitParameter(ParameterExpression node) { _found |= node == target; return node; }
    }

    private sealed class Replacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
