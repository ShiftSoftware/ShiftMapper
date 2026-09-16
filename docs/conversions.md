# Conversions

When two properties share a name and differ in type, ShiftMapper converts rather than gives up. This page is the full table of what converts into what, the exact code each pair emits, what happens at the edges (an out-of-range number, an empty string, a null list), and what the build tells you about each. The README's [What it maps](../README.md#what-it-maps) is the summary; this is the reference behind it.

Everything here follows from one rule, and the rule explains every refusal: **a conversion's answer must come from the two types alone.** Not the machine's culture, not its time zone, not the order somebody declared an enum's members in. A conversion that would need one of those is refused and reported (SM0002), because a map that means one thing on a laptop and another on the server is the failure this library exists to prevent.

Two things hold across the whole page:

- **Two backends.** Every conversion has an in-memory spelling (what `Map` runs) and a query spelling (what `ProjectTo` hands to EF Core). Where they differ, both are shown. Where a conversion has no SQL form at all, this page says so — the build does not always.
- **Nothing is silently defaulted.** A pair with no conversion leaves the destination property unmapped *and reported*. The diagnostic is the contract.

The generated file fully qualifies every name (`global::ShiftMapper.ValueConverter.ToInvariantString(...)`); the snippets below drop the prefix for readability.

## How a pair is decided

`ConversionResolver.Resolve` tries these steps in order and stops at the first that answers. The order is load-bearing in three places, called out below.

