# OpenFetch 1.0 value profile

OpenFetch preserves native JSON types (spec §12). Extended scalar types are JSON strings, and the server interprets them using the column's trusted metadata. A string is never read as a field reference. To compare two fields, use `compareTo`, which is only available when `features.fieldComparison` is enabled.

| Public type | JSON | Accepted input | Output |
|---|---|---|---|
| `string` | string | any string | as stored |
| `integer` | number | integral number within the column's CLR range (`int32`/`int64`, ...) | number |
| `decimal` | number | any JSON number, parsed exactly as `System.Decimal` from its source text | number, exact |
| `number` | number | finite IEEE-754 double (`1e400` is rejected) | number; non-finite stored values are written as strings |
| `boolean` | `true`/`false` | JSON boolean only | boolean |
| `guid` | string | 36-character hyphenated form `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | lower-case, same form |
| `dateTime` | string | RFC 3339 `yyyy-MM-ddTHH:mm:ss[.f{1,7}](Z\|±hh:mm)`; **the offset is mandatory** | UTC, `Z` suffix, trailing zero fractions removed |
| `date` | string | `yyyy-MM-dd` | same |
| `time` | string | `HH:mm:ss` or `HH:mm:ss.f{1,7}` | same |
| `enum` | string | camelCase member name, case-sensitive (`open`, not `Open` or `0`) | camelCase member name |

Rules:

- **Offsets.** A `dateTime` value with an offset is normalised to UTC before it becomes a parameter. `2026-01-05T00:00:00+02:00` compares equal to `2026-01-04T22:00:00Z`.
- **Null.** `null` is only allowed with `eq` and `ne`, and only on nullable fields. `eq null` compiles to `IS NULL` and `ne null` to `IS NOT NULL`.
- **Three-valued logic.** Other comparisons follow SQL three-valued logic: `ne "CZ"` does not match rows whose value is null.
- **Arrays.** `in`/`notIn` take a non-empty array of up to `maxInValues` scalars. `between`/`notBetween` take exactly `[low, high]`, both bounds inclusive.
- **String operators.** `contains`, `startsWith` and `endsWith` are case-sensitive under the column's database collation; there is no provider-neutral case-insensitive mode. `%`, `_` and `\` in the value are matched literally.
- **Encoding.** Documents must be valid UTF-8 without duplicate property names. Strings containing lone surrogates (for example `"\uDD1E"`) are rejected as `invalid_json`.
