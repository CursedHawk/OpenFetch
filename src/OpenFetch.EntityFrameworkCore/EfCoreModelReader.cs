using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace OpenFetch.EntityFrameworkCore;

/// <summary>
/// Reads physical metadata from an EF Core model (§8). This decides nothing about exposure: it only describes what exists,
/// and the model builder exposes what attributes/fluent configuration opt in.
/// </summary>
public static class EfCoreModelReader
{
    public static IEnumerable<EntityMapping> Read(IModel model) =>
        model.GetEntityTypes().Where(e => !e.HasSharedClrType).Select(Read);

    private static EntityMapping Read(IEntityType e)
    {
        string? unsupported = null;
        if (e.IsOwned()) unsupported = "owned entity types cannot be datasets";
        else if (e.BaseType is not null || e.GetDerivedTypes().Any()) unsupported = "inheritance hierarchies are not supported";
        else if (e.GetSqlQuery() is not null || e.GetFunctionName() is not null) unsupported = "SQL-query or function mapped entities are not supported";

        var table = e.GetTableName();
        var store = table is not null
            ? StoreObjectIdentifier.Table(table, e.GetSchema())
            : e.GetViewName() is { } view ? StoreObjectIdentifier.View(view, e.GetViewSchema()) : (StoreObjectIdentifier?)null;
        if (store is null)
        {
            unsupported ??= "entity is not mapped to a table or view";
            store = StoreObjectIdentifier.Table(e.ShortName());
        }
        var id = store.Value;

        var properties = e.GetProperties()
            .Select(p => (Property: p, Column: p.GetColumnName(id)))
            .Where(x => x.Column is not null)
            .Select(x => Read(x.Property, x.Column!))
            .ToImmutableArray();

        var navigations = e.GetNavigations().Select(n =>
        {
            var fk = n.ForeignKey;
            var pairs = fk.Properties.Zip(fk.PrincipalKey.Properties, (d, p) => n.IsOnDependent ? (d.Name, p.Name) : (p.Name, d.Name));
            return new NavigationMapping(n.Name, n.TargetEntityType.ClrType, n.IsCollection, [.. pairs]);
        }).ToImmutableArray();

        var indexed = e.GetIndexes().Select(i => i.Properties[0].Name)
            .Concat(e.GetKeys().Select(k => k.Properties[0].Name))
            .ToImmutableHashSet();

        return new EntityMapping
        {
            ClrType = e.ClrType,
            Table = new PhysicalTable(id.Schema, id.Name),
            Properties = properties,
            Key = e.FindPrimaryKey()?.Properties.Select(p => p.Name).ToImmutableArray() ?? [],
            Navigations = navigations,
            IndexedProperties = indexed,
            HasQueryFilter = e.GetDeclaredQueryFilters().Count > 0,
            UnsupportedReason = unsupported,
        };
    }

    private static PropertyMapping Read(IProperty p, string column)
    {
        // Enums and custom conversions surface here: the public CLR type differs from what the provider stores.
        var converter = p.GetValueConverter() ?? p.GetTypeMapping().Converter;
        var clr = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        return new PropertyMapping
        {
            Name = p.Name,
            ClrType = clr,
            Nullable = p.IsNullable,
            IsShadow = p.IsShadowProperty(),
            MaxLength = p.GetMaxLength(),
            Precision = p.GetPrecision(),
            Scale = p.GetScale(),
            Column = new PhysicalColumn(
                column,
                p.GetColumnType(),
                Nullable.GetUnderlyingType(converter?.ProviderClrType ?? clr) ?? converter?.ProviderClrType ?? clr,
                converter?.ConvertToProvider,
                converter?.ConvertFromProvider),
        };
    }
}
