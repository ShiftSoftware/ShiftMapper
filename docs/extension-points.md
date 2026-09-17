# Extension points: rules that ship in a package

You maintain a library — a framework such as ShiftFramework, for instance, or any package that
ships DTOs and the rules for mapping them — and you want the maps, type-pair conversions and
member conventions you write to apply in every application that references you. The application
should write one line per thing it wants, and nothing else.

For the maps, that line is nothing at all: every mapper class a referenced package declares is
generated into the application's own mapper. For the rules it is `AddConversions<YourConversions>()`
— or nothing either, because your own registration can share a pack with every application that
references you. This page is about what has to be true on your side for that to work, and why: how
a declaration compiled into your DLL is visible to a source generator that can only see metadata,
what travels and what does not, what your `.csproj` needs, and which diagnostics are addressed to
you rather than to the application.

The [README](../README.md) is the front door and already covers the application's view of
[several mapper classes](../README.md#several-mapper-classes-one-mapper),
[packs](../README.md#packs-rules-shared-between-mappers),
[member conventions](../README.md#member-conventions),
[registration](../README.md#registration) and
[mappers from a referenced assembly](../README.md#mappers-from-a-referenced-assembly). This page
goes deeper on each from the package author's side, and does not repeat what the README settles.

Every code block below is either taken from `Contoso.Platform` — the sample's stand-in for a
framework package, modelled on ShiftFramework and consumed by `ShiftMapper.Sample` exactly as a
NuGet reference would be — or from the files the generator wrote for those two projects.

---

## The whole thing, end to end

A package writes an ordinary mapper and an ordinary pack, with the ordinary API:

```csharp
// Contoso.Platform/PlatformMapper.cs — compiled into its own assembly
public class PlatformMapper : ShiftMapperBase
{
    public PlatformMapper() =>
        CreateMap<FileDto, FileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
}

// Contoso.Platform/PlatformConversions.cs — the rules, and no maps
public class PlatformConversions : ShiftMapperConversions
{
    public PlatformConversions()
    {
        CreateConversion<long, string>(
            memory: id => "H" + id,
            query:  id => "H" + id);

        CreateConversion<string?, List<FileDto>>(memory: ToFiles!);

        CreateMemberConvention<SelectDto>()
            .NameFrom<KeyAndNameAttribute>(nameof(KeyAndNameAttribute.Text))
            .Fill(d => d.Value, "{Member}ID")
            .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
    }
}
```

The package registers its own generated mapper and SHARES its pack, in the one `AddXxx` a
framework ships anyway:

```csharp
// Contoso.Platform/ContosoPlatformServiceCollectionExtensions.cs
public static IServiceCollection AddContosoPlatform(this IServiceCollection services) =>
    services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());
    // registers this assembly's generated mapper; shares the pack with every referencing project
```

An application calls that, registers itself, and declares maps that name none of the package's
rules — nothing of the package's appears in its registration:

```csharp
// ShiftMapper.Sample/Program.cs and Mapping/AppMapper.cs, abridged
builder.Services.AddContosoPlatform();
builder.Services.AddShiftMapper();

public class AppMapper : ShiftMapperBase
{
    public AppMapper()
    {
        CreateMap<Brand, BrandHashDto>();       // ExternalIds: List<long> -> List<string>, hashed
        CreateMap<Brand, BrandFilesDto>();      // Files: string -> List<FileDto>; loses ProjectTo (SM0030)
        CreateMap<Product, ProductListDto>();   // Brand, Stock: SelectDto, filled by the convention
    }
}
```

`GET /api/brands/hashed?sql=true` shows the package's hash rule in the SQL Server statement;
`GET /api/products/list?sql=true` shows the convention's member-init inside the `SELECT`;
`GET /api/brands/files?project=true` shows the refusal for the pair that declared no query form;
`GET /api/framework/files` shows the package's own map called through the application's `Mapper`,
generated into the application from the package's metadata. Those four endpoints are the worked
example for everything below.

There is no second API for packages. Nothing in the sample is an attribute written by hand. What
makes that possible is the mechanism in the next section, which is the thing a library author
actually has to understand.

---

## How a declaration crosses an assembly boundary

### The problem

A source generator compiling the application is handed the application's source and a set of
references. A reference is **metadata**: type names, member signatures, attributes, and the types
those attributes mention. It is never a method body. So `PlatformMapper`, seen from the
application's compilation, is a class with a parameterless constructor and nothing inside it. The
`CreateMap` and `CreateConversion` calls are not "hard to read" — they are not there.

That rules out the obvious design, and it is worth being explicit about why, because it is the
first thing every library author tries. A mapper read as source works only in the compilation it
is written in. A mapper compiled into a package contributes nothing to a consumer at compile time
by itself, and SM0028 reports the attempt rather than letting the consumer silently map nothing.

### The split: shape travels as attributes, expressions arrive at run time

The way out is to notice that a declaration is two different kinds of thing:

- **The shape.** Which type pairs. Which members are ignored, conditioned, customised. Whether the
  map has a `ConstructUsing`, a hook, a `ConvertUsing`. Which bases it includes, which derived
  pairs, which concrete type an `As` named. Which conversions exist and whether each has a query
  form. Which member type a convention claims and what its paths are. All of it is names, types
  and flags — exactly what an attribute can hold.
- **The expressions.** The `MapFrom` tree, the `ConstructUsing` factory, the hook delegates, the
  `memory` delegate and `query` tree of a conversion. These cannot be attributes, and they do not
  need to be.

The **shape** is written into your assembly by **your own build**. The same ShiftMapper generator
that writes a mapper's `Map` methods also runs over your package, finds every mapper and every
pack, and emits one file of assembly attributes describing what they declared — while your source
is still in front of it. This is the file `Contoso.Platform`'s build produces (`global::` prefixes
trimmed for width; the file itself is fully qualified):

```csharp
// ShiftMapper.Declarations.g.cs — generated into Contoso.Platform.dll, never edited
[assembly: ShiftMapper.ShiftMapperContract(2)]

// ---- Contoso.Platform.PlatformConversions
[assembly: ShiftMapper.ShiftMapperDeclaredPack(typeof(PlatformConversions))]
[assembly: ShiftMapper.ShiftMapperDeclaredConversion(typeof(PlatformConversions), typeof(long), typeof(string), HasQueryForm = true)]
[assembly: ShiftMapper.ShiftMapperDeclaredConversion(typeof(PlatformConversions), typeof(string), typeof(List<FileDto>), HasQueryForm = false)]
[assembly: ShiftMapper.ShiftMapperDeclaredConvention(typeof(PlatformConversions), typeof(SelectDto), Fill = new string[] { "Value={Member}ID", "?Text={Member}.{NameOf}" }, NameOfAttribute = typeof(KeyAndNameAttribute), NameOfProperty = "Text")]

// ---- Contoso.Platform.PlatformMapper
[assembly: ShiftMapper.ShiftMapperDeclaredMapper(typeof(PlatformMapper))]
[assembly: ShiftMapper.ShiftMapperDeclaredMap(typeof(PlatformMapper), typeof(FileDto), typeof(FileSummary))]
[assembly: ShiftMapper.ShiftMapperDeclaredMember(typeof(PlatformMapper), typeof(FileDto), typeof(FileSummary), "Name", PropertyType = "string", CanSetAfterConstruction = true)]
```

Read it against the two classes above. `s => s.Name.Trim()` is not in it; `"Name"` is. `id => "H" + id`
is not in it; `HasQueryForm = true` is. The `FillIfPossible` entry carries a leading `?`, which is
how "optional" survives the trip. That is the entire contract: a consuming generator reads these
attributes and rebuilds the same internal model it would have built from your source, then hands it
to the same code that handles a local map. Property matching, conversions, nesting, projection,
every diagnostic — none of it can tell a package's map from a local one, and none of it tries.

The **expressions** arrive because the application's generated mapper CONSTRUCTS your classes
on first use — every mapper class and pack it folded in is written into its composition metadata,
and the runtime reads that list. Constructing `PlatformConversions` runs its constructor, and its
constructor calls the real `CreateConversion`, which puts the delegate and the tree into a store the
generated mapper merges into its own; constructing `PlatformMapper` does the same for its
`MapFrom`. This is the same path a mapper class or pack in the application's own project takes.
Nothing about it is package-specific.

So the consuming generator emits, for a package's `MapFrom`, exactly what it emits for a local one —
a lookup by member name against the store the declaring constructor filled. From the sample's
generated mapper:

```csharp
// MapToFileSummary — the package's ForMember
Name = (_ShiftMapperValue_Contoso_Platform_FileDto_To_Contoso_Platform_FileSummary_Name
           ??= Customizations.Value<global::Contoso.Platform.FileDto, global::Contoso.Platform.FileSummary, string>("Name"))(source),

// MapToBrandHashDto — the package's long -> string conversion, looked up in the PACK's store
ExternalIds = global::ShiftMapper.ValueConverter.ToListOrEmpty<long, string>(source.ExternalIds, item => Customizations.Conversion<long, string>(typeof(global::Contoso.Platform.PlatformConversions))(item)),
```

**No lambda text is ever copied.** A lambda in your file has your `using` directives, your captured
fields and your nullable context; copying it as text into a file in another project would break on
the first one that mattered. Splitting shape from expression is what lets the whole scheme work
without the generator understanding a single line of your code.

### Why typed attributes with `typeof`, not a serialized blob

The property that must not be got wrong is **type identity**. `typeof(FileDto)` is resolved
by your compiler and is unambiguously that type in that assembly. A string
`"Contoso.Platform.FileDto"` would have to be re-resolved by name in the consumer, and can find
the wrong type, or none, when two assemblies share a namespace or a type moves. A blob would be
smaller and versioned in one place; identity is worth more than both. Typed attributes are also
readable in a decompiler, which is what somebody will have the first time your rule does not apply
and nobody knows why.

Strings are used only where the value really is a string — a member name, a convention path — and
in two places where an attribute array cannot hold a pair: `IncludedBases` spells a base pair as
`"global::A->global::B"`, and a convention's `Fill` spells an entry as `"Target=path"`.

### Keyed by declaring type — and what that means for what reaches whom

Every attribute names the mapper class or pack that declared it. The consuming generator reads
every MAPPER CLASS every referenced assembly declares — cheaply, only from assemblies that
reference ShiftMapper — and generates their maps into the application's own mapper, each map with
its declaring class's rules and defaults. So your maps are callable in the application from the
reference alone. Your RULES are not applied to the application's own maps by the reference alone:
a pack is read only when something asks for it — a mapper class that adds it, a registration that
adds it, or your own registration sharing it — because a dependency must not quietly alter how the
application's own `long`s render.

The sharing case is `ShiftMapperDeclaredSharedPack`, which is keyed by nothing: it is your
registration saying "every project that references me gets this pack", and the consuming generator
does read it from every reference — only when the application registers something for it to apply
to. It is the one rule that acts without the application naming a type, so it is applied at the
furthest level and announced (SM0043). See [Your registration can share the pack](#mappers-and-packs).

### Three-state options

A map's `MapOptions` lambda travels as what it **said**, not what it resolved to: `NotDeclared`,
`True` or `False`. Your mapper's own `ConfigureDefaults` travels separately, on the
`ShiftMapperDeclaredMapper` marker, and is applied underneath your maps wherever they end up — so
if your map's `Flattening` travelled as a resolved `false` it would bake a value nobody wrote. An
attribute argument cannot be a `bool?`, hence the enum.

### The contract version

`[assembly: ShiftMapperContract(2)]` says which version of this format your build wrote. The
format is a protocol between two different builds of ShiftMapper — the one in your package and the
one in the application — and a reader that meets any other version refuses the assembly **whole**
(SM0033) rather than reading the parts it recognises. Half-reading a shape that has changed is how
a generator emits code that does not compile in a file nobody can edit.

### Where a package's diagnostics land

A map recovered from metadata has no source line in the application. Anything the consumer's build
says about it — an unmapped member, a lost projection — is reported on the application's mapper
class declaration, because that is the nearest thing in the application that asked for the map.

### What the format carries, exactly

Eleven attributes, all in the `ShiftMapper` namespace, all emitted and read by the generator and
never written by hand:

| Attribute | One per |
|---|---|
| `ShiftMapperContract` | assembly; the format version |
| `ShiftMapperDeclaredMapper` | mapper — a presence marker, carrying its `ConfigureDefaults`; emitted even for a mapper that declares nothing |
| `ShiftMapperDeclaredPack` | pack — a presence marker |
| `ShiftMapperDeclaredComposition` | `AddConversions<T>()` in a mapper class's constructor; and, on the generated mapper, every mapper class and pack it folded in |
| `ShiftMapperDeclaredMap` | `CreateMap<A, B>()`, with its ignores, conditions, included bases, `As`, hook and factory flags, and options |
| `ShiftMapperDeclaredMember` | member customised with `MapFrom` or `MapFromSource` (the shape; the tree is runtime) |
| `ShiftMapperDeclaredInclude` | `Include<TDerived, TDerivedDestination>()` |
| `ShiftMapperDeclaredConversion` | `CreateConversion<A, B>(...)`, with `HasQueryForm` |
| `ShiftMapperDeclaredConvention` | `CreateMemberConvention<T>()`, with its `Fill` entries, `NameFrom`, `WhenDestinationIs` and `Direction` |
| `ShiftMapperDeclaredOpenMap` | open generic `CreateMap(typeof(W<>), typeof(WDto<>))` |
| `ShiftMapperDeclaredSharedPack` | `o.ShareConversions<T>()` in a registration — the pack every referencing project's registrations get |
| `ShiftMapperDeclaredSharedMapper` | `o.ShareMapper<T>()` in a registration — the mapper class every referencing project takes under `MapperDiscovery.LocalAndRegistered` |

And one more, on every assembly the generator ran over: `ShiftMapperGenerated` names the
assembly's generated mapper, which is what `AddShiftMapper` registers and `Mapper` builds.

Two consequences of that list are worth knowing before you design a package:

- **Every mapper class and every pack is emitted**, and their maps are ALSO generated into your own
  assembly's mapper. There is no separate "profile" kind of class: what you ship is what you use.
- **Composition crosses the boundary.** A mapper class that adds a pack in its constructor writes
  that down, and a consumer follows it — into a third assembly if that is where it leads. Types
  that are not public are left out, because an attribute cannot name them; a map over one of them
  is generated only in your own assembly, and the run-time door still reaches it there.

One known gap, recorded rather than hidden: a `MapFromSource` whose conversion your generator could
not resolve is reported in **your** build as SM0002, and in a consumer that member falls through to
ordinary name matching instead of staying unmapped. Fix it where it is reported.

---

## Mappers and packs

There is one kind of class for maps and one for rules. The [README](../README.md#several-mapper-classes-one-mapper)
covers the application's view; what follows is what matters when the mapper is yours and the
application is somebody else's.

**A mapper class is read on both sides.** Your `PlatformMapper` is generated into your own
assembly's mapper, and — by default — into the mapper of every project that references you,
re-baked there with that project's rules where those are nearer. The application writes nothing:
`mapper.MapToFileSummary(dto)` is a typed method on its `Mapper` from the reference alone. An
application that has chosen `MapperDiscovery.LocalAndRegistered` takes your classes only when it
names them with `o.AddMapper<PlatformMapper>()` — or when you share them: `o.ShareMapper<PlatformMapper>()`
in your own registration, the mapper analogue of sharing a pack, announced in the application's
build (SM0043). Under `MapperDiscovery.Registered` only what the application names is taken, shared
or not. Whatever it takes, your own registration keeps your maps reachable through `IMapper`. Declarations may be split across
private helper methods; the generator reads the whole class body, not only the constructor. What
it will not read is a declaration inside an `if`, a loop, a lambda or any other position it cannot
bake — that is SM0035, an error, and it applies in your build exactly as it does in an application's,
because it is the same generator.

**Your defaults are yours.** `ConfigureDefaults` on your mapper class governs your maps wherever
they end up; it travels on the `ShiftMapperDeclaredMapper` marker. If a map of yours needs a
particular option, set it on that `CreateMap`; it travels as declared and wins over your default
for that map only.

**Near beats far.** A pair the application declares itself AND you declare keeps the application's
version in the application, and the clash is reported there (SM0027, a warning): overriding a
package's map is a thing to do on purpose. Two packages both declaring a pair is an error in the
application (SM0042); declare maps for the types you own.

**A pack holds rules and nothing else.** `ShiftMapperConversions` has `CreateConversion` and
`CreateMemberConvention` and no `CreateMap`, so adding it cannot bring maps along, and it can be
given to one mapper class or to every map of a registration. Put the rules you want applications to
apply to THEIR maps in a pack; a rule written on your mapper class reaches only your class's maps,
which is the point — referencing your package cannot change how the application's own `long`s
render.

**Your registration can share the pack.** A rule every application should map by — hash ids, a
select convention — is a rule one application will forget to add. So make the registration
yourself, in the `AddXxx` extension your package ships anyway, and write `ShareConversions` where
an application would write `AddConversions`:

```csharp
public static IServiceCollection AddContosoPlatform(this IServiceCollection services) =>
    services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());
```

`ShareConversions` is `AddConversions` for your own assembly's maps, and it makes your build write
`[assembly: ShiftMapperDeclaredSharedPack(typeof(PlatformConversions))]`. The generator compiling any
project that references you reads that and treats it as an `o.AddConversions<PlatformConversions>()`
for every map in that project — including your maps generated there — at the furthest level, so a
rule the project wrote itself for the same pair wins; the project's build says so (SM0043, Info);
and the composition is recorded in the project's own metadata, so its runtime applies the pack
without opening your assembly. A project that makes no `AddShiftMapper` call at all does not get it.
The pack must be public (SM0044), because the application's generated code names it. Two packages
sharing a rule for the same pair are the same distance from every map, and that is SM0031 in the
application, as it is for any two packs.

**Your registration registers your generated mapper, and may share your classes.** `o.ShareMapper<T>()`
beside `o.ShareConversions<T>()` says that every referencing project should have that class in its
generated mapper without naming it (under `LocalAndRegistered`; `All` had it anyway). Share the
classes an application is meant to call by type; leave the rest to the run-time door. Every `AddShiftMapper` call, from whichever
assembly, lands in the one registry the collection holds, and `Mapper` is made of every generated
mapper registered — the application's FIRST, since it already carries your maps re-baked with its
own rules, and yours as the fallback: for a host that has no generator of its own, or a library
mapping through `IMapper` in a host that never registered. The application never injects your
generated mapper; it cannot even name it.

**Dependencies are allowed and resolved late.** A mapper class or pack may take constructor
arguments. It is constructed from the generated mapper's `Services` the first time anything is
mapped — registered if the application registered it, constructed with its dependencies injected
otherwise — not at startup, because the service provider is assigned after the generated mapper is
constructed. A `Mapper` built outside a container has no provider, and a dependency then fails the
first map naming the type:

```
ShiftMapper: the mapper 'X' takes constructor arguments, so it has to come from DI, but the mapper
was not resolved from a service provider. Resolve Mapper through AddShiftMapper(), or give the
mapper a parameterless constructor.
```

For a package this is a design choice with a cost on the other side: **a dependency makes the
generated mapper of every project that references you DI-only**, including in the application's
tests, because everything a generated mapper composes is built together and one that cannot be
built fails the first map. Prefer a parameterless constructor. A mapper class built this way is
handed the generated mapper's provider, so its `Services` property works from a `MapFrom` — but
not from its constructor, which runs first.

---

## Global type-pair conversions

`CreateConversion<TSource, TDestination>(memory, query = null)` registers a rule for a **pair**,
consulted by the same resolver that handles `int` to `string`, just before it would give up and
report SM0002. It answers wherever the pair appears: a plain member, a collection element, a
dictionary value, a constructor argument, a member inside a nested map. Written once, in your
pack, it applies to maps in applications that have never heard of it — every map of every mapper
the application gives the pack to.

The README covers the rules that matter to any user — [a registered pair beats the built-in
table, nearest-wins by distance and by assignability](../README.md#packs-rules-shared-between-mappers).
Three things are specific to writing one that ships.

### The memory form and the query form are a decision about two backends

`memory` is a delegate the generated `Map` methods call, and it may do anything C# can do. `query`
is an expression tree that is **inlined** into the projection — not invoked, because a delegate
call is opaque to EF — so it has to be something a database can run. The two forms are yours to
keep in agreement; nothing can check that for you. `Contoso.Platform`'s first draft had
`"H" + id.ToString("D6")` in memory and `"H" + id` in the query, both well-typed, both translating,
and the same brand came back as `H010010` from `Map` and `H10010` from `ProjectTo`. The sample shows
both backends of a map side by side for that reason.

### Omitting the query form is a declaration you make, not a limitation you hit

Parsing a JSON column into objects is `System.Text.Json`'s job and no database can do it. So the
honest registration is:

```csharp
CreateConversion<string?, List<FileDto>>(memory: ToFiles!);
```

That line **says** the pair cannot be projected. Every map in every consuming application that
touches the pair loses its projection, and each of those builds reports which map:

```
warning SM0030: the map from 'Brand' to 'BrandFilesDto' converts 'string' to 'List<FileDto>'
                with a conversion that has no query form, so ProjectTo cannot use it; Map is unaffected
```

Notice who gets the warning. You made a decision about a type pair; the application developer
wrote `CreateMap<Brand, BrandFilesDto>()` and inherited the consequence without asking for it. That
is why SM0030 is a warning rather than the informational note `ConstructUsing` gets, and it is the
entire reason to do this at compile time: a runtime conversion table converts the pair just as well
and cannot tell anyone which list endpoint has quietly stopped being one query. Inventing a query
expression that "translates" but returns something different from the memory form would be worse
than omitting it — the two backends would disagree, which is the one failure this library is
arranged never to have.

`GET /api/brands/files?project=true` asks for the projection anyway, to show what the refusal
looks like at run time.

### What travels, and how the consumer calls it

The attribute carries the pair and `HasQueryForm`. Both expressions arrive from your pack's
constructor at run time. The consuming generator emits
`Customizations.Conversion<A, B>(typeof(YourConversions))(value)` for the memory form — the same
line an in-project `CreateConversion` produces, naming the scope that answered — and a splice
marker for the query form that the projection composer replaces with your tree, inlined around the
member. The format reserves a `MemoryCall` field for a later optimisation that lifts a
non-capturing lambda into a static method the consumer can call by name; today the declaring
generator always leaves it empty, so the run-time lookup is what every package gets. It is not
slower than a conversion declared in the application's own source, because it is the same code
path.

One version-skew case to know about. The consumer's generated code bakes in that the pair converts.
If a later version of your package drops the conversion, or moves it to a pack the application does
not add, and the application ships without rebuilding, the first map throws:

```
ShiftMapper: no conversion is registered from 'Int64' to 'String' by 'PlatformConversions'. It was
declared with CreateConversion when this mapper was compiled, so the declaration has been removed,
or the mapper no longer includes the mapper or adds the pack that declared it.
```

Removing a conversion from a package is a breaking change for that reason.

### A rule for a base type

A conversion registered for a base type answers for everything assignable to it, nearest by
inheritance winning, so one rule covers an entity hierarchy your package defines and applications
extend. Value types match exactly and only exactly: the generated code hands the delegate back as a
`Func` over the member's own types, which works by contravariance for references and not at all for
a boxed value. The destination is always matched exactly — a conversion's identity is what it
produces.

---

## Member conventions

A conversion is handed **one value** and asked what it becomes. Some rules a framework needs are
shaped differently: which source members fill a destination member depends on that member's own
**name**. `ProductListDto.Brand` of type `SelectDto` is filled from `Product.BrandId`
and `Product.Brand.Name` — two source members, chosen by the word `Brand`. No type-pair rule can
express that, which is why `CreateMemberConvention<TMember>()` sits beside `CreateConversion` rather
than replacing it. The [README](../README.md#member-conventions) shows the application's view; this
is the rule from the side that writes it.

### The vocabulary, and why it is small

```csharp
CreateMemberConvention<SelectDto>()
    .NameFrom<KeyAndNameAttribute>(nameof(KeyAndNameAttribute.Text))
    .Fill(d => d.Value, "{Member}ID")
    .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
```

- **`Fill(target, path)`** — how to fill one member of the shaped type. The target is a selector,
  so renaming `Value` is a compile error in your package rather than a build warning in someone
  else's. The path is a string because it names source members that are not symbols anywhere until
  a map exists. A path may walk navigations with dots to any depth; each step is null-guarded in
  memory and left plain in a query, where the provider turns it into a join.
- **`FillIfPossible(target, path)`** — a `Fill` that is dropped when its path does not resolve,
  instead of failing the member. More on this below, because it is the entry that makes one rule
  enough.
- **`{Member}`** — the destination member's own name. `{Member}ID` on a member called `Brand` reads
  `source.BrandID`, or `source.BrandId`: paths resolve exact-first, then by the mapper's own case
  rule, because a convention that only worked when the application already agreed on casing would
  not be a convention.
- **`{NameOf}`** — the member that the type the path has reached nominates in its own attribute.
  This is the indirection the whole rule turns on. Your package marks nothing in the application;
  the application marks its entities `[KeyAndName(nameof(Id), nameof(Name))]`, and an
  entity that calls its display member `Title` is served by the same rule as one that calls it
  `Name`.
- **`NameFrom<TAttribute>(property)`** — which attribute `{NameOf}` reads, and which of its values
  holds the member name (matched against named arguments first, then constructor parameter names).
  Only needed by a path that uses `{NameOf}`; an id-only rule needs no attribute at all.
- **`WhenDestinationIs<TDestination>()`** — narrows the rule to maps whose destination is
  assignable to the type. Without it the rule applies wherever the member type appears, which is
  usually right. With it your rule cannot reach into an application's unrelated types that happen to
  reuse your DTO. Several calls narrow further; several conventions coexist, each claiming its own
  member type.
- **`Direction(MappingDirection)`** — `Read`, `Write` or `Both` (the default).

Two placeholders and no more, deliberately. The generator cannot execute your code, so a general
callback was never on the table; the vocabulary has to be readable at compile time. If a rule
cannot be said in it, write a `ForMember` in your mapper for the pair in question — an explicit
`ForMember` always wins over a convention.

### It resolves to text, which is why it reaches the projection

The rule becomes an ordinary inline member-init in both halves of the generated code. From the
sample, for `CreateMap<Product, ProductListDto>()` with nothing else configured:

```csharp
// in-memory MapToProductListDto
Brand = new global::Contoso.Platform.SelectDto { Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId), Text = (source.Brand is null ? default(string)! : source.Brand.Name) },
Stock = new global::Contoso.Platform.SelectDto { Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.StockId) },

// the projection EF is handed
Brand = new global::Contoso.Platform.SelectDto { Value = source.BrandId.ToString(), Text = source.Brand!.Name },
Stock = new global::Contoso.Platform.SelectDto { Value = source.StockId.ToString() },
```

The same rule written as an `AfterMap` works in memory and cannot appear in a list query at all,
which is why frameworks that reach for one end up maintaining a second, hand-inlined path for lists.
`GET /api/products/list?sql=true` shows the member-init inside the `SELECT`, with the join to
`Brands` that the `Text` path implies and no join to `Stocks`, because nothing reads through it.

### One rule serves both shapes

Look at `Stock` above: `Value` only, no `Text`. `Stock` in the sample nominates no display member,
so `{Member}.{NameOf}` has nothing to resolve. With a required `Fill` that would be SM0034 and an
unmapped member, and your package would need a **second** rule for every entity that leaves its
label to whatever renders it — precisely the per-entity listing conventions exist to avoid.
`FillIfPossible` drops the entry, the id is still set, and the same rule covers the full shape and
the id-only shape. The other way it can fail is covered too: a source with a foreign key and no
navigation beside it, which is what a request body looks like.

It skips quietly, and that is why it is a separate method rather than a flag: writing
`FillIfPossible` is the acknowledgement, exactly as `Ignore` is. A required `Fill` that cannot
resolve is still reported (SM0034), and the member is left unmapped rather than quietly matched by
name to the very thing the convention existed to override. If every entry drops out the member is
reported unmapped like any other, and a convention with no readable `Fill` at all is SM0038 in the
build that declares it.

### The write direction is derived

`CreateMap<ProductRequest, Product>()`, where the request carries `SelectDto` members,
sets `Product.BrandId` from `request.Brand.Value` — the reverse of the entry you wrote for the
response. A `Fill` whose path is a plain member reverses on its own; an entry that walks a
navigation does not, and should not, since a display name is read from the related row and never
written back to it. The navigation beside the key is claimed and left alone: `Product.Brand`
name-matches the request's `Brand`, and without the convention claiming it every write map in every
consumer would be an SM0011 error demanding a map from your DTO to their entity.
`POST /api/products/preview` returns the ids set and both navigations null.

### It composes with your conversions

Each value a convention fills goes through the ordinary conversion table. If the key is a `long`
and your pack also registers the hash-id conversion, `Value` arrives hashed — neither rule
mentions the other. (In the sample `BrandId` is an `int`, so it is formatted by the built-in table
instead; value types match a conversion exactly.)

### Crossing the boundary

A convention is entirely shape — a member type, some target/path pairs, an attribute and a
direction — so it crosses an assembly with nothing left behind. The attribute spells each entry
`"Value={Member}ID"`, with a leading `?` for `FillIfPossible`; the marker has to survive, because a
package's id-only rule that arrived as a hard requirement would hand SM0034 to every consumer whose
entity nominates no name. What the consumer rebuilds is the same convention the source path builds,
not a weaker kind, and a cross-assembly test pins both facts.

---

## What your project file needs

The generator that writes your metadata is the same generator that writes an application's mapper,
and it has to run **over your package**. If it does not, your assembly carries no declaration
attributes — and no generated `Map` methods — and every application that uses your mapper or pack
gets SM0028.

`dotnet add package ShiftSoftware.ShiftMapper` delivers both halves: `lib/net10.0` holds the
runtime types your mapper and pack derive from, and `analyzers/dotnet/cs` holds the generator, which
NuGet hands to the compiler. A library that references the package the ordinary way is already covered.
Inside this repository, where there is no package, `Contoso.Platform` says the same thing with
two project references:

```xml
<ItemGroup>
  <ProjectReference Include="..\ShiftMapper\ShiftMapper.csproj" />
  <ProjectReference Include="..\ShiftMapper.Generator\ShiftMapper.Generator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Two things to keep true:

- **Do not strip the analyzer.** `<ExcludeAssets>analyzers</ExcludeAssets>` on the ShiftMapper
  reference, or any build arrangement that keeps generators from running over your library, leaves
  your DLL silent. Nothing fails in your build; the failure surfaces as SM0028 in someone else's.
- **Do not hide the ShiftMapper dependency from consumers.** Your declaration attributes are
  ShiftMapper types, and your mapper derives from `ShiftMapperBase`. A consumer has to be able to
  resolve both, so the ShiftMapper reference must flow as an ordinary dependency of your package,
  not as `PrivateAssets="all"`.

**Read what you shipped.** Turn on emitted generated files, and the metadata lands on disk where you
can open it:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>

<ItemGroup>
  <Compile Remove="Generated/**" />
</ItemGroup>
```

`Generated/ShiftMapper.Generator/ShiftMapper.Generator.ShiftMapperGenerator/ShiftMapper.Declarations.g.cs`
is the file quoted earlier. If a rule you wrote is not in it, no consumer will see it. A project with
no mappers and no packs produces no file at all — there is nothing to say and nothing is emitted,
not even an empty header; a mapper that declares nothing still gets its marker.

Your build also reports on your declarations as it reads them: SM0035 for a declaration in a
position the generator cannot bake, SM0038 for a convention with no readable `Fill`, SM0002 for a
`MapFromSource` whose conversion does not resolve. Those are yours to fix before the package ships,
because they will not be reported again on the other side.

---

## The diagnostics addressed to a package author

Forty rules exist; the [README table](../README.md#diagnostics) lists them. Four are about the
boundary itself. They are reported in the **consuming** application's build, so what you will
usually see is a bug report quoting one.

**SM0028 — a referenced assembly carries no ShiftMapper declaration metadata.** Error.

```
ShiftMapper: 'PlatformMapper' is in a referenced assembly that carries no ShiftMapper declaration
metadata, so nothing it declares could be read. That package has to be built with the ShiftMapper
generator referenced as an analyzer.
```

The application added a pack of yours, and your assembly has no `[ShiftMapperContract]` and no
marker naming that type. (A mapper class of yours is simply invisible in that case: nothing announces
it, so nothing is asked for.) Nothing of yours can be baked, so the build
stops rather than mapping nothing in silence. The fix is in your `.csproj`, above.

**SM0031 — two packs declare a conversion for the same type pair.** Error.

```
ShiftMapper: 'FirstConversions' and 'SecondConversions' both declare a conversion from 'long' to
'string'. Near beats far everywhere else, but these are the same distance away, so which one applied
would depend on the order they were added. Declare the pair on the mapper to settle it.
```

The one place nearest-wins cannot decide, so it is an error: whichever won, half the application's
maps would convert the other way and nobody reading either pack could see why. One pack listing a
pair twice stays silent. A `CreateConversion` on the application's mapper, or a pack it adds
itself when the clash is between registration-wide packs, is nearer than either and settles it.
For you, the lesson is to declare conversions for pairs you own; a rule for `long` to `string` will
meet another package's rule for `long` to `string` eventually.

**SM0032 — a declared conversion could not be read.** Warning.

```
ShiftMapper: 'PackageA' declares a conversion whose metadata could not be read, so that pair will
not convert. The package and this project were probably built with different versions of ShiftMapper.
```

A `ShiftMapperDeclaredConversion` attribute is present but its constructor arguments are not the
three the reader expects. Since nobody writes these by hand, that means the generator that wrote
them and the one reading them disagree about the shape, in a way the contract version did not
catch. It exists so the pair fails loudly with the package named, rather than as a downstream SM0002
with nothing to connect it to the package that was supposed to supply it.

**SM0033 — a referenced assembly declares a different ShiftMapper contract.** Warning.

```
ShiftMapper: 'PackageA' carries ShiftMapper declaration metadata version 1, and this ShiftMapper
reads version 2. Its mappers and packs were ignored. Build the package and this project against the
same ShiftMapper.
```

Your package was built against a different ShiftMapper than the application uses. The assembly is
refused whole, so the application also gets SM0028 for each of your types it asked for — two
messages for one cause, and the SM0033 is the one to act on. State the ShiftMapper version your
package needs, and do not bump it lightly. The current contract version is 2.

### The ones you cause but do not see

Every projection-loss rule the README describes for a map applies to a map that arrived from you,
and is reported in the consumer's build against their mapper class. A `BeforeMap` or `AfterMap` in
your mapper costs every consumer that map's projection (SM0018); a `ConstructUsing` does the same
(SM0015); a `Condition` likewise (SM0017); a conversion with no query form costs every map that
touches the pair (SM0030). The shape of each travels precisely so that the consumer's build can say
so. Before you ship a hook, ask whether the value is derivable from the source — a `ForMember`
projects, and a hook does not.

---

## Checklist

1. Put maps in classes deriving from `ShiftMapperBase`, and rules in a class deriving from
   `ShiftMapperConversions`. Every one of them is written to metadata; there is no other kind, and
   none of them needs to be `partial`.
2. Keep a mapper class's constructor parameterless unless a rule genuinely needs a service; a
   dependency makes the generated mapper of every project that references you DI-only.
3. Write declarations in statement position — constructor body, expression-bodied constructor, or
   private helper methods — never inside `if`, loops or lambdas (SM0035).
4. A mapper class may add packs in its constructor; that composition travels, so a consumer's
   generated mapper follows it.
5. Put rules meant for the APPLICATION's maps in a pack, not on your mapper class. A rule on your
   class reaches only that class's maps, by design. Declare maps only for types you own; a pair two
   packages both declare is an error in every application that references both (SM0042).
6. For each `CreateConversion`, decide the query form deliberately. Supply one that produces the
   same value as the memory form, or omit it and accept that every map touching the pair reports
   SM0030 in every consumer. Never invent a query form that returns something different.
7. Register conversions for pairs you own. A pair another package's pack might also claim is an
   SM0031 error for whoever adds both at the same level.
8. For a convention, use `FillIfPossible` for anything that reads through a navigation or an
   attribute-nominated member, so one rule serves the id-only shape too. Add
   `WhenDestinationIs<T>()` if your member type could appear in types that are not yours.
9. Reference `ShiftSoftware.ShiftMapper` as an ordinary dependency: analyzers included, not
   `PrivateAssets="all"`.
10. Turn on `EmitCompilerGeneratedFiles` and read `ShiftMapper.Declarations.g.cs` before you
    publish. If a rule is not in that file, no consumer will get it.
11. Treat removing or moving a declaration as a breaking change: a consumer's generated code bakes
    in that the pair converts, and one that ships without rebuilding throws on first map.
12. Fix SM0035, SM0038 and SM0002 in your own build; they are not repeated on the other side.
13. State the ShiftMapper version your package was built against. An application on a different
    one gets SM0033 and SM0028, and nothing from you.
