/**
 * Reference query builder for the OpenFetch JSON query protocol (language 1.0).
 * Generic over a row type (e.g. `components["schemas"]["OrdersRow"]` from openapi-typescript): field paths and comparison
 * values are type-checked. The server remains authoritative for capabilities, operators and limits.
 */

type Primitive = string | number | boolean | null | undefined;
export type Scalar = string | number | boolean | null;
type Depth = [never, 0, 1, 2, 3];

/** Dotted paths to scalar fields, following to-one nested objects (never arrays: collections use any/expand). */
export type FieldPath<T, D extends number = 3> = [D] extends [never] ? never
  : T extends object ? {
      [K in keyof T & string]-?: NonNullable<T[K]> extends Primitive ? K
        : NonNullable<T[K]> extends readonly unknown[] ? never
        : `${K}.${FieldPath<NonNullable<T[K]>, Depth[D]>}`
    }[keyof T & string]
  : never;

/** Value type at a dotted path. */
export type FieldValue<T, P extends string> =
  P extends `${infer H}.${infer R}` ? H extends keyof T ? FieldValue<NonNullable<T[H]>, R> : never
  : P extends keyof T ? T[P] : never;

/** Names of collection (array-valued) relationships, and the element row type of one. */
export type CollectionName<T> = { [K in keyof T & string]-?: NonNullable<T[K]> extends readonly unknown[] ? K : never }[keyof T & string];
export type CollectionRow<T, K extends keyof T> = NonNullable<T[K]> extends readonly (infer E)[] ? E : never;

export type ComparisonOperator = "eq" | "ne" | "gt" | "gte" | "lt" | "lte" | "in" | "notIn" | "between" | "notBetween" | "contains" | "startsWith" | "endsWith";
export type Filter =
  | { type: "comparison"; field: string; operator: ComparisonOperator; value: Scalar | Scalar[] }
  | { type: "logical"; operator: "and" | "or"; children: Filter[] }
  | { type: "not"; operand: Filter }
  | { type: "any"; relationship: string; where?: Filter };

export type Order = { field: string; direction: "asc" | "desc" };
export type AggregateFunction = "count" | "countDistinct" | "sum" | "avg" | "min" | "max";
export type Expansion = { relationship: string; select: string[]; where?: Filter; orderBy?: Order[]; first?: number };

/** A complete OpenFetch query document. */
export interface QueryDocument {
  version: "1.0";
  select?: string[];
  where?: Filter;
  orderBy?: Order[];
  page?: { first?: number; after?: string; offset?: number };
  distinct?: boolean;
  groupBy?: string[];
  aggregates?: { function: AggregateFunction; field?: string; alias: string }[];
  having?: Filter;
  expand?: Expansion[];
}

type Value<T, P extends string> = Exclude<FieldValue<T, P>, undefined>;
type NonNull<T, P extends string> = Exclude<FieldValue<T, P>, undefined | null>;

/** Typed filter helpers for a row type. Collections are filtered with `any` on the related row type. */
export interface Filters<T> {
  eq<P extends FieldPath<T>>(field: P, value: Value<T, P>): Filter;
  ne<P extends FieldPath<T>>(field: P, value: Value<T, P>): Filter;
  gt<P extends FieldPath<T>>(field: P, value: NonNull<T, P>): Filter;
  gte<P extends FieldPath<T>>(field: P, value: NonNull<T, P>): Filter;
  lt<P extends FieldPath<T>>(field: P, value: NonNull<T, P>): Filter;
  lte<P extends FieldPath<T>>(field: P, value: NonNull<T, P>): Filter;
  in<P extends FieldPath<T>>(field: P, values: [NonNull<T, P>, ...NonNull<T, P>[]]): Filter;
  notIn<P extends FieldPath<T>>(field: P, values: [NonNull<T, P>, ...NonNull<T, P>[]]): Filter;
  between<P extends FieldPath<T>>(field: P, low: NonNull<T, P>, high: NonNull<T, P>): Filter;
  notBetween<P extends FieldPath<T>>(field: P, low: NonNull<T, P>, high: NonNull<T, P>): Filter;
  contains<P extends FieldPath<T>>(field: P, value: NonNull<T, P> & string): Filter;
  startsWith<P extends FieldPath<T>>(field: P, value: NonNull<T, P> & string): Filter;
  endsWith<P extends FieldPath<T>>(field: P, value: NonNull<T, P> & string): Filter;
  and(...children: [Filter, ...Filter[]]): Filter;
  or(...children: [Filter, ...Filter[]]): Filter;
  not(operand: Filter): Filter;
  /** At least one related row matches; `not(any(...))` = none; `not(any(r, f => f.not(...)))` = all. */
  any<K extends CollectionName<T>>(relationship: K, where?: (f: Filters<CollectionRow<T, K>>) => Filter): Filter;
}

