using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace OpenFetch.EntityFrameworkCore;

/// <summary>
/// Reads physical metadata from an EF Core model (§8). This decides nothing about exposure: it only describes what exists,
/// and the model builder exposes what attributes/fluent configuration opt in.
/// Inheritance: TPH -> discriminator type filter, TPT -> base tables joined on the key, TPC -> UNION ALL of concrete tables.
/// Owned references (table splitting) are flattened into dotted owner properties; owned collections become internal entities.
/// </summary>
public static class EfCoreModelReader
{
    public static IEnumerable<EntityMapping> Read(IModel model) =>
        model.GetEntityTypes().Where(e => !e.HasSharedClrType && !IsOwnedReference(e)).Select(Read);

    private static bool IsOwnedReference(IEntityType e) => e.IsOwned() && e.FindOwnership() is { IsUnique: true };

    private static StoreObjectIdentifier? Store(IEntityType e) =>
        e.GetTableName() is { } table ? StoreObjectIdentifier.Table(table, e.GetSchema())
        : e.GetViewName() is { } view ? StoreObjectIdentifier.View(view, e.GetViewSchema())
        : null;

    private static PhysicalTable Table(StoreObjectIdentifier s) => new(s.Schema, s.Name);

    private static EntityMapping Read(IEntityType e)
    {
        string? unsupported = null;
        if (e.GetSqlQuery() is not null || e.GetFunctionName() is not null) unsupported = "SQL-query or function mapped entities are not supported";

        var root = e.GetRootType();
        var hierarchy = e.BaseType is not null || e.GetDerivedTypes().Any();
        var strategy = hierarchy ? root.GetMappingStrategy() ?? RelationalAnnotationNames.TphMappingStrategy : null;

        var store = Store(e);
        var properties = ImmutableArray.CreateBuilder<PropertyMapping>();
        var inherited = ImmutableArray<InheritedTable>.Empty;
        var union = ImmutableArray<UnionBranch>.Empty;
        (string, ImmutableArray<object>)? typeFilter = null;
        PhysicalTable main;

        if (strategy == RelationalAnnotationNames.TpcMappingStrategy
            && e.GetDerivedTypesInclusive().Where(t => !t.IsAbstract()).ToList() is var concrete && (concrete.Count != 1 || concrete[0] != e))
        {
            // TPC with subtypes: UNION ALL of every concrete table; columns are aliased to property names.
            var stores = concrete.Select(t => Store(t)!.Value).ToList();
            union = [.. stores.Select(s => new UnionBranch(Table(s),
                e.GetProperties().ToFrozenDictionary(p => p.Name, p => p.GetColumnName(s) ?? p.Name)))];
            main = union.IsEmpty ? new PhysicalTable(null, e.ShortName()) : union[0].Table;
            if (union.IsEmpty) unsupported ??= "abstract TPC type without concrete subtypes";
            foreach (var p in e.GetProperties()) properties.Add(Read(p, p.Name, 0));
        }
        else if (store is { } s)
        {
            main = Table(s);
            var levels = new List<IEntityType> { e };
            if (strategy == RelationalAnnotationNames.TptMappingStrategy)
            {
                // TPT: each base type with its own table is joined 1:1 on the primary key.
                for (var b = e.BaseType; b is not null; b = b.BaseType) levels.Add(b);
                var key = e.FindPrimaryKey()!.Properties;
                inherited = [.. levels.Skip(1).Select(b => Store(b)!.Value).Select(bs =>
                    new InheritedTable(Table(bs), [.. key.Select(k => (k.GetColumnName(s)!, k.GetColumnName(bs)!))]))];
            }
            else if (strategy == RelationalAnnotationNames.TphMappingStrategy && e.BaseType is not null && root.FindDiscriminatorProperty() is { } disc)
            {
                // TPH: a derived dataset only sees rows of its own type and subtypes.
                typeFilter = (disc.Name, [.. e.GetDerivedTypesInclusive().Where(t => !t.IsAbstract()).Select(t => t.GetDiscriminatorValue()).OfType<object>()]);
            }

            foreach (var p in e.GetProperties())
            {
                var level = p.IsPrimaryKey() ? 0 : Math.Max(0, levels.IndexOf((IEntityType)p.DeclaringType));
                var levelStore = level == 0 ? s : Store(levels[level])!.Value;
                if (p.GetColumnName(levelStore) is { } column) properties.Add(Read(p, column, level));
            }
            AddOwned(e, levels, s, "", properties);
        }
        else
        {
            unsupported ??= "entity is not mapped to a table or view";
            main = new PhysicalTable(null, e.ShortName());
        }

        var navigations = e.GetNavigations()
            .Where(n => !(n.ForeignKey.IsOwnership && n.ForeignKey.IsUnique))
            .Select(n =>
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
            Table = main,
            Properties = properties.ToImmutable(),
            Key = e.FindPrimaryKey()?.Properties.Select(p => p.Name).ToImmutableArray() ?? [],
            Navigations = navigations,
            IndexedProperties = indexed,
            HasQueryFilter = root.GetDeclaredQueryFilters().Count > 0,
            UnsupportedReason = unsupported,
            InheritedTables = inherited,
            Union = union,
            TypeFilter = typeFilter,
            IsOwnedCollection = e.IsOwned(),
        };
    }

    /// <summary>Flattens owned references stored in the owner's table (table splitting) into dotted properties, recursively.</summary>
    private static void AddOwned(IEntityType owner, List<IEntityType> levels, StoreObjectIdentifier ownerStore, string prefix, ImmutableArray<PropertyMapping>.Builder properties)
    {
        foreach (var nav in owner.GetNavigations().Where(n => n.ForeignKey.IsOwnership && n.ForeignKey.IsUnique && !n.IsOnDependent))
        {
            var owned = nav.TargetEntityType;
            var level = Math.Max(0, levels.IndexOf((IEntityType)nav.DeclaringEntityType));
            var store = level == 0 ? ownerStore : Store(levels[level])!.Value;
            if (Store(owned) is not { } ownedStore || ownedStore != store) continue; // owned type in its own table: not supported as columns
            var optional = !nav.ForeignKey.IsRequiredDependent;
            foreach (var p in owned.GetProperties().Where(p => !p.IsPrimaryKey()))
                if (p.GetColumnName(store) is { } column)
                {
                    var mapping = Read(p, column, level);
                    properties.Add(mapping with
                    {
                        Name = prefix + nav.Name + "." + p.Name,
                        Nullable = mapping.Nullable || optional,
                        DeclaringType = owned.ClrType,
                    });
                }
            AddOwned(owned, [owned], store, prefix + nav.Name + ".", properties);
        }
    }

    private static PropertyMapping Read(IProperty p, string column, int table)
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
                converter?.ConvertFromProvider,
                table),
        };
    }
}
