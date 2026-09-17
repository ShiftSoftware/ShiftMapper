# Diagnostics

Every ShiftMapper message is `SM####`, reported by a `DiagnosticAnalyzer` rather than by the generator, so `.editorconfig` tunes them per folder and the IDE shows them live. Forty-one rules in use, SM0001 to SM0046 (SM0029, SM0039, SM0040, SM0041 and SM0045 are retired); eight stop the build (SM0011, SM0012, SM0016, SM0028, SM0031, SM0035, SM0042, SM0044). The [README table](../README.md#diagnostics) is the one-line summary; this page gives each rule what it is protecting, what is still generated, and how to answer it.

<a id="sm0001"></a>
## SM0001 — Destination property is not mapped

**Warning.** The destination has a property and the source has nothing with that name — not by exact match, not by the case-insensitive fallback, and not by [flattening](../README.md#flattening-and-naming-conventions), which is on by default and only ever runs after the direct match has failed. The property keeps its default value in both `Map` and `ProjectTo`.

```csharp
public class Source      { public int Id { get; set; } }
public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0001: 'Destination.Name' is not mapped because 'Source' has no readable property named 'Name'
}
```

**Fix.** Three honest answers, and the message points at the `CreateMap` so you can add one line to it:

- Add (or rename) a property on the source so the names line up.
- Supply the value: `.ForMember(d => d.Name, opt => opt.MapFrom(s => ...))`.
- Acknowledge it: `.ForMember(d => d.Name, opt => opt.Ignore())`. That is a decision written where the next reader sees it, not a silencer — the IDE offers it as the lightbulb "Ignore 'Name'" (see [Code fixes](../README.md#code-fixes)).

Lowering the severity in `.editorconfig` is for folders where unmapped members are the norm (test doubles, say), not for an individual property.

<a id="sm0002"></a>
## SM0002 — ShiftMapper does not convert between the two types

**Warning.** The names match and the types do not, and nothing in the conversion table bridges them. The property is left unmapped in both backends. Note the wording — *does not convert*, not *cannot*: some pairs really have no conversion (`bool` to `int`, `Stream` to `Uri`); others are refused on purpose because the right answer would come from something other than the two types. Those are listed under [What it deliberately refuses](../README.md#what-it-deliberately-refuses): `DateTime`/`DateTimeOffset` either way, one enum to a different enum, `TimeSpan` to `TimeOnly`, and any user-defined *explicit* operator.

```csharp
public class Source      { public bool Flag { get; set; } }
public class Destination { public int  Flag { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0002: 'Destination.Flag' is not mapped because ShiftMapper does not convert 'bool' to 'int'
}
```

A pair of *objects* you could map yourself (`Product` to `ProductDto`) is not SM0002 — that is SM0011, which asks for the map. SM0002 is for value types, framework types, and the pairs above.

**Fix.**

- Give the destination the source's type — the usual answer.
- Convert it yourself on that member: `.ForMember(d => d.Flag, opt => opt.MapFrom(s => s.Flag ? 1 : 0))`.
- Convert it everywhere with a [global type-pair conversion](../README.md#global-type-pair-conversions): `CreateConversion<bool, int>(memory: b => b ? 1 : 0, query: b => b ? 1 : 0)`. A registered pair wins over the built-in table and reaches every map, collections included; omit `query` and every map touching the pair loses its projection (SM0030).
- Or acknowledge it with `opt.Ignore()`.

<a id="sm0003"></a>
## SM0003 — Destination property's setter is not public

**Warning.** The property looks assignable and is not: its setter is `private`, `protected` or `internal`. Generated code lives outside your type and assigns only through a public setter, so the property is skipped in both backends. A property with *no* setter at all is skipped silently — that is a choice the DTO's author made — and an `init` accessor counts as public: it is set in the object initializer.

```csharp
public class Source      { public int Id { get; set; } }
public class Destination { public int Id { get; internal set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0003: 'Destination.Id' is not mapped because its setter is not public
}
```

**Fix.** Make the setter public, or make it `init` if the value is settled at construction. If the property really is not for the mapper to write, `opt.Ignore()` says so — or remove the setter entirely and the warning goes with it.

<a id="sm0004"></a>
## SM0004 — Destination type cannot be created by ShiftMapper

**Warning.** There is no constructor ShiftMapper can call at all: the destination is abstract, an interface, or every constructor is non-public. This is the "nobody can build it from outside" case. A public constructor that exists but cannot be *filled* is SM0013, which names the parameter.

```csharp
public class Source { public int Id { get; set; } }

public abstract class Destination { public int Id { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0004: no Map method was generated to create 'Destination' because it has no constructor ShiftMapper can call
}
```

**What is still generated.** The update overload `Map(source, existingDestination)` — copying onto an object you were handed is still possible, and is emitted. The create method and the projection are not, so `Map<Destination>(source)` and `ProjectTo<Destination>` do not exist for this pair.

**Fix.**

- Map to a concrete type, or give the abstract type a public constructor.
- For an interface or abstract destination with one concrete implementation, redirect: `CreateMap<Brand, BrandDto>(); CreateMap<Brand, IBrandDto>().As<BrandDto>();` — the redirection projects too, because the concrete type is fixed at compile time.
- `ConstructUsing(s => ...)` if only your code knows how to build it; see SM0015 for what that costs.

<a id="sm0005"></a>
## SM0005 — No mapping code was generated for this mapper

**Warning.** The class derives from `ShiftMapperBase`, so it was clearly meant to be a mapper class, but the generated mapper does not include it. Two reasons, and the message names which: the class — or a type it is nested inside — is generic, and an open generic type has no type arguments to construct it with; or the project's discovery is `MapperDiscovery.Registered` and no `o.AddMapper<...>()` names the class. Nothing else about a mapper class's shape matters: it need not be `partial`, nor nested in `partial` types, because nothing is generated onto it.

```csharp
public class AppMapper<T> : ShiftMapperBase          // generic
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0005: no mapping code was generated for 'AppMapper' because generic mapper classes are not supported
}
```

Nothing it declares is generated, and no diagnostics are reported about the maps inside it.

**Fix.** Make the class non-generic — if the maps genuinely vary by a type argument, declare the closed pairs in a non-generic class — or, under `Registered`, name the class in the `AddShiftMapper` call (or choose a discovery mode that takes it).

<a id="sm0006"></a>
## SM0006 — Destination property is not mapped by the reverse map

**Info.** SM0001's quieter twin, for the map that `ReverseMap()` added. A DTO is normally a subset of its entity, so mapping back always leaves entity-only members untouched — keys the client never sends, audit columns, navigation collections. Reporting each as a warning would make `ReverseMap` unusable on exactly the shape it exists for, so this is informational: visible in the IDE, absent from console build output at any verbosity, and not counted as a build warning. The forward direction stays under SM0001.

```csharp
public class Source      { public int Id { get; set; } public string Only { get; set; } = ""; }
public class Destination { public int Id { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>().ReverseMap();
    // info SM0006: the reverse map leaves 'Source.Only' unmapped because 'Destination' has no readable property named 'Only'
}
```

**Fix.** Usually nothing — it is describing the expected shape. The reverse map takes its own configuration after `ReverseMap()`, so `.ReverseMap().ForMember(d => d.Only, opt => opt.MapFrom(...))` fills it and `opt.Ignore()` acknowledges it. To have these enforced where it matters, raise it: `dotnet_diagnostic.SM0006.severity = warning` (see [Tuning them](../README.md#tuning-them)).

<a id="sm0007"></a>
## SM0007 — Destination property matches more than one source property when case is ignored

**Warning.** Only reachable under the default `PropertyMatching.CaseInsensitive`, and only when no exact match settled it first: the source has two properties whose names differ only by case, so either could be meant. Picking one would be a coin toss with your data, so nothing is mapped and the message lists the candidates.

```csharp
public class Source      { public string Sku { get; set; } = ""; public string SKU { get; set; } = ""; }
public class Destination { public string sku { get; set; } = ""; }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0007: 'Destination.sku' is not mapped because 'Source' has several properties matching 'sku' when case is ignored (SKU, Sku)
}
```

**Fix.** Give the destination the exact name of the one you mean — exact matches are tried first in both modes, so `Sku` on the destination finds `Sku` and never `SKU`. Or rename one of the source properties. Or turn the fallback off for this map, `CreateMap<Source, Destination>(o => o.Matching = PropertyMatching.CaseSensitive)` — or for the whole mapper by overriding `ConfigureDefaults` — after which the property is reported as a plain SM0001. `opt.Ignore()` acknowledges it as usual.

<a id="sm0008"></a>
## SM0008 — Mapped through a conversion that can lose information

**Info.** The property *is* mapped, and the conversion drops something by design, for every value it is given: a `DateTime` becoming a `DateOnly` loses its time of day, an enum becoming its number loses its name, a `HashSet` discards duplicates, a null becomes the destination's default. The message tail says what is dropped. Informational because this is the conversion doing what it says; a warning on every one would train you to ignore ShiftMapper's warnings. Contrast SM0010, where an *ordinary* value can come out wrong.

```csharp
public class Source      { public DateTime Moment { get; set; } }
public class Destination { public DateOnly Moment { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // info SM0008: 'Destination.Moment' is mapped by converting 'DateTime' to 'DateOnly', which can lose information (the time of day is discarded)
}
```

**Fix.** Nothing to fix if the narrowing is what you want; it is here so the loss is a decision you have seen. Give the two properties the same type if you would rather it did not happen. Both backends perform the same conversion.

<a id="sm0009"></a>
## SM0009 — Mapped by parsing text at runtime

**Info.** The destination is a number, `bool`, `Guid`, enum, `char`, or date/time type, and the source is a `string`, so the property is filled by parsing text when the map runs. Every other conversion is settled at compile time; this is the one place a map can fail on *data* rather than on types.

```csharp
public class Source      { public string Count { get; set; } = ""; }
public class Destination { public int    Count { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // info SM0009: 'Destination.Count' is filled by parsing text when the map runs, so source text that does not parse throws
}
```

**What happens at runtime.** Null, empty and whitespace-only text becomes the destination's default (or null, when the destination is nullable — an `int?` keeps "nobody filled this in" distinct from `0`). Anything else that does not parse throws a `FormatException` naming the two properties (`Source.Count -> Destination.Count`) rather than quietly mapping a zero. Parsing is culture-invariant. The rules are in `ShiftMapper.ValueConverter`.

**Fix.** Store the value as its real type if you can. Otherwise accept it, or take control of the tolerant case with `opt.MapFrom(s => int.TryParse(s.Count, out var n) ? n : -1)`. For `ProjectTo`, the projection carries the same `ValueConverter.Parse` call — a static method in this library, which no database provider has a translator for — so a parsed member belongs in a map you `Map` rather than project.

<a id="sm0010"></a>
## SM0010 — Mapped through a conversion that can change the value

**Warning.** The property is mapped, and the destination cannot hold every value the source can: a `long` of 9,000,000,000 arrives in an `int` as 410,065,408, a `decimal` price arrives in a `double` with digits gone. For values inside the destination's range the conversion is exact; outside it the result is silently wrong rather than rejected, and nothing in the code or at runtime marks it. That silence is why this is a warning where SM0008 is a note. It fires once per property for collections too — a `List<long>` onto a `List<int>` narrows every element.

```csharp
public class Source      { public long Big { get; set; } }
public class Destination { public int  Big { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0010: 'Destination.Big' is mapped by converting 'long' to 'int', which cannot hold every value the source can
    //                 (a value outside the destination type's range wraps round rather than being rejected)
    // generated: Big = unchecked((int)source.Big)
}
```

The message tail tells the two stories apart: integer-to-integer *wraps*; floating point to integer gives an unspecified result and turns NaN into zero; anything to `decimal` throws `OverflowException` out of range.

**Fix.** Give the destination the source's type — the real fix. Once you have decided the range is safe, `dotnet_diagnostic.SM0010.severity = none` for that folder, or `= error` to make it impossible to inherit. `opt.Ignore()` is not an answer here: the property is mapped and you want it to be.

**Both backends, different failure.** `Map` wraps in C#. In a projection the same narrowing is done by the database, and SQL Server raises an arithmetic overflow error rather than wrapping. `ShiftMapper.Sample` keeps a live instance — `Brand.ExternalIds` is a `List<long>` and `BrandDto.ExternalIds` a `List<int>` — with the reasoning in the comments of `AppMapper`.

<a id="sm0011"></a>
## SM0011 — Nested object property has no map

**Error.** The names match, both sides are objects ShiftMapper could map (or collections of them), and nothing in this mapper declares the pair. A warning would let the build through with the nested object null, and a null nested object in a response looks exactly like a null in the database — so the build stops instead. The generated file still compiles; the member is simply absent from both `Map` and `ProjectTo` until you decide.

```csharp
public class Child    { public int Id { get; set; } }
public class ChildDto { public int Id { get; set; } }

public class Source      { public Child    Item { get; set; } = new(); }
public class Destination { public ChildDto Item { get; set; } = new(); }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // error SM0011: 'Destination.Item' needs a map from 'Child' to 'ChildDto'. Add CreateMap<Child, ChildDto>(),
    //               or CreateMap<ChildDto, Child>().ReverseMap(), or .ForMember(d => d.Item, opt => opt.Ignore()) ...
}
```

**Fix.** Two ways forward, both one line, and either way the decision is written where a reader can find it:

- Declare the map: `CreateMap<Child, ChildDto>();`. The IDE offers this as the lightbulb "Add CreateMap<Child, ChildDto>()". A map registered by `ReverseMap()` counts the same as one registered by `CreateMap`, so when the opposite direction already exists, adding `.ReverseMap()` to it usually reads better than a second declaration — a judgement the lightbulb leaves to you. A map in another part of the same `partial` mapper counts too.
- Leave it alone on purpose: `.ForMember(d => d.Item, opt => opt.Ignore())`.

A nested pair that arrives through a constructor argument (a record taking its `ChildDto` as a parameter) gets the same verdict. Turning analyzers off (`<RunAnalyzers>false</RunAnalyzers>`) still generates the code, silently — see [Tuning them](../README.md#tuning-them).

<a id="sm0012"></a>
## SM0012 — Nested object mapping is circular

**Error.** The declared maps nest each other in a loop, so there is no depth at which the graph is complete. Generated code that followed it would recurse until the stack ran out — and a `StackOverflowException` takes the process down rather than failing one request. Cutting at some arbitrary depth would replace the crash with a response whose shape depends on a number nobody chose. So the loop is refused, the message spells it out as the properties it is made of, and the edge that closes it is cut so the generated file still compiles.

```csharp
public class Brand   { public List<Product> Products { get; set; } = new(); }
public class Product { public Brand Brand { get; set; } = new(); }

public class BrandDto   { public List<ProductDto> Products { get; set; } = new(); }
public class ProductDto { public BrandDto Brand { get; set; } = new(); }

public class AppMapper : ShiftMapperBase
{
    public AppMapper()
    {
        CreateMap<Brand, BrandDto>();
        CreateMap<Product, ProductDto>();
        // error SM0012: nested mapping never finishes — BrandDto.Products -> ProductDto.Brand -> BrandDto. Break the loop with ...
    }
}
```

A type whose DTO holds another of the same DTO (`NodeDto.Next`) is the same loop with one map in it, and is caught the same way.

**Fix.** Ignore whichever side is the back-reference — `CreateMap<Product, ProductDto>().ForMember(d => d.Brand, opt => opt.Ignore())` — and the rest of the graph maps as normal, in both backends. Which of the two types is the view and which is the thing being viewed is a decision only you can make, which is why there is no default.

<a id="sm0013"></a>
## SM0013 — A constructor parameter cannot be filled

**Warning.** The destination has a public constructor and one of its parameters has nothing to fill it: no source property of that name, and no `ForMember` for the member it stands for. The message names the parameter, which is the whole reason this is not SM0004. Where several constructors are offered, it describes the one that came closest (fewest unfillable parameters).

```csharp
public class Source { public int Id { get; set; } }
public record Destination(int Id, DateTime CreatedAt);

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0013: no Map method was generated to create 'Destination' because its constructor parameter 'CreatedAt' (DateTime) cannot be filled from 'Source'
}
```

**What is still generated.** As for SM0004: no create method and no projection. The update overload is emitted where the destination has anything assignable — a positional record has nothing, so it gets no update method either.

**Fix.** A constructor parameter is a destination member that happens to be written inside the parentheses, so the same rules apply: give the source a property of that name, or fill it with `ForMember` on the member the parameter stands for — on a positional record that is the same name: `.ForMember(d => d.CreatedAt, opt => opt.MapFrom(s => DateTime.UtcNow))`. If you switched this map to `PropertyMatching.CaseSensitive`, check the spelling: `ID` no longer finds `Id`. See [Records, primary constructors and `required` members](../README.md#records-primary-constructors-and-required-members).

<a id="sm0014"></a>
## SM0014 — A required member is not mapped

**Warning.** A `required` member has nothing filling it. C# refuses an object initializer that leaves a required member out (CS9035), so this is not one property left empty — the whole destination cannot be built. Saying so here is the difference between one sentence and a compiler error inside a generated file you cannot open.

```csharp
public class Source { public int Id { get; set; } }

public class Destination
{
    public required int    Id      { get; set; }
    public required string Missing { get; set; }
}

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Source, Destination>();
    // warning SM0014: no Map method was generated to create 'Destination' because its required member 'Missing' (string) is not mapped
}
```

**What is still generated.** No create method and no projection, as for SM0004 and SM0013; the update overload where there is anything assignable.

**Fix.**

- Map it: a source property named `Missing`, or `.ForMember(d => d.Missing, opt => opt.MapFrom(s => ...))`.
- Put `[SetsRequiredMembers]` on a constructor that fills the required members itself; the C# compiler takes that promise at its word, and so does this.
- Drop `required` if the member is not really required.

`opt.Ignore()` is **not** enough here, and the warning stays if you try: ignoring says "do not assign it", and the object still cannot be built without it.

<a id="sm0015"></a>
## SM0015 — `ConstructUsing`: the map cannot be projected

**Info.** The map builds its destination with `ConstructUsing`, so `Map` works and `ProjectTo` cannot: a projection has to reach EF as one expression, and there is no general way to graft the mapped members onto an object a delegate returned. Informational because this is the feature doing what it says, not a mistake — it is here so "why does `ProjectTo` throw for this one map" is answered at build time.

```csharp
public class Source { public int Id { get; set; } }

public class Destination
{
    public Destination(int id) => Id = id;
    public int Id { get; }
}

public class AppMapper : ShiftMapperBase
{
    public AppMapper() =>
        CreateMap<Source, Destination>()
            .ConstructUsing(s => new Destination(s.Id));
    // info SM0015: the map from 'Source' to 'Destination' builds its destination with ConstructUsing, so ProjectTo cannot use it; Map is unaffected
}
```

**What happens if you project anyway.** The projection member is still generated, and throws an `InvalidOperationException` naming this map and what to do instead — rather than being absent, which would surface as a CS0103 in a generated file for any map that nests this one. The call site is also reported (SM0037). `ShiftMapper.Sample` demonstrates it: `GET /api/invoices/{id}/label` maps in memory; add `?project=true` to see the refusal. That map also carries an `AfterMap`, so the build reports SM0018 beside this SM0015 and the thrown message names the hook rather than `ConstructUsing`.

**Fix.** If you need the projection, let ShiftMapper pick the constructor and say the same thing with `ForMember` on the members the arguments stand for — records and primary constructors project fine that way. If the construction genuinely needs your code (a service, a clock), keep `ConstructUsing` and `Map` that pair; there is nothing to acknowledge.

<a id="sm0016"></a>
## SM0016 — Member cannot be given a `Condition`

**Error.** A `Condition` guards an assignment, and this member's value is settled while the object is being created, so there is no assignment to guard. Three shapes, and the message says which: the member is `init`-only; it is `required` and this map builds the destination with an object initializer, which C# refuses to leave a required member out of; or it is filled by a constructor argument, which cannot be left out. An error because both ways of ignoring the request diverge silently — assign anyway and the condition never fires on a create; skip it and the member is never mapped.

```csharp
public class Source      { public string Name { get; set; } = ""; }
public class Destination { public string Name { get; init; } = ""; }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() =>
        CreateMap<Source, Destination>()
            .ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));
    // error SM0016: 'Destination.Name' cannot be given a Condition because it is init-only, so its value is settled when the object is created, so there is nothing to leave untouched
}
```

The generated file still compiles — the member is emitted unconditioned — so a project with analyzers off gets the member mapped rather than a CS error in a file it cannot edit.

**Fix.** Drop the `Condition`, or give the member an ordinary public setter. One exception worth knowing: on a `ConstructUsing` map the generator writes no object initializer, so a `required` member with a setter *is* conditionable there.

<a id="sm0017"></a>
## SM0017 — `Condition`: the map cannot be projected

**Warning.** A member of this map is assigned behind a `Condition` (from `ForMember` or `ForAllMembers`), so `ProjectTo` cannot use the map; `Map` is unaffected. A projection is one member initializer handed to the database, and there is no way to leave a binding out per row. A warning rather than SM0015's note because the failure would otherwise be silent: the projection would bind unconditionally and quietly hand back different rows from `Map`, per row, in a list endpoint, with no exception anywhere.

```csharp
public class Source      { public string Name { get; set; } = ""; }
public class Destination { public string Name { get; set; } = "n"; }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() =>
        CreateMap<Source, Destination>()
            .ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));
    // warning SM0017: the map from 'Source' to 'Destination' assigns 'Name' behind a Condition, so ProjectTo cannot use it; Map is unaffected
}
```

Asking for the projection throws a message naming this map rather than returning data that disagrees with `Map`, and the call site is reported as SM0037.

**Fix.** Often nothing: a `Condition` is the tool for a PATCH-style update map, and update maps are used through `Map`, not projected. `ShiftMapper.Sample` accepts this warning on its `BrandPatch` to `Brand` map, which backs `PATCH /api/brands/{id}`. If you need the projection, drop the `Condition` and map the member unconditionally — an `opt.Ignore()` after a `Condition` on the same member drops it too, since there is nothing left to guard.

<a id="sm0018"></a>
## SM0018 — In-memory hook: the map cannot be projected

**Warning.** The map runs a `BeforeMap` or `AfterMap` (the message names which, or both), so `ProjectTo` cannot use it; `Map` is unaffected. A projection is one expression handed to the database, and there is no statement in it for your code to be. A warning on the same reasoning as SM0017: the projection would otherwise be built, run, and hand back rows the hook never touched — silently, with `Map` and `ProjectTo` disagreeing about the same map.

```csharp
public class Source      { public string Name { get; set; } = ""; }
public class Destination { public string Name { get; set; } = ""; }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() =>
        CreateMap<Source, Destination>()
            .AfterMap((s, d) => d.Name += "!");
    // warning SM0018: the map from 'Source' to 'Destination' runs AfterMap over its destination, so ProjectTo cannot use it; Map is unaffected
}
```

Asking for the projection throws a message naming this map; the call site is reported as SM0037. `ShiftMapper.Sample` shows both hooks and their order on `GET /api/stocks/hooks`, and the refusal with `?project=true`.

**Fix.** The usual fix is not to silence it. A value worked out from the *source* belongs in a `ForMember`, which projects: `.ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name + "!"))` says the same thing and keeps the projection. `AfterMap` earns its place only where the finished *destination* is genuinely needed (a display string built from two mapped members, say), and those maps are the ones you `Map` rather than project — accept the warning for them.

<a id="sm0019"></a>
## SM0019 — `ConvertUsing` replaces the whole map, so the configuration does nothing

**Warning.** The map has a `ConvertUsing`, which *is* the whole map — no member is matched, converted or assigned afterwards — and it also carries configuration that therefore never applies. The message names what is ignored. It matters because it looks configured: a `ForMember` above a `ConvertUsing` reads as though it refines the map and refines nothing, and unlike most mistakes this one leaves no trace at runtime, because the member it names is simply never assigned by anything.

```csharp
public class Source      { public string Name { get; set; } = ""; public int Rank { get; set; } }
public class Destination { public string Name { get; set; } = ""; public int Rank { get; set; } }

public class AppMapper : ShiftMapperBase
{
    public AppMapper() =>
        CreateMap<Source, Destination>()
            .ForMember(d => d.Rank, opt => opt.Ignore())
            .AfterMap((s, d) => d.Name += "!")
            .ConvertUsing(s => new Destination { Name = s.Name });
    // warning SM0019: the map from 'Source' to 'Destination' uses ConvertUsing, which replaces the whole map, so its ForMember / AfterMap does nothing
}
```

**Fix.** Delete the configuration it ignores, or fold what it does into the `ConvertUsing` expression. A clean `ConvertUsing` says nothing at all — and it is the one map-level hook that projects, because an expression is exactly what a projection is (`GET /api/brands/labels?sql=true` in `ShiftMapper.Sample` shows the concatenation done by the database). If you wanted the members matched by name and only construction replaced, `ConstructUsing` is the method you were reaching for; see SM0015 for what that costs the projection.

<a id="sm0020"></a>
## SM0020 — Destination property is filled by flattening

**Info.** Reported at the `CreateMap`, once for every member flattening filled, naming the path it
walked.

Flattening fills a destination member that has no source of its own by walking into the source —
`OrderDto.CustomerName` from `Order.Customer.Name`. It is on by default (`MapOptions.Flattening`),
and it is a guess: nothing in the name `CustomerName` says it means `Customer.Name` rather than a
column nobody has added yet. SM0020 is how you read the guesses back. It is informational so a normal
build does not fill up with them; it shows in the IDE and not in console build output; raise it in `.editorconfig` (`dotnet_diagnostic.SM0020.severity = warning`) to see it in a build.

```csharp
public class Customer { public string Name { get; set; } = ""; }
public class Order    { public Customer Customer { get; set; } = new(); }
public class OrderDto { public string CustomerName { get; set; } = ""; }

CreateMap<Order, OrderDto>();
```

> info SM0020: 'OrderDto.CustomerName' is filled by flattening, from 'Order.Customer.Name'

Both backends carry the walk. In memory a nullable step is guarded —
`source.Customer is null ? default(string)! : source.Customer.Name` — and in a projection it is a
join. The leaf goes through the ordinary conversion table, so a leaf the table refuses is SM0002, not
a silent skip.

Two limits keep the list short and are worth knowing when reading it. Flattening never competes with
a real property: it runs only after every direct spelling has failed, so it can only fill a member
that would otherwise have been SM0001. And it will not walk into a `string`, a collection, or a
nullable value type — `NameLength` never becomes `Name.Length`, and `LinesQuantity` over a
`List<Line>` stays SM0001.

**There is nothing to fix.** Read the path and confirm it is the one you meant. Where the maps
matter, make the audit mandatory:

```ini
dotnet_diagnostic.SM0020.severity = warning
```

Where you would rather have no guessing at all, turn it off — `CreateMap<Order, OrderDto>(o =>
o.Flattening = false)` per map, or for the whole mapper in `ConfigureDefaults` — and the members go
back to being SM0001, which is the stricter answer. Worked example: `CreateMap<InvoiceLine, InvoiceLineFlatDto>()` in the sample's `AppMapper` produces five of these (`ProductName`, `ProductSku`, `ProductPrice`, `ProductBrandName`, `ProductStockCity`), and `GET /api/invoices/lines/flat?sql=true` shows them as one query with three joins (Products, Brands, Stocks) and no `CASE`, because every step is a required navigation.
query with three joins.

<a id="sm0021"></a>
## SM0021 — Destination property could be flattened more than one way

**Warning.** The member is left unmapped, both paths are named, and this replaces the SM0001 that
would otherwise have been reported for it.

```csharp
public class Inner { public string Name { get; set; } = ""; }
public class Outer { public string CustomerName { get; set; } = ""; }

public class Order
{
    public Outer Order2 { get; set; } = new();
    public Inner Order2Customer { get; set; } = new();
}

public class OrderDto { public string Order2CustomerName { get; set; } = ""; }

CreateMap<Order, OrderDto>();
```

> warning SM0021: 'OrderDto.Order2CustomerName' is not mapped because flattening resolves it more
> than one way: Order2.CustomerName, Order2Customer.Name

Two paths is a question, not a tie to break. Picking the first would be exactly the silent guess
this library exists not to make — the same answer SM0007 gives two source names that differ only by
case. The generated code contains no assignment for the member at all, in either backend.

**The fix is to answer the question.** Name the path with a `ForMember`, which always wins over
flattening:

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.Order2CustomerName, opt => opt.MapFrom(s => s.Order2Customer.Name));
```

or rename one of the source properties so the split is unambiguous. If the member is `required`,
expect SM0014 beside this one — an unmapped `required` member cannot be constructed around.

<a id="sm0022"></a>
## SM0022 — IncludeBase names a map that does not exist

**Warning.** Reported at the derived map's `CreateMap`. Nothing is inherited, and nothing else goes
wrong — which is exactly why it is worth saying.

`IncludeBase<TSourceBase, TDestinationBase>()` takes over the `ForMember` configuration of the map
between the base types, so that map has to exist somewhere in this mapper: its own parts, a mapper
it includes, or a referenced assembly's declarations all count. When it does not, the derived map keeps
doing exactly what it did before. A base map that was renamed, or never written, takes its whole
configuration with it in silence, and every member the base was going to speak for falls back to
the conventions.

```csharp
public class EntityBase { public string Audit { get; set; } = ""; }
public class BaseDto    { public string Audit { get; set; } = ""; }
public class Brand : EntityBase { public string Name { get; set; } = ""; }
public class BrandDto : BaseDto { public string Name { get; set; } = ""; }

// No CreateMap<EntityBase, BaseDto>() anywhere.
CreateMap<Brand, BrandDto>().IncludeBase<EntityBase, BaseDto>();
```

> warning SM0022: the map from 'Brand' to 'BrandDto' includes a base map from 'EntityBase' to
> 'BaseDto', which this mapper does not declare, so nothing is inherited

**The fix** is to add the `CreateMap<EntityBase, BaseDto>()`, or to correct the type arguments if
the base map exists under another pair. Inheritance follows through — a base that has a base of its
own is inherited too, and a loop of bases is walked once rather than forever — so one `IncludeBase`
on each level is enough. Worked example: `CatalogMapper` in the sample, where `BundleItem` inherits
`PhysicalItem`'s configuration and, through it, `CatalogItem`'s (`GET /api/catalog/bundles`).

<a id="sm0023"></a>
## SM0023 — Include cannot dispatch to the derived pair

**Warning.** Reported at the base map's `CreateMap`. The branch is simply not emitted, so the
generated file still compiles and the base map goes on mapping a derived value as though it were
the base — everything the derived type knows dropped without a word. This rule makes that loud.

`Include<TDerived, TDerivedDestination>()` needs three things, and the message names which one is
missing because the fix differs:

| Condition | Message tail |
|---|---|
| `TDerived` derives from the map's source | `'Other' does not derive from 'Shape'` |
| `TDerivedDestination` derives from the map's destination | `'OtherDto' does not derive from 'ShapeDto'` |
| the derived pair has a `CreateMap` of its own | `there is no CreateMap<Circle, CircleDto>()` |

The third is the one people hit:

```csharp
public class Shape { public string Name { get; set; } = ""; }
public class ShapeDto { public string Name { get; set; } = ""; }
public class Circle : Shape { public int Radius { get; set; } }
public class CircleDto : ShapeDto { public int Radius { get; set; } }

CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>();
// and no CreateMap<Circle, CircleDto>()
```

> warning SM0023: the map from 'Shape' to 'ShapeDto' cannot dispatch to 'Circle' to 'CircleDto'
> because there is no CreateMap<Circle, CircleDto>()

**The fix** is the one the message names: declare the derived map (usually with its own
`IncludeBase<Shape, ShapeDto>()` so it inherits the base configuration), or correct the type
arguments. With the derived map present, the generated create method tests
`if (source is global::Circle derived0) return MapToCircleDto(derived0);` before falling through to
the base body, and the update overload tests both objects, because there must be somewhere to
write.

<a id="sm0024"></a>
## SM0024 — Map cannot be projected because it dispatches on the runtime type

**Warning.** Reported once per map that carries an `Include`, at its `CreateMap`. `Map` is
unaffected.

A projection has one element type, fixed when the query is written, and no per-row type test a
provider could translate. A warning rather than a note, for the reason SM0017 and SM0018 are: the
alternative failure is silent — the projection would build every row as the base destination and
quietly disagree with `Map`.

```csharp
CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>();
CreateMap<Circle, CircleDto>();
```

> warning SM0024: the map from 'Shape' to 'ShapeDto' dispatches on the source's runtime type
> through Include, so ProjectTo cannot use it; Map is unaffected

The projection member is still emitted — as a throw, not left out, because a map that nests this
one refers to it by name and a missing member would be a CS0103 inside a generated file. The throw
names what to write instead, using the pair the dispatch would test first:

> ShiftMapper: the map from 'Shape' to 'ShapeDto' dispatches on the source's runtime type through
> Include, which runs in C# and has no SQL. Use Map instead, or project the derived type directly:
> OfType<Circle>().ProjectTo<CircleDto>(mapper).

**The fix is not a workaround; it is the query you meant.** `OfType<TDerived>()` then
`ProjectTo<TDerivedDestination>()` is one query that says which shape you wanted. The sample shows
the refusal at `GET /api/catalog/projected` and the alternative at `GET /api/catalog/physical` (and
`?sql=true` for the SQL it produces). Any `ProjectTo` on the base pair is also flagged where it is
written, as SM0037. If the destination is an interface and one concrete type is enough, `As` (next
entry) projects where `Include` cannot, because the concrete type is fixed when the map is declared
rather than per row.

<a id="sm0025"></a>
## SM0025 — As names a type that cannot stand in for the destination

**Warning.** Reported at the `CreateMap` carrying the `As`. Two reasons, and the message says
which.

`As<TConcrete>()` names the concrete type to build for an interface or abstract destination; the
map becomes a redirection to the concrete map (`return MapToBrandDto(source);`). That needs the
concrete type to be assignable to the destination and to have a `CreateMap` of its own, or there is
nothing to redirect to.

```csharp
public interface IBrandDto { string Name { get; } }
public class Brand { public string Name { get; set; } = ""; }
public class BrandDto : IBrandDto { public string Name { get; set; } = ""; }

CreateMap<Brand, IBrandDto>().As<BrandDto>();   // no CreateMap<Brand, BrandDto>()
```

> warning SM0025: the map from 'Brand' to 'IBrandDto' cannot be built as 'BrandDto' because there
> is no CreateMap<Brand, BrandDto>()

The other reason — `As<Unrelated>()` where `Unrelated` does not implement `IBrandDto` — reads
`because it is not assignable to 'IBrandDto'`. That case is not merely wrong but unemittable, since
the generated method returns the destination type, so the `As` is ignored entirely and the
destination goes back to what it was without it: an interface with nothing to construct, which is
SM0004. Expect both.

**The fix** is to declare the concrete map (`CreateMap<Brand, BrandDto>()`) beside the redirection,
or to name a type that implements the destination. Done right, the pair projects — the projection is
the concrete map's, widened with `MapCustomizations.Widen<Brand, BrandDto, IBrandDto>(...)` — which
is the distinction from `Include` worth keeping straight. `GET /api/catalog/labels?sql=true` in the
sample is the working case.

<a id="sm0026"></a>
## SM0026 — Open generic map was not closed

**Warning.** Reported at the mapper's class declaration rather than at the `CreateMap(typeof(...))`
line. The declaration produced no maps.

`CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>))` is closed once for every pair the
mapper already maps — `PagedResult<Brand>` to `PagedResultDto<BrandDto>` for a `CreateMap<Brand,
BrandDto>`, and so on. One type parameter on each side is the only shape with a single obvious
pairing. With two there is no answer to choose, only a combinatorial one nobody asked for, so the
declaration is refused and says so rather than quietly producing nothing.

```csharp
public class Pair<TKey, TValue> { public TKey Key { get; set; } = default!; }
public class PairDto<TKey, TValue> { public TKey Key { get; set; } = default!; }

CreateMap<Brand, BrandDto>();
CreateMap(typeof(Pair<,>), typeof(PairDto<,>));
```

> warning SM0026: 'Pair' to 'PairDto' was not closed because open generic maps need exactly one
> type parameter on each side

**The fix** is to write the closed `CreateMap` calls out — `CreateMap<Pair<long, Brand>,
PairDto<string, BrandDto>>()` — which also works alongside an open declaration: an explicit closed
map always wins over the one the generator would have produced for the same pair.

Two things the rule does not say, worth knowing. A pair that would violate the wrapper's constraints
(`where T : class` against a struct element) is skipped silently, because emitting it would be a CS
error inside a file you cannot edit. And an interface or abstract element type is skipped too — a
wrapper's member is a nested map, and there is no single type to construct — which is common when
the pair came from an `As` map. Neither skip produces a diagnostic, so an open generic that closes
over nothing is quiet. `GET /api/catalog/paged` and `/api/catalog/paged-brands` in the sample are
two closures of the same line, the second over a pair declared in a different file.

<a id="sm0027"></a>
## SM0027 — A map is declared both in this project and by a referenced package

**Warning.** Reported at the project's own `CreateMap`. There is a defined answer, and both halves
of the library give the same one: the project's declaration is the one that runs, in the generated
code and in the runtime store alike.

A warning rather than an error because nothing is undefined: the project is nearer than a package,
and overriding a package's map is a thing to do on purpose. What it cannot be is silent — the
package's declaration reads exactly like the project's, and a `ForMember` on it does nothing here.
When neither declaration is nearer — two classes of the project, two packages, or one class twice —
that is SM0042, an error.

```csharp
// in Contoso.Platform
public class PlatformMapper : ShiftMapperBase
{
    public PlatformMapper() =>
        CreateMap<FileDto, FileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
}

// in the application
public class FileMapper : ShiftMapperBase
{
    public FileMapper() => CreateMap<FileDto, FileSummary>();   // this one runs here; the package's Trim does not
}
```

> warning SM0027: 'FileDto' to 'FileSummary' is declared in 'FileMapper' and again by the referenced
> package's 'PlatformMapper'; the one in 'FileMapper' is the one that runs

The package's own generated mapper is unaffected — its `MapFrom` runs there. **The fix** is to delete
the project's declaration if the override was not meant; there is no merging of two declarations for
one pair.

<a id="sm0028"></a>
## SM0028 — A referenced assembly carries no ShiftMapper declaration metadata

**Error.** Reported once per pack that could not be read. One of the rules that stop the build,
because the alternative is a pack that was asked for and contributes nothing, in silence.

A source generator sees a referenced assembly as metadata — type names, signatures, attributes —
and never a method body. A mapper compiled into a package is therefore, from the outside, a class
with an empty constructor. What makes packages work is the package's own build: the same generator
runs there and writes what every mapper class and pack declares into the assembly as attributes. A
package built without the generator wrote nothing down: its mapper classes are simply invisible —
nothing announces them, so nothing is asked for — and an `AddConversions` for one of its packs
finds nothing to read.

```csharp
public AppMapper() => AddConversions<FrameworkConversions>();   // FrameworkConversions' package was built without the generator
```

> error SM0028: 'FrameworkConversions' is in a referenced assembly that carries no ShiftMapper declaration
> metadata, so nothing it declares could be read. That package has to be built with the ShiftMapper
> generator referenced as an analyzer.

**The fix is in the package, not here.** It has to reference the `ShiftSoftware.ShiftMapper`
package, which brings the generator in as an analyzer and writes the declarations on the package's
own build. There is no consuming-side workaround, which is why the message states the limitation
rather than only that it was hit. A package that was built correctly is recognised by the
`[assembly: ShiftMapperContract(2)]` its build stamps on it, and every mapper and pack in it by a
`ShiftMapperDeclaredMapper` or `ShiftMapperDeclaredPack` marker — even one that declares nothing;
`Contoso.Platform` — the sample's stand-in for a framework package — is the working example in this
repository, consumed through `GET /api/brands/hashed` and `GET /api/framework/files`.

<a id="sm0029"></a>
## SM0029 — retired

`ConfigureDefaults` used to do nothing on a profile, and SM0029 said so. There are no profiles: every
class deriving from `ShiftMapperBase` is a mapper, and its `ConfigureDefaults` governs its own maps
wherever they end up — in its own generated half and in every mapper that includes it. The id is not
reused.

<a id="sm0030"></a>
## SM0030 — Map cannot be projected because a conversion has no query form

**Warning.** Reported at the `CreateMap` of every map that touches the pair, naming the pair.
`Map` is unaffected.

`CreateConversion<TSource, TDestination>(memory, query = null)` with the `query` left out is a
declaration, not an oversight: it says the pair cannot be translated to SQL. Every map that uses
the conversion — through a plain member, a flattened path, a collection's element type, a
dictionary's value type or a constructor argument — loses its projection.

A warning where SM0015 (`ConstructUsing`) is a note, and the difference is who pays. Whoever wrote
the `CreateConversion` made a decision about a type pair, possibly in a framework; whoever writes a
map that happens to touch that pair inherits the consequence without having asked for it, and is the
one who needs to be told.

```csharp
public class Money { public decimal Amount { get; set; } }
public class Brand { public Money Price { get; set; } = new(); }
public class BrandDto { public string Price { get; set; } = ""; }

CreateConversion<Money, string>(m => m.Amount.ToString());   // no query form
CreateMap<Brand, BrandDto>();
```

> warning SM0030: the map from 'Brand' to 'BrandDto' converts 'Money' to 'string' with a conversion
> that has no query form, so ProjectTo cannot use it; Map is unaffected

The projection member is emitted as a throw that says the same thing and adds `Use Map instead, or
give CreateConversion a query expression for that pair.` — so a `ProjectTo` that reaches it fails
with a sentence rather than a provider's translation error. The call site is flagged too, as SM0037.

**The fix** is one of two. Supply the query form, positionally or as `query:` — the presence of the
second argument is what counts — if the pair can be expressed in SQL:

```csharp
CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
```

Or accept that maps touching the pair are in-memory only and call `Map` for them. The sample's
`GET /api/products/fingerprints` is the second answer: the fingerprint is a hash computed character
by character, no database could express it, and `?project=true` shows the refusal. A parent map that
nests this one inherits the refusal as SM0036.

<a id="sm0031"></a>
## SM0031 — Two packs declare a conversion for the same type pair

**Error.** Reported at the mapper's class declaration, naming both packs and the pair. One of the
rules that stop the build.

Everywhere else, nearest wins: a `ForMember`, then the declaring mapper's own `CreateConversion`,
then the packs it added, then the including mapper's own and its packs, then the packs the
registration gave every mapper, then the built-in table. Two packs at the SAME distance are
different: whichever won would depend on the order they were added, and half the maps in the
application would convert the other way with nobody reading either pack able to see why. There is
no answer to pick, so the build stops and asks for one.

```csharp
// PackageA's pack:  CreateConversion<long, string>(id => "A" + id, id => "A" + id);
// PackageB's pack:  CreateConversion<long, string>(id => "B" + id, id => "B" + id);

public AppMapper()
{
    AddConversions<FirstConversions>();    // from PackageA
    AddConversions<SecondConversions>();   // from PackageB
    CreateMap<Source, Destination>();
}
```

> error SM0031: 'FirstConversions' and 'SecondConversions' both declare a conversion from 'long' to
> 'string'. Near beats far everywhere else, but these are the same distance away, so which one
> applied would depend on the order they were added. Declare the pair on the mapper to settle it.

Two things keep it from firing spuriously. Packs are opt-in, so a package that is referenced but
never added declares nothing and cannot conflict. And one pack listing a pair twice stays silent —
the later registration wins, as it does in the runtime dictionary.

**The fix that clears the error** is to stop adding one of the two packs, or to have one drop the
pair. Or declare the pair nearer — on the mapper with `CreateConversion`, or in a pack the mapper
adds itself when the clash is between registration-wide packs: a nearer declaration wins the lookup,
and it clears the error, because the decision the rule was asking for has been made.

<a id="sm0032"></a>
## SM0032 — A declared conversion could not be read

**Warning.** Reported at the mapper's class declaration, naming the assembly. The conversion is
dropped, so that pair will not convert, and the downstream symptom is an SM0002 on every member that
needed it.

A warning rather than an error because the referenced assembly compiled: the mistake belongs to
the package author, or to a version skew between the package's generator and this one, and failing
this build over it would leave the developer with nothing to do but wait. It is loud enough to
report upstream. Until this rule fired, the conversion was lost in total silence and nothing
connected the SM0002 to the package that was supposed to supply it.

The shape that triggers it is a `[assembly: ShiftMapperDeclaredConversion(...)]` whose constructor
arguments are not three types — `(Type declaredBy, Type source, Type destination)` — which is what a
package built by a different version of the generator, or hand-written metadata, leaves behind:

```csharp
// in the package — written by hand, or by a generator this one does not understand
[assembly: ShiftMapperDeclaredConversion(typeof(BrokenConversions), null, null)]
```

> warning SM0032: 'ShiftMapperPackage' declares a conversion whose metadata could not be read, so
> that pair will not convert. The package and this project were probably built with different
> versions of ShiftMapper.

**The fix** is to align versions: rebuild the package against the same ShiftMapper this project
uses, or update this project to the package's. If the package was built against a different
contract version, SM0033 is reported instead and the whole assembly's declarations are ignored
rather than one conversion. The metadata is written by the generator and never by hand, so in practice the only
way to see this is a version mismatch.

<a id="sm0033"></a>
## SM0033 — A referenced assembly declares a different ShiftMapper contract

**Warning.** Reported at the mapper's class declaration. Every mapper and pack in that assembly is
ignored, whole.

A package's build stamps `[assembly: ShiftMapperContract(version)]` beside the declarations it
writes. The generator reading them understands one contract version — `2`, as of this commit
(`ShiftMapperGenerator.DeclarationContract`) — and refuses an assembly stamped with any other.
Refusing whole rather than half-reading is the point: a generator that guessed at a shape it does
not know would emit code that fails to compile in a file the developer cannot edit, which is the
worst outcome available. An OLDER contract is refused too: version 1 described profiles, a type that
no longer exists, and its assemblies cannot be consumed without a rebuild anyway.

> warning SM0033: 'PackageA' carries ShiftMapper declaration metadata version 1, and this
> ShiftMapper reads version 2. Its mappers and packs were ignored. Build the package and this project
> against the same ShiftMapper.

**The fix** is the one in the message: build the package and this project against the same
ShiftMapper. The check is `DeclaredMappers.CheckContract`.

<a id="sm0034"></a>
## SM0034 — A member convention could not fill the member it claimed

**Warning.** Reported at the mapper's class declaration, naming the expanded path and the
destination member. The member is left unmapped.

A member convention (`CreateMemberConvention<TMember>()`) is a rule for filling any destination
member of one type from source members the destination member's own name selects — `{Member}ID`
and `{Member}.{NameOf}` for a member called `Brand`. When the rule claims a member and a required
`Fill` cannot be satisfied, the member is left unmapped rather than filled some other way. Falling
back to name matching would quietly map it to the very thing the convention was written to
override, which is the failure a convention exists to prevent.

Four ways to get here, and the message says which:

- the path does not resolve on the source — `fills 'Value' from 'BrandCode' (its Fill says
  '{Member}Code'), which does not resolve on 'Product'`. The expanded path is named because that is
  the one somebody can look for; `{NameOf}` stays unexpanded when the walk failed before reaching a
  type to read it from;
- the target is not a settable public property of `TMember` — `fills 'Code', which 'SelectDTO' does
  not declare as a settable public property`;
- the value cannot be converted — `cannot convert 'Guid' to 'long' for 'Value'` — because each
  filled value goes through the ordinary conversion table, global conversions included;
- `TMember` has no public parameterless constructor, so `new TMember { ... }` cannot be emitted.

```csharp
public class SelectDTO { public string Value { get; set; } = ""; }
public class Product { public long Id { get; set; } }              // no BrandCode
public class ProductListDto { public SelectDTO Brand { get; set; } = new(); }

CreateMemberConvention<SelectDTO>().Fill(d => d.Value, "{Member}Code");
CreateMap<Product, ProductListDto>();
```

> warning SM0034: the member convention for 'SelectDTO' fills 'Value' from 'BrandCode' (its Fill
> says '{Member}Code'), which does not resolve on 'Product'. 'ProductListDto.Brand' was left
> unmapped.

**Three fixes, and which one is right depends on whether the source should have the member.** If
it should, correct the `Fill` path or the source. If the source legitimately lacks it — a foreign key
with no navigation beside it, a request body, an entity that nominates no display member — the
entry was never required, and `FillIfPossible` says so: it drops out quietly and the rest of the
member is still built, which is how one rule in `Contoso.Platform/PlatformConversions.cs` serves
both `Product.Brand` (id and name) and a request that carries only the id. Writing `FillIfPossible`
is the acknowledgement, as `Ignore` is, so it stays silent by design. And for one map that is the
exception, a `ForMember` on the member always wins over a convention. If every entry is
`FillIfPossible` and all of them drop out, there is nothing to build and the member is reported as
an ordinary SM0001 rather than as this. Worked example: `GET /api/products/list?sql=true`.

<a id="sm0035"></a>
## SM0035 — This declaration cannot be honoured where it is written

**Error.** Reported at the offending call — the squiggle is under `CreateMap<Destination,
Source>()`, not under the class — and one of the rules that stop the build.

The generator reads declarations from syntax and bakes them once. A declaration inside an `if`, a
loop, a `switch`, a lambda or a local function is therefore applied unconditionally, discarding the
very thing the developer wrote. Before this rule existed that produced output byte-for-byte
identical to writing the call plainly, with no diagnostic. When the baked branch then did not run,
the two backends disagreed: an in-memory `Map` threw from the customization store while `ProjectTo`
quietly dropped the member. A mapper that does not do what its source says, and says nothing, is
the one failure this library refuses to have — configuration the generator cannot bake is an error,
never a silent default.

```csharp
public AppMapper(bool flag)
{
    CreateMap<Source, Destination>();
    if (flag) { CreateMap<Destination, Source>(); }   // SM0035, pointing at this call
}
```

> error SM0035: 'CreateMap' is written inside an 'if', which the generator cannot honour:
> declarations are read at compile time, so the call is baked exactly once and unconditionally no
> matter what the surrounding code does. Move it to an unconditional statement in the constructor,
> or in a method the constructor calls.

It covers every declaration root — `CreateMap`, `AddConversions`, `CreateConversion`,
`CreateMemberConvention` — and names which, and the `AddShiftMapper` lambda, which is read the
same way: a method group or a delegate variable instead of an inline lambda, or an
`AddConversions`/`ShareConversions` behind an `if` inside it, is reported at the call. The rejected positions, each with its own wording, are:
`if` and `else`; `for`, `foreach`, `while` and `do` ("baked once however many times the loop runs");
a conditional `?:` expression; `switch` statements and expressions; `try`, `catch` and `finally`; a
local function; a lambda; behind `&&` or `||`; a property accessor; and a field or property
initializer.

**What is still allowed is the load-bearing half of the rule.** It keys on statement position within
whatever member holds the call — never on which member that is, and never on reachability:

```csharp
public AppMapper() => CreateMap<Source, Destination>();   // expression-bodied constructor: fine

public AppMapper()                                        // block-bodied: fine
{
    AddMaps();
}

private void AddMaps()                                    // a helper the constructor calls: fine
{
    var map = CreateMap<Source, Destination>();           // a chain held in a local: fine
    map.ForMember(d => d.Name, o => o.Ignore());
}
```

Three deliberate consequences. A declaration's own lambdas are not "inside a lambda" — every real
chain nests them, `ForMember(d => d.X, o => o.MapFrom(...))`, and the check runs on the chain's
root, whose lambdas are descendants rather than ancestors. A private helper that is never called is
not reported, because proving that needs a call graph whose answer is unbounded (another partial
part, a source-generated part, DI or reflection can all reach it), and a rule whose false-positive
rate cannot be bounded by reading one file is worse than no rule. And a method of your own that
happens to be named `CreateMap` is not accused of anything — the symbol is bound before a word is
said.

**The fix** is to move the call to an unconditional statement, and to write the condition somewhere
the generator can see it: two mappers, or a `ForMember` with a `Condition`, depending on what the
`if` was for. There is no way to make the branch mean what it says at compile time, which is why
this is an error and not a warning.

<a id="sm0036"></a>
## SM0036 — Map cannot be projected because a map it nests cannot

**Warning.** Reported at the parent's `CreateMap`, naming the child. `Map` is unaffected.

A projection is one expression, assembled from the projections of the maps it nests. If one of
those cannot be an expression — a hook (SM0018), a `Condition` (SM0017), a `ConstructUsing`
(SM0015), an `Include` (SM0024), a conversion with no query form (SM0030) — neither can the one
above it. Until this rule existed each map answered from its own facts alone: a parent nesting a
broken child reported itself projectable, emitted a projection, and spliced in the child's, which is
emitted as a throw. The build warned about the child; the query failed at run time naming a pair the
developer had never asked about.

```csharp
CreateMap<Inner, InnerDto>().AfterMap((s, d) => d.Name = d.Name.Trim());   // SM0018, its own cause
CreateMap<Outer, OuterDto>();                                              // SM0036, inherited
```

> warning SM0036: the map from 'Outer' to 'OuterDto' nests the map from 'Inner' to 'InnerDto',
> which cannot be projected, so ProjectTo cannot use this one either; Map is unaffected

The message names the child because that is the map somebody has to go and fix; naming the parent
would describe the symptom. The child keeps its own message about its own cause, so the two are
reported beside each other rather than one instead of the other. It reaches all the way up — three
levels produce two SM0036s, `Middle` naming `Bottom` and `Top` naming `Middle` — because the pass
runs to a fixpoint rather than once in declaration order. An `As` redirection inherits its concrete
map's verdict the same way. The parent's own projection member becomes a throw that explains itself
(`... this one inherits its verdict`) rather than one that splices in a throw about a different pair.

**Closures of an open generic are exempt from the message, and the reason is measured.** One
`CreateMap(typeof(Page<>), typeof(PageDto<>))` closes over every pair the mapper has, so reporting
here would be N messages on one line about maps nobody wrote, every one derivable from the child's
own message, which already fired. On this rule's first run over the sample and the runtime suite it
produced nine such warnings, all from two `CreateMap(typeof(...))` lines. The closure is still not
projectable — its projection throws, and a `ProjectTo` on it is flagged at the call site as
SM0037 — only the duplicate message is withheld, on the same reasoning that makes the generator
skip closing over interface and abstract elements rather than emit an SM0002 for each.

**The fix** is the child's: address the cause its own diagnostic names, and the parent's projection
comes back with nothing changed on the parent. Or accept the pair as in-memory and call `Map`.

<a id="sm0037"></a>
## SM0037 — ProjectTo cannot be used for this pair

**Warning.** Reported at the call — the squiggle is under `ProjectTo<Destination>`, not under the
query leading up to it — for both spellings, `source.ProjectTo<Destination>(mapper)` and
`mapper.ProjectTo<Destination>(source)`.

Every other projection rule describes a mapper where it is declared. Whoever writes the query is
usually a different person looking at a different file, and this is the one rule that reaches them.
A warning rather than an error because the call is not wrong in itself: it is a query that will
throw when it runs, and the throw says the same thing this does.

```csharp
public class AppMapper : ShiftMapperBase
{
    public AppMapper() =>
        CreateMap<Source, Destination>().AfterMap((s, d) => d.Name = d.Name.Trim());
}

IQueryable<Destination> Run(IQueryable<Source> source, AppMapper mapper) =>
    source.ProjectTo<Destination>(mapper);   // SM0037, here
```

> warning SM0037: 'Source' to 'Destination' cannot be projected: the map runs AfterMap over its
> destination. Use Map instead.

**How the answer reaches the call site.** A call site knows the pair and the mapper and nothing
about how the mapper was configured. So the shape travels: the generator records each map that
cannot project as `[ShiftMapperNotProjectable(typeof(Source), typeof(Destination), "runs AfterMap
over its destination")]` on the generated part of the mapper, and the analyzer reads it off the
receiver's or argument's type. The reason is carried rather than recomputed so that the call site
and the declaration say the same thing, and it works for a mapper that arrived as a package
reference, which has no syntax to re-read. A mapper whose maps all project carries no attribute at
all.

**It reasons from positive evidence only, and the silence half is what keeps it sound.** A generic
repository projecting `IQueryable<TEntity>` to `TDto` names no pair, so nothing is said:

```csharp
public class Repository<TEntity, TDto>
{
    public IQueryable<TDto> List(IQueryable<TEntity> source, IShiftMapper mapper) =>
        mapper.ProjectTo<TEntity, TDto>(source);   // type parameters: silent, by design
}
```

The obvious rule — warn when a map "is only ever `ProjectTo`'d" — would
have had to prove a negative over an open world and accuse that repository of a mistake it has not
made. Inverted to "this concrete call, on this concrete pair, on this mapper, is known to throw", it
is decidable. Two more silences follow from the same principle: `mapper.Map<Destination>(source)` on
the same pair says nothing, because the map is not broken, only its projection; and a call through
`IShiftMapper` with concrete type arguments says nothing, because the interface carries no metadata
— only the mapper class does.

**The fix** is `Map`, or fixing the map's own diagnostic so it projects again. Where the refusal is
intended, say so at the call: the sample does exactly that in five endpoints that exist to
demonstrate a refusal (`GET /api/stocks/hooks?project=true`, `GET /api/products/fingerprints?
project=true` and others), each wrapped in `#pragma warning disable SM0037` with a comment saying
why.

<a id="sm0038"></a>
## SM0038 — This member convention fills nothing

**Warning.** Reported at the mapper's class declaration, naming the member type. The convention
does not apply, and members of that type fall through to ordinary name matching.

A member convention is a rule about how to fill a member; one with no `Fill` or `FillIfPossible`
has nothing to say. The rule exists to stop a lie. Without it, the member fell through to name
matching and the build reported SM0001 — "'Source' has no readable property named 'Brand'" — about
the very member somebody had just written a convention for. Worse, SM0001's code fix (the lightbulb
that writes `opt.Ignore()`) then offered to ignore it, one click from cementing the wrong answer in
source. The same misdiagnosis shaped SM0011's fix: a nested member whose convention was silently
dropped looks exactly like a missing `CreateMap`, and the "add the missing CreateMap" lightbulb had
to wait until this rule could tell the two apart. Naming the real cause where it happens is what
makes both fixes safe to ship.

```csharp
public class Wrapper { public string Value { get; set; } = ""; }
public class Source { public long BrandId { get; set; } }
public class Destination { public Wrapper Brand { get; set; } = new(); }

CreateMemberConvention<Wrapper>();   // declared, and nothing readable hangs off it
CreateMap<Source, Destination>();
```

> warning SM0038: the member convention for 'Wrapper' fills nothing, so it will not apply and
> members of that type fall through to ordinary name matching. Chain a Fill or FillIfPossible onto
> it, or remove it.

`NameFrom`, `WhenDestinationIs` and `Direction` on their own do not count as filling anything; they
qualify a rule that still needs at least one `Fill` entry to exist.

**The fix** is the one in the message: chain the `Fill` the convention was meant to carry —
`.Fill(d => d.Value, "{Member}Id")` for the example above — or delete the declaration. Do not reach
for SM0001's `Ignore` on the member it was written for; that is the wrong answer this rule exists to
prevent.

<a id="sm0042"></a>
## SM0042 — A map is declared twice with nothing to choose between the two

**Error.** Reported at the second `CreateMap`, naming the pair and the two places. One of the rules
that stop the build. The first declaration is kept so the generated file still compiles.

A pair is one map. When the project declares it and a referenced package does too, the project's is
nearer and wins (SM0027, a warning). When neither declaration is nearer than the other — two mapper
classes of the project each wrote it, two packages each wrote it, or one class wrote it twice — the
only way to pick one is the order the files happened to be read in, and a map that silently depends
on which class came first is exactly what this library refuses.

```csharp
public class CatalogMapper      : ShiftMapperBase { public CatalogMapper()      => CreateMap<CatalogItem, CatalogItemDto>(); }
public class InvoiceLabelMapper : ShiftMapperBase { public InvoiceLabelMapper() => CreateMap<CatalogItem, CatalogItemDto>(); }   // SM0042
```

> error SM0042: 'CatalogItem' to 'CatalogItemDto' is declared in both 'CatalogMapper' and
> 'InvoiceLabelMapper', and nothing says which one should run. Declare the pair in one of them

The same rule covers one class writing a pair twice — in one constructor, across the parts of a
partial class, or as a `ReverseMap` plus an explicit `CreateMap` of the reversed pair — worded
"declared twice in 'CatalogMapper'".

What is NOT this: the same package declaration reached through two references collapses silently.
An explicit `CreateMap` for a pair an open generic would also close is the explicit one, silently —
that is what closures are for.

**The fix** is to give the pair one home.

<a id="sm0043"></a>
## SM0043 — A referenced package shared a pack or a mapper class with this project

**Info.** Reported at each `AddShiftMapper` call, once per shared pack — and, under
`MapperDiscovery.LocalAndRegistered`, once per shared mapper class — naming the pack or class and
the assembly that shared it. Nothing is wrong; this is the build saying which
rules arrived without a line of yours asking for them.

A package registers itself with the same `AddShiftMapper` an application uses, and where it writes
`o.ShareConversions<T>()` instead of `o.AddConversions<T>()` its build records the pack as
`[assembly: ShiftMapperDeclaredSharedPack]`. Your generator reads that from every reference and
treats it as an `AddConversions<T>()` for every map in your project, as long as you call
`AddShiftMapper` at all — the furthest level before the built-in table, so a rule you wrote
yourself, in a mapper class or in a pack of your own, wins over it. That is the one declaration that reaches you without you naming the type,
which is why it is announced rather than silent.

```csharp
// in Contoso.Platform — the package's own registration
services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());

// in the application
builder.Services.AddContosoPlatform();
builder.Services.AddShiftMapper();   // SM0043
```

> info SM0043: every map in this project also gets 'PlatformConversions', which
> 'Contoso.Platform' shares with every project that references it; it is applied after everything
> written here, so a rule of your own for the same pair wins

**Nothing to do.** To override one of its rules, declare the pair yourself — in the mapper, or in a
pack given to the call. Turn the message off in `.editorconfig` if you would rather not see it;
the pack still applies, because the metadata that carries it is what your generated code was built
from. A shared pack is a REGISTRATION's pack: a project that makes no `AddShiftMapper` call at all
does not get it. A shared mapper class is the same thing for a class rather than a pack — taken
into your generated mapper under `MapperDiscovery.LocalAndRegistered` without an `AddMapper` line
of yours (see [Choosing which classes are taken](../README.md#choosing-which-classes-are-taken)).

<a id="sm0044"></a>
## SM0044 — A shared pack or mapper class must be public

**Error.** Reported at the `ShareConversions` or `ShareMapper` call, in the package's own build.
One of the rules that stop the build.

The referencing project's generated code names the shared type — in an assembly attribute, in every
conversion call a pack answers, in the constructor call that builds a mapper class — so a type that
project cannot see is a compile error in a file nobody can edit. It is reported here instead, where
it can be fixed.

```csharp
internal class PlatformConversions : ShiftMapperConversions { /* ... */ }

services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());   // SM0044
```

> error SM0044: 'PlatformConversions' is shared with every project that references this one, but
> it is not public, so their generated code could not name it. Make the pack public, or add it with
> AddConversions for this project's mappers alone

**The fix** is in the message: make the pack public, or keep it to this project's mappers with
`AddConversions`.

<a id="sm0046"></a>
## SM0046 — This registration line has no effect

**Warning.** Reported at the line. Two shapes:

- `o.AddMapper<T>()` under `MapperDiscovery.All`, the default. Every mapper class the project can
  see is in its generated mapper already, so the call changes nothing — and a developer who wrote
  it almost certainly meant to choose a mode.
- `o.Discovery` set to different values in two `AddShiftMapper` calls. The generated mapper is
  built once for the project, so the setting has to be one; the later setting is used.

```csharp
services.AddShiftMapper(o => o.AddMapper<PlatformMapper>());   // SM0046: discovery is All
```

> warning SM0046: 'AddMapper<PlatformMapper>()' has no effect: discovery is MapperDiscovery.All, so
> every mapper class this project can see is in its generated mapper already. Set
> o.Discovery = MapperDiscovery.LocalAndRegistered or MapperDiscovery.Registered to make AddMapper
> decide, or delete the call

**The fix** is the one the message gives: set the mode that makes the line mean something, or
delete it. See [Choosing which classes are taken](../README.md#choosing-which-classes-are-taken).