export function filters<T>(): Filters<T> {
  const cmp = (operator: ComparisonOperator) => (field: string, value: unknown): Filter =>
    ({ type: "comparison", field, operator, value: value as Scalar | Scalar[] });
  return {
    eq: cmp("eq"), ne: cmp("ne"), gt: cmp("gt"), gte: cmp("gte"), lt: cmp("lt"), lte: cmp("lte"),
    in: cmp("in"), notIn: cmp("notIn"),
    between: (field: string, low: unknown, high: unknown) => cmp("between")(field, [low, high]),
    notBetween: (field: string, low: unknown, high: unknown) => cmp("notBetween")(field, [low, high]),
    contains: cmp("contains"), startsWith: cmp("startsWith"), endsWith: cmp("endsWith"),
    and: (...children: Filter[]) => ({ type: "logical", operator: "and", children }),
    or: (...children: Filter[]) => ({ type: "logical", operator: "or", children }),
    not: (operand: Filter) => ({ type: "not", operand }),
    any: (relationship: string, where?: (f: Filters<never>) => Filter) =>
      where ? { type: "any", relationship, where: where(filters<never>()) } : { type: "any", relationship },
  } as unknown as Filters<T>;
}

/** Builder for one expansion of a collection relationship (fields of the related row only). */
export class ExpansionBuilder<R> {
  private readonly doc: Omit<Expansion, "relationship"> = { select: [] };
  select(...fields: (keyof R & string)[]): this { this.doc.select.push(...fields); return this; }
  where(build: (f: Filters<R>) => Filter): this { this.doc.where = build(filters<R>()); return this; }
  orderBy(field: keyof R & string, direction: "asc" | "desc" = "asc"): this { (this.doc.orderBy ??= []).push({ field, direction }); return this; }
  first(n: number): this { this.doc.first = n; return this; }
  /** @internal */ toJSON(relationship: string): Expansion { return { relationship, ...this.doc }; }
}

/** Fluent builder producing an OpenFetch query document for rows of type T. */
export class QueryBuilder<T> {
  private readonly doc: QueryDocument = { version: "1.0" };

  select(...fields: FieldPath<T>[]): this { (this.doc.select ??= []).push(...fields); return this; }
  where(build: (f: Filters<T>) => Filter): this { this.doc.where = build(filters<T>()); return this; }
  orderBy(field: FieldPath<T> | (string & {}), direction: "asc" | "desc" = "asc"): this { (this.doc.orderBy ??= []).push({ field, direction }); return this; }
  first(n: number): this { (this.doc.page ??= {}).first = n; return this; }
  /** Continue after a previous page's `endCursor`. */
  after(cursor: string): this { (this.doc.page ??= {}).after = cursor; return this; }
  offset(n: number): this { (this.doc.page ??= {}).offset = n; return this; }
  distinct(): this { this.doc.distinct = true; return this; }

  expand<K extends CollectionName<T>>(relationship: K, build: (b: ExpansionBuilder<CollectionRow<T, K>>) => ExpansionBuilder<CollectionRow<T, K>>): this {
    (this.doc.expand ??= []).push(build(new ExpansionBuilder<CollectionRow<T, K>>()).toJSON(relationship));
    return this;
  }

  groupBy(...fields: FieldPath<T>[]): this { (this.doc.groupBy ??= []).push(...fields); return this; }
  aggregate(fn: "count", alias: string): this;
  aggregate(fn: AggregateFunction, field: FieldPath<T>, alias: string): this;
  aggregate(fn: AggregateFunction, fieldOrAlias: string, alias?: string): this {
    (this.doc.aggregates ??= []).push(alias === undefined ? { function: fn, alias: fieldOrAlias } : { function: fn, field: fieldOrAlias, alias });
    return this;
  }
  /** Filter on aggregate aliases and grouped fields (untyped: aliases are defined by the query). */
  having(build: (f: Filters<Record<string, Scalar>>) => Filter): this { this.doc.having = build(filters<Record<string, Scalar>>()); return this; }

  build(): QueryDocument {
    if (this.doc.select && (this.doc.groupBy || this.doc.aggregates)) throw new Error("Aggregate queries return groupBy fields and aliases; do not call select().");
    if (this.doc.page?.after !== undefined && this.doc.page.offset !== undefined) throw new Error("after() and offset() cannot be combined.");
    return structuredClone(this.doc);
  }
}

export const query = <T>() => new QueryBuilder<T>();