1. **Types nobody can reason about** — `dynamic`, pointers, unresolved types — are refused first. `dynamic` has to go first: the compiler reports an implicit conversion to it from every type, so one `dynamic` property left in would swallow whatever it was pointed at and silence the SM0002 it should have produced.
2. **A global conversion you registered** (`CreateConversion<TSource, TDestination>`) — *ahead* of the built-in table, so a rule written for a pair that already converts (`long` to `string`, which is what a hash-id rule is) is honoured rather than silently ignored. See [Extending the table](#extending-the-table).
3. **Collections of simple values**, then **dictionaries** — ahead of the identity test on purpose. A `List<int>` filling a `List<int>` is *copied*, not assigned; letting identity win there would make one collection mapping share the entity's list while every other one copies.
4. **The same type** — a plain assignment. Nullable reference *annotations* are ignored, so `string?` to `string` is a copy. Note that for a reference type this copies the reference: `Child` to `Child` hands the DTO the same object, and only collections and dictionaries (step 3) are rebuilt.
5. **The refused pairs** — see [What is refused](#what-is-refused-sm0002). Before everything below, so a later step that only sees part of the picture cannot pick one up.
6. **C# already converts implicitly** — widening, lifting, an `implicit operator` you wrote — assigned across, nothing said. Reference conversions and boxing are excluded here; see the refusals for why.
7. **To text**, then **from text**.
8. **A nullable source into a non-nullable value destination** — unwrapped with `GetValueOrDefault()`, then the rest resolved recursively. This comes *before* the cast, so `int?` to `int` never becomes `(int)source.X`, which is a perfectly valid C# conversion that throws on every null.
9. **A cast** between numbers and enums.
10. **Between the date and time types**, which C# gives no conversions of its own.

Falls off the end: no conversion. The pair is then offered to the nested-object path (`Product` to `ProductDto`), and if it is not two objects either, it is SM0002.

## The table

### Assigned straight across

No code, no diagnostic. The generated line is `Value = source.Value`.

| Source | Destination | Why |
|---|---|---|
| `int` | `int` | identical |
| `string` | `string` | identical, and deliberately *not* read as a collection of `char` |
| `Status` | `Status` | identical enum |
| `int` | `long`, `decimal`, `double` | implicit widening |
| `float` | `double` | implicit widening |
| `char` | `int` | implicit in C# |
| `int` | `int?` | lifting |
| `int?` | `long?` | lifted widening |
| `Money` | `decimal` (and back) | a user-defined `implicit operator` is taken at its word |

One thing worth knowing: the implicit conversions that *do* lose precision — `long` to `double`, `int` to `float` — go through here too, with no diagnostic. Warning about them would mean warning about a line the developer could write by hand without a squeak from the compiler; ShiftMapper follows the language rather than second-guessing it.

### Casts: numbers and enums

Anything C# classes as an *explicit* numeric conversion is, by construction, a destination that cannot hold every value the source can — an `int` to a `long` is implicit and was taken three steps earlier. So the numeric rows are all SM0010 (Warning). The enum rows are SM0008 (Info), because converting by value is what an enum cast is *for*.

| Source | Destination | Emitted | Out-of-range value | Diagnostic |
|---|---|---|---|---|
| `long` | `int` | `unchecked((int)source.Value)` | wraps round | SM0010 |
| `int` | `byte` | `unchecked((byte)source.Value)` | wraps round | SM0010 |
| `int` | `char` | `unchecked((char)source.Value)` | wraps round | SM0010 |
| `double` | `int` | `unchecked((int)source.Value)` | unspecified; `NaN` becomes `0` | SM0010 |
| `double` | `float` | `(float)source.Value` | precision is lost | SM0010 |
| `decimal` | `int` | `(int)source.Value` | **throws `OverflowException`** | SM0010 |
| `double` | `decimal` | `(decimal)source.Value` | precision is lost; outside `decimal`'s range throws `OverflowException` | SM0010 |
| `Status` | `int` | `unchecked((int)source.Value)` | — | SM0008 |
| `int` | `Status` | `unchecked((Status)source.Value)` | nothing checks that the result is a declared member | SM0008 |

The same cast is emitted in the projection.

**Why `unchecked` is written, and why not everywhere.** `(int)source.Big` truncates in an ordinary project and throws `OverflowException` in one built with `<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>` — the same generated line, two behaviours, decided by a csproj setting the generated file knows nothing about. Pinning it makes every map behave the same everywhere, which is what lets SM0008 and SM0010 describe the behaviour at all. It is written only where it changes the meaning: whole-number destinations (including enums and `char`) from anything but `decimal`. The `decimal` conversions ignore the checked context entirely and throw regardless, and writing `unchecked` there would promise a truncation that never happens.

**Three different stories for an out-of-range value**, and the SM0010 message tells you which one applies:

- Whole number to whole number *wraps*: `long` 4,000,000,001 arrives in an `int` as −294,967,295. The sample keeps this pair on purpose — `Brand.ExternalIds` is a `List<long>`, `BrandDto.ExternalIds` a `List<int>` — and the build reports it as SM0010 on `BrandDto.ExternalIds`. The seeded ids stay inside `int` range (migration `BrandExternalIdsWithinIntRange`; brand 1 holds 10010 and 10011), because `BrandDto` is also reached by nested projections and SQL Server raises an arithmetic overflow error rather than wrapping; put an id above `int.MaxValue` into `SeedData.cs` to watch `Map` wrap it.
- Floating point to whole number is *unspecified* by the language: `unchecked((int)1e20)` lands on `int.MaxValue`, `unchecked((byte)300.0)` on `0`, and `NaN` on `0`. The message says "unspecified" because that is the honest word, and telling a developer that a `double` "wraps round" would send them looking for the wrong bug.
- Anything involving `decimal` *throws*. That is the one narrowing that fails loudly, and the message says so.

An enum cast always reports SM0008, whatever the widths involved — the reinterpretation is the point, and the note says that nothing checks the result is a member the enum declares. One enum to a *different* enum is refused outright; see below.

### To text

Any value type that can format itself fills a `string`. It cannot fail, so nothing is reported. In memory the call is `ValueConverter.ToInvariantString(source.Value)`; in a projection it is `source.Value.ToString()`.

| Source | Text produced | Format |
|---|---|---|
| `int`, `long`, `decimal`, `double`, the other numeric types | `1234.5` | the type's general format, invariant culture |
| `bool` | `True` / `False` | `bool.ToString()` — not JSON's lowercase |
| `char` | a one-character string | |
| any enum | the member *name*, e.g. `Active` | |
| `Guid` | `d3b07384-d9a0-4f7e-9c3b-2a1f0e5d6c7b` | the dashed form |
| `DateTime` | `2026-08-21T14:30:00.0000000Z` | `"O"` — keeps the sub-second digits and the `Kind` |
| `DateTimeOffset` | `2026-08-21T14:30:00.0000000+03:00` | `"O"` |
| `DateOnly` | `2026-08-21` | `"O"` |
| `TimeOnly` | `14:30:00.0000000` | `"O"` |
| `TimeSpan` | `1.02:30:00` | `"c"` |
| any `T?` of the above | `null` stays `null` | not `""`, not `"0"` |
| a struct of your own implementing `IFormattable` | whatever it formats to | format `null`, invariant culture |

**Text never depends on the machine.** Every format provider is `CultureInfo.InvariantCulture`, so `1234.5` is the same five characters in Berlin and in London. A DTO is something you serialise and send elsewhere; a value formatted with the server's culture would travel as `1234,5` from one server and fail to parse on the next. The date and time types use `"O"` rather than their default format because the default throws information away — `08/21/2026 14:30:00` has lost the sub-second digits and the `Kind`, so a UTC timestamp would read back as local time.

**In a projection, the database decides the format.** The query spelling is the ordinary `ToString()` EF's translators are written against, and what comes back is the database's own `CONVERT` — `CONVERT(varchar(11), [p].[BrandId])` on SQL Server. That is not the invariant-culture guarantee the in-memory call makes. For integers the two agree; for a `bool`, a `DateTime` or a `decimal` with trailing zeros, expect `Map` and `ProjectTo` to produce different text for the same value. If the text has to be identical across both backends, keep the property typed and let your serialiser write it.

**What does not convert to text:** reference types, even ones implementing `IFormattable`. A destination `string` would otherwise swallow *any* source property by calling `ToString()` on it, and `"ShiftMapper.Sample.Entities.Product"` is not a mapping anybody asked for. `object` to `string` is SM0002.

### From text

The mirror of the above, and the one direction that can fail on *data* rather than on types — which is why every row is SM0009 (Info). A `string` fills any value type implementing `IParsable<T>`, plus enums and `char`.

| Destination | Emitted | Absent text (`null`, `""`, whitespace) | Text that does not parse |
|---|---|---|---|
| `int`, `long`, `decimal`, `double`, `bool`, `Guid`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, your own `IParsable` struct | `ValueConverter.Parse<T>(source.Value, "Source.Value -> Destination.Value")` | `default` — `0`, `false`, `Guid.Empty`, `DateOnly.MinValue` | throws |
| any `T?` of those | `ValueConverter.ParseOrNull<T>(...)` | `null` | throws |
| `DateTime` / `DateTime?` | `ValueConverter.ParseDateTime(...)` / `ParseDateTimeOrNull(...)` | `default` / `null` | throws |
| any enum / `TEnum?` | `ValueConverter.ParseEnum<TEnum>(...)` / `ParseEnumOrNull<TEnum>(...)` | `default` / `null` | throws |
| `char` / `char?` | `ValueConverter.ParseChar(...)` / `ParseCharOrNull(...)` | `null` and `""` only — `" "` is a value | throws, including for more than one character |

The rules, and the reasons:

- **Nothing means default; nonsense throws.** A null, empty or whitespace-only string is *absence*, so it becomes `default` — or `null` when the destination is nullable, which is the difference between an empty cell arriving as `0` (a real quantity) and as `null` ("nobody filled this in"). A string with content that does not parse is *bad data*, and throws: turning `"abc"` into `0` would put the problem in your database instead of your logs.
- **The exception names the map.** Every parse call carries the property pair as a string literal, so the message reads `ShiftMapper: cannot convert "abc" to Int32 while mapping StockDto.Id -> Stock.Id. See the inner FormatException for the underlying reason. Either correct the source data, or give the destination property a type ShiftMapper does not have to parse into.` It is always a `FormatException` — even when the BCL threw `OverflowException` — so there is one type to catch around a mapping call; the original is the inner exception. The quoted text is truncated at 60 characters, without splitting a surrogate pair.
- **`char` is the one exception to the whitespace rule**, deliberately: for a character, `" "` *is* the value, and collapsing it to `'\0'` would throw data away. Text of more than one character is rejected rather than truncated to its first character — a two-character value in a one-character column is bad data, not a value to trim.
- **`DateTime` has its own reader** because the plain `IParsable` parse converts what it reads into local time — a UTC timestamp written by the to-text side would come back three hours out on a UTC+3 server. `ParseDateTime` uses `DateTimeStyles.RoundtripKind`, so the `Kind` that was written is the `Kind` that is read. `DateTimeOffset` does *not* need this: it carries its offset in the value, so the plain invariant parse already round-trips `+03:00`, `Z` and `-05:00`.
- **Enums parse by name or by number**, and the name match ignores case: `"active"`, `"Active"` and `"1"` all give `Status.Active`. The text usually came from a column or a query string rather than from C#, and rejecting `"active"` would be pedantry rather than safety. Absent text gives `default(TEnum)` — whichever member has the value `0`, or no declared member at all if none does.
- **Text converts into value types only.** `System.Net.IPAddress` implements `IParsable<IPAddress>`, and without this rule a `string` would map onto a non-nullable `IPAddress` property — and an empty column would fill it with `null`, the only "absent" a reference type has, surfacing as a `NullReferenceException` far from the map that caused it. So a `string` to a reference type is SM0002.

**This is the first of two families with no query form** — the date and time helpers below are the other. Parsing is a `ValueConverter` call in the projection too — `Id = ValueConverter.Parse<int>(source.Id, "StockDto.Id -> Stock.Id")` appears verbatim in the generated `StockDto` to `Stock` projection — because there is no BCL spelling of "parse this with invariant culture and name the map on failure" that a database runs. No diagnostic says so. What EF does with a call it has no translator for is EF's rule, not ShiftMapper's: in the outermost `Select` it can evaluate the call on the client after the row is read; anywhere it would have to become SQL it fails at run time with a translation error. In practice parsing runs on write maps — DTO to entity, as `POST /api/stocks` does — which nobody projects.

### Between the date and time types

C# provides no conversions among `DateOnly`, `TimeOnly`, `DateTime` and `TimeSpan`. ShiftMapper supplies the three that have exactly one sensible answer and refuses the rest.

| Source | Destination | Emitted | Result | Diagnostic |
|---|---|---|---|---|
| `DateOnly` | `DateTime` | `ValueConverter.ToDateTime(source.Value)` | midnight, `DateTimeKind.Unspecified` | — |
| `DateTime` | `DateOnly` | `ValueConverter.ToDateOnly(source.Value)` | the time of day is discarded | SM0008 |
| `TimeOnly` | `TimeSpan` | `ValueConverter.ToTimeSpan(source.Value)` | the duration since midnight | — |
| `TimeSpan` | `TimeOnly` | — | refused: a negative duration, or one of a day or more, has no time of day, and `TimeOnly.FromTimeSpan` throws on it — a runtime failure outside the one place ShiftMapper is allowed to fail on data | SM0002 |
| `DateTime` | `DateTimeOffset` | — | refused: implicit in C#, but it attaches `TimeZoneInfo.Local` to any `Unspecified` value — which is every `DateTime` read from a database | SM0002 |
| `DateTimeOffset` | `DateTime` | — | refused: "drop the offset" and "convert to UTC first" are both defensible; picking one silently would be wrong half the time | SM0002 |

Each helper has a nullable twin chosen by overload, so a `DateOnly?` source into a `DateTime?` destination keeps its nulls. Like parsing, these have no query form: the projection carries the same `ValueConverter.ToDateOnly(...)` call, with the consequences described above.

### Nullable, in both directions

| Source | Destination | Emitted | Diagnostic |
|---|---|---|---|
| `int` | `int?` | `source.Value` | — |
| `int?` | `int` | `source.Value.GetValueOrDefault()` | SM0008: "a null source becomes the destination type's default value" |
| `int?` | `long` | `source.Value.GetValueOrDefault()` | SM0008 |
| `DateTime?` | `DateTime` | `source.Value.GetValueOrDefault()` | SM0008 |
| `long?` | `int` | `unchecked((int)source.Value.GetValueOrDefault())` | SM0010 — null becomes `0` *and* the rest narrows; the narrowing is the half worth a warning |
| `long?` | `int?` | `unchecked((int?)source.Value)` | SM0010 — a lifted cast; `null` stays `null` |
| `int?` | `string` | `ValueConverter.ToInvariantString(source.Value)` | — ; `null` stays `null`, not `""` |
| `string` | `int?` | `ValueConverter.ParseOrNull<int>(...)` | SM0009; absent text stays `null` |
| `DateTime?` | `DateOnly` | `ValueConverter.ToDateOnly(source.Value.GetValueOrDefault())` | SM0008: "a null source becomes the destination type's default value, and the time of day is discarded" |

The unwrap is written once and every other rule works lifted for free: the recursion resolves the *inner* pair and wraps `GetValueOrDefault()` around it, and the diagnostic note is composed from both halves. Note the asymmetry between the two text rows. A nullable value going *out* to text keeps its absence, because the to-text step runs before the unwrap and `ToInvariantString` has nullable overloads; only a non-nullable *value* destination has to invent a default.

`GetValueOrDefault()` is emitted in the projection too.

### Collections of simple values

**Read from** anything that is an `IEnumerable<T>` — arrays, every BCL collection, an unevaluated query. A type that implements `IEnumerable<T>` for two different `T` is not treated as a collection (there is no way to know which sequence was meant), and only rank-1 arrays count. A `string` is never read as an `IEnumerable<char>`.

**Built as** one of the shapes ShiftMapper knows how to construct. The list is deliberately shorter than the list it can read:

| Destination | Built with | Concrete type produced |
|---|---|---|
| `T[]` | `ToArrayOrEmpty` | array |
| `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` | `ToListOrEmpty` | `List<T>` |
| `HashSet<T>`, `ISet<T>`, `IReadOnlySet<T>` | `ToHashSetOrEmpty` | `HashSet<T>` — SM0008, duplicates are discarded |
| anything else — `Stack<T>`, `Queue<T>`, a collection type of your own | — | SM0002 |

**Elements** must be value types or `string` on both sides (a registered global conversion counts too), and they convert by recursion into this same table — so every scalar row above applies per element, and a collection reports what its elements report, once for the property.

| Source | Destination | Emitted (in memory) | Diagnostic |
|---|---|---|---|
| `List<int>` | `List<int>` | `ValueConverter.ToListOrEmpty(source.Value)` — a copy, not the same list | — |
| `List<int>` | `IReadOnlyList<int>`, `int[]` | `ToListOrEmpty(source.Value)`, `ToArrayOrEmpty(source.Value)` | — |
| `List<int>` | `HashSet<int>`, `ISet<int>`, `IReadOnlySet<int>` | `ToHashSetOrEmpty(source.Value)` | SM0008 |
| `List<int>` | `IReadOnlyList<long>` | `ToListOrEmpty<int, long>(source.Value, static item => item)` | — |
| `List<long>` | `List<int>` | `ToListOrEmpty<long, int>(source.Value, static item => unchecked((int)item))` | SM0010 |
| `List<int>` | `List<string>` | `ToListOrEmpty<int, string>(source.Value, static item => ValueConverter.ToInvariantString(item))` | — |
| `List<string>` | `List<int>` | `ToListOrEmpty<string, int>(source.Value, static item => ValueConverter.Parse<int>(item, "Source.Value -> Destination.Value"))` | SM0009 |
| `List<int?>` | `List<int>` | `ToListOrEmpty<int?, int>(source.Value, static item => item.GetValueOrDefault())` | SM0008 |
| `List<long>` | `HashSet<int>` | the converting `ToHashSetOrEmpty` | SM0010, carrying both notes |
| `string` | `char[]` (and back) | — | SM0002 |
| `List<int>` | `Stack<int>` | — | SM0002 |
| `List<Product>` | `List<ProductDto>` | not a conversion — the nested-mapping path, which needs a `CreateMap` for the element pair (SM0011 without one) | |

A null *element* follows the scalar rules: a null `string` in a `List<string>` filling a `List<int>` arrives as `0`.

**Always a new collection**, even when the source could have been assigned across, as a `List<T>` can be to an `IEnumerable<T>`. Two reasons, and the second is the one that matters: assigning the entity's list to the DTO *shares* it, so adding to the DTO afterwards adds to the entity EF is tracking; and an `IEnumerable<T>` may not be a collection at all but an unevaluated query, which a DTO would then re-run on every enumeration and throw on once the `DbContext` is disposed. `POST /api/supplier-feeds/preview` returns `ownsItsOwnData: true` as proof of the copy.

**Why the type arguments and the identity lambda.** Generics are invariant: a `List<int>` is not a `List<long>` however freely an `int` becomes a `long`. So elements go through the converting overload whenever the types differ at all, and both type arguments are written out — inference reads `TDestination` from what the lambda returns, so `item => item` over a `List<int>` would infer `List<int>` again and the generated file would not compile. `static` on the lambda forbids capturing, which lets the compiler cache the delegate instead of allocating one per map; it is dropped only where the element goes through a global conversion, which reaches the mapper's own instance.

#### Null collections and `AllowNullCollections`

A null source collection becomes an **empty** destination collection. That is the default — per map, `CreateMap<A, B>(o => o.AllowNullCollections = true)` asks for the other answer, or set it once for a whole mapper in `ConfigureDefaults`. The README's [Null collections](../README.md#null-collections) has the reasoning; this is what it does to the generated code.

In memory the policy is one method name — `ToListOrEmpty` versus `ToList` — and nothing else about the map changes:

| Policy | `List<int>` to `int[]` emits |
|---|---|
| default | `ValueConverter.ToArrayOrEmpty(source.Value)` |
| `AllowNullCollections = true` | `ValueConverter.ToArray(source.Value)` — a null source stays null |

Two method families rather than a `bool` parameter, because the generated line then *says* which policy is in force without a signature open beside it. The `OrEmpty` builders return non-nullable types, which is half the point: a DTO built through them has no collection property that can be null.

**In a projection** the policy is a real question rather than a method name, and it is answered *only where a null can actually arrive*:

| Source annotation | Policy | Projection emits |
|---|---|---|
| `List<long>?` | default | `Enumerable.ToList<int>(Enumerable.Select<long, int>((source.Value ?? Enumerable.Empty<long>()), item => unchecked((int)item)))` |
| `int[]?` to `IReadOnlyList<int>` (assignable) | default | `(source.Value ?? (IReadOnlyList<int>)new List<int>())` |
| `List<long>` (not annotated) | default | `Enumerable.ToList<int>(Enumerable.Select<long, int>(source.Value, ...))` — **no guard** |
| `List<int>?` | `AllowNullCollections = true` | `source.Value` — no guard |

The non-annotated case is unguarded on purpose, and it cost a working query to learn. EF recognises a primitive collection by the shape of the expression around it, and `x ?? empty` is a shape it does not see through: a `Select` over a JSON column that translates on its own stops translating once wrapped, and the developer gets a runtime "could not be translated" for a guard they never asked for. `GET /api/products` goes through `Brand.ExternalIds`, which is exactly this shape. So the nullable annotation on the source property is load-bearing — it is the developer's own statement about the column. Declare a collection nullable when it really is, and both backends answer alike: `GET /api/brands` and `GET /api/brands/projected` return the same `aliases` for the brands whose column is null. The generated projection carries the guard as `(source.Aliases ?? (IReadOnlyList<string>)new List<string>())` and leaves `ExternalIds` unguarded; `?sql=true` shows how your provider renders each (the sample documents SQL Server's `OUTER APPLY OPENJSON([b].[ExternalIds])` shape on `BrandHashDto`, `GET /api/brands/hashed?sql=true`).

Navigation collections — collections of mapped *objects* — need no guard in a projection at all: EF materialises no rows as an empty collection rather than a null.

#### The query spelling

`ValueConverter`'s builders are ShiftMapper's own methods and no database runs them, so the projection uses the LINQ shape a developer would have hand-written, which is exactly what EF's translators recognise:

| Case | In memory | In a projection |
|---|---|---|
| same elements, assignable shape (`List<int>` to `List<int>` or `IReadOnlyList<int>`) | `ToListOrEmpty(source.Value)` | `source.Value` — straight across; there is no entity to share with, EF builds a fresh collection per row |
| same elements, shape must change (`List<int>` to `int[]`) | `ToArrayOrEmpty(source.Value)` | `Enumerable.ToArray(source.Value)` |
| elements convert (`List<int>` to `List<string>`) | `ToListOrEmpty<int, string>(source.Value, static item => ValueConverter.ToInvariantString(item))` | `Enumerable.ToList<string>(Enumerable.Select<int, string>(source.Value, item => item.ToString()))` |

The per-element conversion swaps its spelling by the scalar rules: a cast stays a cast, to-text becomes `ToString()`, and a parse stays a `ValueConverter.Parse` call. `static` comes off the lambda in a tree, where it is meaningless.

### Dictionaries

A dictionary is a collection with two element types, and everything above applies to it: it is copied rather than shared, it answers the null-collection policy, and its keys and values convert *independently* by the ordinary rules.

**Read from** any `IEnumerable<KeyValuePair<K, V>>` — `Dictionary`, `IDictionary`, `IReadOnlyDictionary`, `SortedDictionary`, `ConcurrentDictionary`, a plain list of pairs. **Built as** `Dictionary<K, V>`, `IDictionary<K, V>` or `IReadOnlyDictionary<K, V>`; the concrete type is always `Dictionary<K, V>`. Keys and values must be simple — value types or `string`, or a registered pair.

| Source | Destination | Emitted (in memory) | Diagnostic |
|---|---|---|---|
| `Dictionary<string, int>` | `Dictionary<string, int>`, `IDictionary<string, int>`, `IReadOnlyDictionary<string, int>` | `ValueConverter.ToDictionaryOrEmpty(source.Value)` — a copy | — |
| `SortedDictionary<string, int>` | `Dictionary<string, int>` | `ToDictionaryOrEmpty(source.Value)` | — |
| `Dictionary<string, int>` | `Dictionary<string, string>` | `ToDictionaryOrEmpty<string, int, string, string>(source.Value, static key => key, static value => ValueConverter.ToInvariantString(value))` | — |
| `Dictionary<string, int>` | `Dictionary<string, long>` | the converting overload, with the identity on both lambdas | — ; keys untouched, so nothing to report |
| `Dictionary<int, string>` | `Dictionary<string, string>` | `ToDictionaryOrEmpty<int, string, string, string>(source.Value, static key => ValueConverter.ToInvariantString(key), static value => value)` | SM0008 — keys converted |
| `Dictionary<long, string>` | `Dictionary<int, string>` | keys narrowed | SM0010, with the collision note as well |
| `Dictionary<string, string>` | `Dictionary<string, int>` | values parsed | SM0009 |
| `Dictionary<string, int>` | `SortedDictionary<string, int>` | — | SM0002: readable, not buildable |
| `Dictionary<string, Child>` | `Dictionary<string, ChildDto>` | — | SM0002: nested mapping does not reach dictionaries yet, and half-converting one — a fresh dictionary holding the entity's own `Child` instances — is the outcome the simple-element rule exists to prevent |

**Converting the keys is the one thing a dictionary can lose that a list cannot.** Two source keys can convert to the same destination key — two `long`s narrowing onto one `int`, `"A"` and `"a"` arriving as the same text — and then the *last one wins*, the same answer `ToHashSet` gives for duplicate values and for the same reason: throwing halfway through building a DTO is a worse answer. So any map whose key type is converted carries an SM0008 note, even for `int` to `string` where a collision cannot actually happen; the rule is about converting keys, not about the two types. Leave the key type alone and nothing is reported — a dictionary cannot collide with itself.

The identity `key => key` is not dead code, for the same invariance reason as lists: a `Dictionary<string, int>` never becomes a `Dictionary<string, string>` however freely an `int` becomes text, so the whole dictionary is rebuilt, and the key lambda is what settles the destination's key type for the compiler rather than for type inference.

**In a projection**, the same-types-and-assignable case is handed straight across — with `?? (IReadOnlyDictionary<K, V>)new Dictionary<K, V>()` when the source is annotated nullable under the default policy — and everything else is `Enumerable.ToDictionary<KeyValuePair<string, int>, string, string>(source.Value, item => item.Key, item => item.Value.ToString())`. Whether a provider can turn that into SQL is EF's answer to give. In practice dictionaries arrive as keyed payloads rather than columns, which is why the sample demonstrates them on a request shape: `POST /api/supplier-feeds/preview` converts values (`prices`), converts keys (`notes`, the SM0008 pair) and empties a missing dictionary (`extras`) through one `CreateMap<SupplierFeed, SupplierFeedDto>()`.

## What is refused (SM0002)

SM0002 says "does not convert", not "cannot be converted", and the wording is deliberate: some of these pairs C# converts quite happily, and ShiftMapper still declines because the answer would depend on something other than the two types. Claiming they were impossible would be a lie the developer could disprove in one line.

| Pair | Why |
|---|---|
| `DateTime` to `DateTimeOffset` | the offset would come from the machine's time zone, not from the data |
| `DateTimeOffset` to `DateTime` | dropping the offset and converting to UTC are both defensible; neither is chosen |
| one enum to a *different* enum | a cast pairs them by number, so inserting a member into the middle of one silently changes the meaning, with no compile error and no exception. Mapping by name would be right, and is a feature with its own failure modes rather than a line in this table |
| `TimeSpan` to `TimeOnly` | a negative duration, or one of a day or more, has no time of day; the conversion throws on ordinary data |
| a user-defined `explicit operator` (`Guarded` to `int`) | its author chose the keyword that says stop and think; having a generator apply it silently inverts what the keyword says. `implicit` operators are honoured for the mirror-image reason |
| `bool` to `int`, `int` to `bool` | no conversion exists in C# either |
| `int` to `object`, `Child` to `object`, anything to an interface | moving a reference around — boxing, up-casting — hands the DTO a reference to the very entity it was meant to be a copy of, lazy-loading proxy and cycles included |
| `object` to `Product`, unboxing | a downcast compiles and throws on every value that is not really that type — a coin toss, not a mapping |
| `string` to `char[]` and back | a `string` is an `IEnumerable<char>` and is never treated as one |
| `string` to a reference type; a reference type to `string` | text converts to and from value types only |
| `List<int>` to `Stack<int>`, `Dictionary<K, V>` to `SortedDictionary<K, V>` | a shape ShiftMapper can read but not build |
| `Dictionary<string, Child>` to `Dictionary<string, ChildDto>` | objects inside a dictionary |
| `dynamic` on either side | refused first, so it cannot swallow anything |

The message names both types — `'BrandDto.Founded' is not mapped because ShiftMapper does not convert 'DateTime' to 'DateTimeOffset'` — and the destination property keeps its default value.

**Not every "no" from this table is the last word.** When both sides are objects of your own — an ordinary class or struct, not `object`, not an interface, not abstract, not a BCL type — the pair goes to the nested-mapping path instead, where the answer is the `CreateMap` you declared for it, or an SM0011 *error* if there is none. A member typed as an interface or abstract class is not an object that path recognises, so it stays SM0002. Two *different* BCL types are SM0002 too, rather than an SM0011 demanding a `CreateMap` for them: a mapper is for your types, and a `Uri` on one side and a `string` on the other is better described as "not converted" than as "map missing". The same BCL type on both sides is an identity and copies the reference.

**A runtime too old for the generator** also produces SM0002. The runtime library and the generator are separate references, and `ValueConverter.ConverterApiVersion` (currently `3`) is how the generator checks that the runtime underneath it is new enough to call: `1` covers the scalar text and date/time helpers, `2` the collection builders, `3` dictionaries and the `OrEmpty` family. A project on an older runtime loses exactly the conversions that runtime cannot serve — reported as SM0002 — instead of a CS0117 inside a generated file it cannot edit; the conversions that are plain C# (widening, casts, `GetValueOrDefault()`) keep working because they call nothing. Shipping both halves in one package on one version number is what makes this a defence rather than a daily occurrence.

## Extending the table

Three ways past a refusal, in the order of precedence the generator applies:

1. **`ForMember(d => d.X, opt => opt.MapFrom(s => ...))`** — a decision about one member. It takes that member *out* of the conversion table: SM0002, SM0008, SM0009 and SM0010 stop being reported for it, and your expression must return the destination member's type. **`opt.MapFromSource(s => ...)`** returns the *source* type and lets the table convert it — keeping the invariant-culture guarantee that `s.Price.ToString()` inside a `MapFrom` would lose. Both project. See the README's [Per-member options](../README.md#per-member-options).
2. **`CreateConversion<TSource, TDestination>(memory, query)`** — a decision about a type pair, once, for every map *of the mapper that declares it*, applied ahead of the built-in table and inside collections, dictionaries and nested maps. `memory` is a delegate `Map` calls; `query` is an expression tree *inlined* into the projection, which is what lets `GET /api/invoices/stamps?sql=true` format a `DateTime` inside the `SELECT`. Omitting `query` declares the pair unprojectable, and every map touching it loses its projection — reported as SM0030 (Warning) at build time, and demonstrated by `GET /api/products/fingerprints?project=true`, whose projection throws a message naming the pair. Registered pairs match by assignability, nearest by inheritance winning. A rule several mappers should share goes in a **pack** (`ShiftMapperConversions`), added to one mapper with `AddConversions<T>()` or to every mapper of a registration with `o.AddConversions<T>()`. See the README's [Packs](../README.md#packs-rules-shared-between-mappers).
3. **The built-in table** on this page.

**Which rule answers is decided by distance.** For a map declared by mapper P: P's own `CreateConversion`, then the packs P added, then — when P was included by M — M's own and M's packs, then the packs the registration gave every mapper, then this table. Two packs at the same distance claiming one pair is SM0031, an error, unless something nearer settles it. The generated call names the scope that answered — `Customizations.Conversion<long, string>(typeof(PlatformConversions))` — and the runtime looks in exactly that store, so the generator and the runtime cannot disagree about which rule ran.

A registered pair counts as "simple" for collection and dictionary elements, so a rule for `Money` to `string` fills a `List<string>` as readily as a single member. Inside a collection the element lambda loses its `static` — the conversion reaches the mapper's own `Customizations` — and in the projection it is emitted as `MapCustomizations.Splice<TSource, TDestination>(item, typeof(Scope))`, a marker that `Compose` replaces with the registered tree.

## The diagnostics, side by side

| Id | Severity | Fires when | The note in the message |
|---|---|---|---|
| SM0002 | Warning | no conversion exists, or a refused one would | — |
| SM0008 | Info | something is dropped *by design*, predictably, for every value | `a null source becomes the destination type's default value`; `the time of day is discarded`; `an enum and a number convert by value, and nothing checks that the result is a member the enum actually declares`; `duplicate values are discarded, so the destination can hold fewer items than the source`; `two source keys that convert to the same destination key collapse into one, so the destination can hold fewer entries than the source` |
| SM0009 | Info | the destination is filled by parsing text at run time | — |
| SM0010 | Warning | an *ordinary* value can come out different, with nothing in the code to say so and no exception when it does | `a value outside the destination type's range wraps round rather than being rejected`; `a value outside the destination type's range gives an unspecified result rather than an error, and NaN becomes zero`; `a value too large for the destination throws OverflowException`; `precision is lost, and a value outside decimal's range throws OverflowException`; `precision is lost` |

Notes compose. A `List<long>` to `HashSet<int>` reports one SM0010 reading "duplicate values are discarded, so the destination can hold fewer items than the source, and for each element, a value outside the destination type's range wraps round rather than being rejected"; a `DateTime?` to `DateOnly` reports one SM0008 reading "a null source becomes the destination type's default value, and the time of day is discarded". Collections and dictionaries report once per property, at the loudest severity any part earned.

SM0008 and SM0009 are informational because they are the feature doing what it was asked: the developer wrote two types that differ and asked ShiftMapper to cope, and a warning on every one of them would train people to ignore the warnings. They show in the IDE and do not appear in console build output at any verbosity (a clean `dotnet build -v diag` of the sample prints none of them), and they do not count as build warnings. SM0010 is a warning because nothing else marks the loss. Tune any of them per folder in `.editorconfig` — `dotnet_diagnostic.SM0010.severity = error` where a wrong id is unacceptable, `= none` where you have decided the range is safe — but the real fix is almost always to give the destination the source's type.

## Worked examples in the sample

The generated file for the sample's `AppMapper` is checked in under `ShiftMapper.Sample/Generated/`; these are the lines to look for.

`Brand` to `BrandDto` — four conversions in one map, and both spellings of each:

```csharp
// MapToBrandDto — in memory
FoundedYear = ValueConverter.ToInvariantString(source.FoundedYear),           // int -> string
Tags        = ValueConverter.ToListOrEmpty(source.Tags),                      // List<string> -> IReadOnlyList<string>, copied
ExternalIds = ValueConverter.ToListOrEmpty<long, int>(source.ExternalIds,
                  static item => unchecked((int)item)),                       // List<long> -> List<int>, SM0010
Aliases     = ValueConverter.ToListOrEmpty(source.Aliases),                   // List<string>? -> empty, never null

// the projection
FoundedYear = source.FoundedYear.ToString(),
Tags        = source.Tags,                                                    // assignable and not annotated: straight across
ExternalIds = Enumerable.ToList<int>(Enumerable.Select<long, int>(source.ExternalIds, item => unchecked((int)item))),
Aliases     = (source.Aliases ?? (IReadOnlyList<string>)new List<string>()),  // annotated nullable: guarded
```

- `GET /api/brands` and `GET /api/brands/projected` — the same list from each backend. Brands 1 and 2 are seeded with aliases; brands 3 to 8 have a null `Aliases` column and arrive as `aliases: []` from both endpoints. `externalIds` come through unchanged, because the seeded ids sit inside `int` range.
- `GET /api/brands/projected?sql=true` — the `COALESCE` on `Aliases`, and the absence of one on `ExternalIds`.
- `GET /api/products` — the conversions above still working three levels down one projection: `foundedYear` text over an `int`, the stock's `id` text over an `int`, `bayNumbers` strings over `int`s.
- `POST /api/stocks` — the reverse direction, `StockDto` to `Stock`: `Id = ValueConverter.Parse<int>(source.Id, "StockDto.Id -> Stock.Id")` and `BayNumbers` parsed element by element, both SM0009. A non-numeric `id` in the body throws the `FormatException` described above, naming `StockDto.Id -> Stock.Id`.
- `POST /api/supplier-feeds/preview` — dictionaries: values converted, keys converted (SM0008), a missing dictionary arriving as `{}`, and `ownsItsOwnData` proving the copy.
- `GET /api/invoices/stamps?sql=true` and `GET /api/products/fingerprints?project=true` — a pack's conversion with a query form, and one without (SM0030).
