# Extension points: rules that ship in a package

You maintain a library — a framework such as ShiftFramework, for instance, or any package that
ships DTOs and the rules for mapping them — and you want the maps, type-pair conversions and
member conventions you write to apply in every application that references you. The application
should write one line, and nothing else.

That line is `AddProfile<YourProfile>()`. This page is about what has to be true on your side for
it to work, and why: how a declaration compiled into your DLL is visible to a source generator that
can only see metadata, what travels and what does not, what your `.csproj` needs, and which
diagnostics are addressed to you rather than to the application.

The [README](../README.md) is the front door and already covers the application's view of
[profiles](../README.md#profiles-maps-written-outside-the-mapper),
[global conversions](../README.md#global-type-pair-conversions),
[member conventions](../README.md#member-conventions) and
[rules from a referenced assembly](../README.md#rules-from-a-referenced-assembly). This page goes
deeper on each from the package author's side, and does not repeat what the README settles.

Every code block below is either taken from `Contoso.Platform` — the sample's stand-in for a
framework package, modelled on ShiftFramework and consumed by `ShiftMapper.Sample` exactly as a
NuGet reference would be — or from the files the generator wrote for those two projects.

---

## The whole thing, end to end

A package writes an ordinary profile with the ordinary API:

```csharp
// Contoso.Platform/PlatformProfile.cs — compiled into its own assembly
public class PlatformProfile : ShiftMapperProfile
{
    public PlatformProfile()
    {
        CreateConversion<long, string>(
            memory: id => "H" + id,
            query:  id => "H" + id);

        CreateConversion<string?, List<FileDto>>(memory: PlatformConversions.ToFiles!);

        CreateMemberConvention<SelectDto>()
            .NameFrom<KeyAndNameAttribute>(nameof(KeyAndNameAttribute.Text))
            .Fill(d => d.Value, "{Member}ID")
            .FillIfPossible(d => d.Text, "{Member}.{NameOf}");

        CreateMap<FileDto, FileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
    }
}
```

An application adds it, and declares maps that name none of the package's rules:

```csharp
// ShiftMapper.Sample/Mapping/AppMapper.cs, abridged
public partial class AppMapper : ShiftMapperBase
{
    public AppMapper()
    {
        AddProfile<PlatformProfile>();

        CreateMap<Brand, BrandHashDto>();       // ExternalIds: List<long> -> List<string>, hashed
        CreateMap<Brand, BrandFilesDto>();      // Files: string -> List<FileDto>; loses ProjectTo (SM0030)
        CreateMap<Product, ProductListDto>();   // Brand, Stock: SelectDto, filled by the convention
    }
}
```

`GET /api/brands/hashed?sql=true` shows the package's hash rule in the SQL Server statement;
`GET /api/products/list?sql=true` shows the convention's member-init inside the `SELECT`;
`GET /api/brands/files?project=true` shows the refusal for the pair that declared no query form.
Those three endpoints are the worked example for everything below.

There is no second API for packages. Nothing in the sample is an attribute written by hand, and
nothing in it names `PlatformConversions`. What makes that possible is the mechanism in the next
section, which is the thing a library author actually has to understand.

---

## How a declaration crosses an assembly boundary

### The problem

A source generator compiling the application is handed the application's source and a set of
references. A reference is **metadata**: type names, member signatures, attributes, and the types
those attributes mention. It is never a method body. So `PlatformProfile`, seen from the
application's compilation, is a class with a parameterless constructor and nothing inside it. The
`CreateMap` and `CreateConversion` calls are not "hard to read" — they are not there.

That rules out the obvious design, and it is worth being explicit about why, because it is the
first thing every library author tries. A profile read as source works only in the compilation it
is written in. A profile compiled into a package contributes nothing at compile time, and SM0028
reports the attempt rather than letting the mapper silently map nothing.

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
that writes a mapper's `Map` methods also runs over your package, finds every
`ShiftMapperProfile` subclass, and emits one file of assembly attributes describing what those
profiles declared — while your source is still in front of it. This is the file
`Contoso.Platform`'s build produces (`global::` prefixes trimmed for width; the file itself is
fully qualified):

```csharp
// ShiftMapper.Declarations.g.cs — generated into Contoso.Platform.dll, never edited
[assembly: ShiftMapper.ShiftMapperContract(1)]

// ---- Contoso.Platform.PlatformProfile
[assembly: ShiftMapper.ShiftMapperDeclaredMap(typeof(PlatformProfile), typeof(FileDto), typeof(FileSummary))]
[assembly: ShiftMapper.ShiftMapperDeclaredMember(typeof(PlatformProfile), typeof(FileDto), typeof(FileSummary), "Name", PropertyType = "string", CanSetAfterConstruction = true)]
[assembly: ShiftMapper.ShiftMapperDeclaredConversion(typeof(PlatformProfile), typeof(long), typeof(string), HasQueryForm = true)]
[assembly: ShiftMapper.ShiftMapperDeclaredConversion(typeof(PlatformProfile), typeof(string), typeof(List<FileDto>), HasQueryForm = false)]
[assembly: ShiftMapper.ShiftMapperDeclaredConvention(typeof(PlatformProfile), typeof(SelectDto), Fill = new string[] { "Value={Member}ID", "?Text={Member}.{NameOf}" }, NameOfAttribute = typeof(KeyAndNameAttribute), NameOfProperty = "Text")]
```

Read it against the profile above. `s => s.Name.Trim()` is not in it; `"Name"` is. `id => "H" + id`
is not in it; `HasQueryForm = true` is. The `FillIfPossible` entry carries a leading `?`, which is
how "optional" survives the trip. That is the entire contract: a consuming generator reads these
attributes and rebuilds the same internal model it would have built from your source, then hands it
to the same code that handles a local map. Property matching, conversions, nesting, projection,
every diagnostic — none of it can tell a package's map from a local one, and none of it tries.

The **expressions** arrive because `AddProfile<T>()` is the one declaration call that also does
something at run time: it records `T` so the mapper can construct it on first use. Constructing
`PlatformProfile` runs its constructor, and its constructor calls the real `CreateConversion`
and `ForMember(... MapFrom ...)`, which put the delegate and the trees into the mapper's
customization store. This is the same path a profile in the application's own project takes.
Nothing about it is package-specific.

So the consuming generator emits, for a package's `MapFrom`, exactly what it emits for a local one —
a lookup by member name against the store the profile's constructor filled. From the sample's
generated mapper:

```csharp
// MapToFileSummary — the package's ForMember
Name = (_ShiftMapperValue_Contoso_Platform_FileDto_To_Contoso_Platform_FileSummary_Name
           ??= Customizations.Value<global::Contoso.Platform.FileDto, global::Contoso.Platform.FileSummary, string>("Name"))(source),

// MapToBrandHashDto — the package's long -> string conversion
ExternalIds = global::ShiftMapper.ValueConverter.ToListOrEmpty<long, string>(source.ExternalIds, item => Customizations.Conversion<long, string>()(item)),
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

### Keyed by profile, so it is opt-in

Every attribute names the profile that declared it, and the consuming generator reads only
attributes for profiles the mapper actually added. It also walks only the assemblies those profiles
live in; a package whose profile nobody adds is never scanned at all. Referencing your package
changes nothing in an application until it asks, which is what stops a dependency from quietly
altering how someone else's maps behave.

### Three-state options

A map's `MapOptions` lambda travels as what it **said**, not what it resolved to: `NotDeclared`,
`True` or `False`. A profile's own `ConfigureDefaults` configures nothing (SM0029) — the mapper
that adds the profile supplies the defaults — so if your map's `Flattening` travelled as a resolved
`false` it would override a setting the application made, with a value nobody wrote. An attribute
argument cannot be a `bool?`, hence the enum.

### The contract version

`[assembly: ShiftMapperContract(1)]` says which version of this format your build wrote. The
format is a protocol between two different builds of ShiftMapper — the one in your package and the
one in the application — and a reader that meets a version it does not know refuses the assembly
**whole** (SM0033) rather than reading the parts it recognises. Half-reading a shape that has
changed is how a generator emits code that does not compile in a file nobody can edit.

### Where a package's diagnostics land

A map recovered from metadata has no source line in the application. Anything the consumer's build
says about it — an unmapped member, a lost projection — is reported on the application's mapper
class declaration, because that is the nearest thing in the application that asked for the map.

### What the format carries, exactly

Seven attributes, all in the `ShiftMapper` namespace, all emitted and read by the generator and
never written by hand:

| Attribute | One per |
|---|---|
| `ShiftMapperContract` | assembly; the format version |
| `ShiftMapperDeclaredMap` | `CreateMap<A, B>()`, with its ignores, conditions, included bases, `As`, hook and factory flags, and options |
| `ShiftMapperDeclaredMember` | member customised with `MapFrom` or `MapFromSource` (the shape; the tree is runtime) |
| `ShiftMapperDeclaredInclude` | `Include<TDerived, TDerivedDestination>()` |
| `ShiftMapperDeclaredConversion` | `CreateConversion<A, B>(...)`, with `HasQueryForm` |
| `ShiftMapperDeclaredConvention` | `CreateMemberConvention<T>()`, with its `Fill` entries, `NameFrom`, `WhenDestinationIs` and `Direction` |
| `ShiftMapperDeclaredOpenMap` | open generic `CreateMap(typeof(W<>), typeof(WDto<>))` |

Two consequences of that list are worth knowing before you design a package:

- **Only `ShiftMapperProfile` subclasses are emitted.** A class deriving directly from
  `ShiftMapperBase` is a mapper, and a mapper's declarations become generated code, not metadata.
  Put everything you want to ship in a profile.
- **There is no attribute for a profile adding another profile.** In one compilation a profile may
  call `AddProfile<Other>()` and the union is mapped; across a boundary the consuming generator has
  no way to learn that `Other` was involved. Ship profiles the application adds directly, and let
  the application list them.

One known gap, recorded rather than hidden: a `MapFromSource` whose conversion your generator could
not resolve is reported in **your** build as SM0002, and in a consumer that member falls through to
ordinary name matching instead of staying unmapped. Fix it where it is reported.

---

## Profiles

A profile is a place to write declarations, not a second mapper. Nothing is generated onto it —
no `Map` methods, no projections. Its declarations become the declarations of every mapper that
adds it, called through that mapper as if they had been written in its constructor. The
[README](../README.md#profiles-maps-written-outside-the-mapper) covers the application's view;
what follows is what matters when the profile is yours and the mapper is somebody else's.

**The surface is the mapper's surface.** `ShiftMapperProfile` derives from `ShiftMapperBase`, so
`CreateMap`, the open generic `CreateMap`, `CreateConversion`, `CreateMemberConvention` and every
refinement chained onto them are literally the same methods. Declarations may be split across
private helper methods inside the profile; the generator reads the whole class body, not only the
constructor. What it will not read is a declaration inside an `if`, a loop, a lambda or any other
position it cannot bake — that is SM0035, an error, and it applies in your build exactly as it does
in an application's, because it is the same generator.

**Defaults come from the mapper that adds you.** One mapper has one `ConfigureDefaults`, whichever
file a map was written in. Overriding it on a profile is reported (SM0029) because it would
otherwise be a reasonable guess that does nothing. If a map of yours needs a particular option, set
it on that `CreateMap`; it travels as declared and wins over the application's default for that
map only.

**Near beats far.** A pair the application declares both in your profile and in its own mapper
keeps the application's version, and the clash is reported (SM0027). The same order holds at run
time: the mapper's own registrations are in the store before profiles are merged in, and a merge
never overwrites. So the generator and the runtime cannot disagree about which declaration ran.

**Dependencies are allowed and resolved late.** A profile may take constructor arguments. It is
resolved from the mapper's `Services` the first time anything is mapped, not while the mapper's
constructor runs, because the service provider is not assigned until after that constructor
returns. A parameterless profile needs no registration; one with parameters must be registered
(`services.AddTransient<YourProfile>()`), or the first map throws naming the profile and the fix:

```
ShiftMapper: the profile 'X' takes constructor arguments, so it has to come from DI, but 'AppMapper'
was not resolved from a service provider. Register the profile with services.AddTransient<X>() and
resolve the mapper through AddShiftMapper, or give the profile a parameterless constructor.
```

For a package this is a design choice with a cost on the other side: **a profile with dependencies
makes every mapper that adds it DI-only**, including in the application's tests, because all of a
mapper's profiles are built together and one that cannot be built fails the first map. Prefer a
parameterless profile. Where a rule genuinely needs a service, the profile has to take it through its constructor and be registered in DI (`services.AddTransient<YourProfile>()`); a profile's own `Services` property is never assigned — only the mapper's is — so an expression cannot reach one through it.

---

## Global type-pair conversions

`CreateConversion<TSource, TDestination>(memory, query = null)` registers a rule for a **pair**,
consulted by the same resolver that handles `int` to `string`, just before it would give up and
report SM0002. It answers wherever the pair appears: a plain member, a collection element, a
dictionary value, a constructor argument, a member inside a nested map. Written once, in your
profile, it applies to maps in applications that have never heard of it.

The README covers the rules that matter to any user — [a registered pair beats the built-in
table, assignability with nearest-wins](../README.md#global-type-pair-conversions). Three things
are specific to writing one that ships.

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
CreateConversion<string?, List<FileDto>>(memory: PlatformConversions.ToFiles!);
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

The attribute carries the pair and `HasQueryForm`. Both expressions arrive from your profile's
constructor at run time. The consuming generator emits `Customizations.Conversion<A, B>()(value)`
for the memory form — the same line an in-project `CreateConversion` produces — and a splice
marker for the query form that the projection composer replaces with your tree, inlined around the
member. The format reserves a `MemoryCall` field for a later optimisation that lifts a
non-capturing lambda into a static method the consumer can call by name; today the declaring
generator always leaves it empty, so the run-time lookup is what every package gets. It is not
slower than a conversion declared in the application's own source, because it is the same code
path.

One version-skew case to know about. The consumer's generated code bakes in that the pair converts.
If a later version of your package drops the conversion, or moves it to a profile the application
no longer adds, and the application ships without rebuilding, the first map throws:

```
ShiftMapper: no conversion is registered from 'Int64' to 'String'. It was declared with
CreateConversion when this mapper was compiled, so the declaration has been removed or moved to a
profile this mapper no longer adds.
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
cannot be said in it, write a `ForMember` in your profile for the pair in question — an explicit
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
and your profile also registers the hash-id conversion, `Value` arrives hashed — neither rule
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
attributes, and every application that adds your profile gets SM0028.

`dotnet add package ShiftSoftware.ShiftMapper` delivers both halves: `lib/net10.0` holds the
runtime types your profile derives from, and `analyzers/dotnet/cs` holds the generator, which NuGet
hands to the compiler. A library that references the package the ordinary way is already covered.
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
  ShiftMapper types, and your profile derives from `ShiftMapperProfile`. A consumer has to be able
  to resolve both, so the ShiftMapper reference must flow as an ordinary dependency of your package,
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
no profiles produces no file at all — there is nothing to say and nothing is emitted, not even an
empty header.

Your build also reports on your declarations as it reads them: SM0035 for a declaration in a
position the generator cannot bake, SM0038 for a convention with no readable `Fill`, SM0002 for a
`MapFromSource` whose conversion does not resolve. Those are yours to fix before the package ships,
because they will not be reported again on the other side.

---

## The diagnostics addressed to a package author

Thirty-eight rules exist; the [README table](../README.md#diagnostics) lists them. Four are about
the boundary itself. They are reported in the **consuming** application's build, so what you will
usually see is a bug report quoting one.

**SM0028 — a referenced assembly carries no ShiftMapper declaration metadata.** Warning.

```
ShiftMapper: the profile 'PlatformProfile' is in a referenced assembly that carries no
ShiftMapper declaration metadata, so nothing it declares could be read. That package has to be
built with the ShiftMapper generator referenced as an analyzer.
```

The application called `AddProfile<T>()`, `T` lives in a reference, and that reference has no
`[ShiftMapperContract]` and no declared-profile attributes naming `T`. The mapper compiles and maps
nothing from the profile; at run time the profile's constructor still runs and fills the store, but
no generated code reads from it. The fix is in your `.csproj`, above. It is a warning rather than an
error because the assembly compiled and the mistake belongs to the package author, not to whoever
is building now.

**SM0031 — two assemblies declare a conversion for the same type pair.** Error.

```
ShiftMapper: 'PackageA' and 'PackageB' both declare a conversion from 'long' to 'string'. Near
beats far everywhere else, but these are the same distance away, so which one applied would depend
on reference order. Declare the pair in this project to settle it.
```

The one place near-beats-far cannot decide, so it is the one error in the group: whichever won,
half the application's maps would convert the other way and nobody reading either package could see
why. Two declarations of one pair from the **same** assembly stay silent — that is one package
listing a pair twice, harmless and not the application's problem. A `CreateConversion` in the application's own source is nearer than either package, so it wins the lookup — and it clears the error, because the application has made the decision the rule was asking for. Removing one of the two `AddProfile` calls, or one package dropping the pair, settles it too.
its own `CreateConversion`, which wins over both. For you, the lesson is to declare conversions for
pairs you own; a rule for `long` to `string` will meet another package's rule for `long` to
`string` eventually.

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

**SM0033 — a referenced assembly declares a newer ShiftMapper contract.** Warning.

```
ShiftMapper: 'PackageA' carries ShiftMapper declaration metadata version 2, and this ShiftMapper
understands version 1. Its profiles were ignored. Update the ShiftMapper package in this project.
```

Your package was built against a newer ShiftMapper than the application uses. The assembly is
refused whole, so the application also gets SM0028 for each of your profiles it added — two messages
for one cause, and the SM0033 is the one to act on. The fix is on the application's side; what you
can do is state the ShiftMapper version your package needs, and not bump it lightly. The current
contract version is 1.

### The ones you cause but do not see

Every projection-loss rule the README describes for a map applies to a map that arrived from you,
and is reported in the consumer's build against their mapper class. A `BeforeMap` or `AfterMap` in
your profile costs every consumer that map's projection (SM0018); a `ConstructUsing` does the same
(SM0015); a `Condition` likewise (SM0017); a conversion with no query form costs every map that
touches the pair (SM0030). The shape of each travels precisely so that the consumer's build can say
so. Before you ship a hook, ask whether the value is derivable from the source — a `ForMember`
projects, and a hook does not.

---

## Checklist

1. Put every declaration in a class deriving from `ShiftMapperProfile`, not `ShiftMapperBase`.
   Only profiles are written to metadata.
2. Keep the profile's constructor parameterless unless a rule genuinely needs a service; a
   profile with dependencies makes every consuming mapper DI-only.
3. Write declarations in statement position — constructor body, expression-bodied constructor, or
   private helper methods — never inside `if`, loops or lambdas (SM0035).
4. Do not add other profiles from a package profile and expect the consumer to see them; ship
   profiles the application adds directly.
5. For each `CreateConversion`, decide the query form deliberately. Supply one that produces the
   same value as the memory form, or omit it and accept that every map touching the pair reports
   SM0030 in every consumer. Never invent a query form that returns something different.
6. Register conversions for pairs you own. A pair another package might also claim is an SM0031
   error for whoever references both.
7. For a convention, use `FillIfPossible` for anything that reads through a navigation or an
   attribute-nominated member, so one rule serves the id-only shape too. Add
   `WhenDestinationIs<T>()` if your member type could appear in types that are not yours.
8. Reference `ShiftSoftware.ShiftMapper` as an ordinary dependency: analyzers included, not
   `PrivateAssets="all"`.
9. Turn on `EmitCompilerGeneratedFiles` and read `ShiftMapper.Declarations.g.cs` before you
   publish. If a rule is not in that file, no consumer will get it.
10. Treat removing or moving a declaration as a breaking change: a consumer's generated code bakes
    in that the pair converts, and one that ships without rebuilding throws on first map.
11. Fix SM0035, SM0038 and SM0002 in your own build; they are not repeated on the other side.
12. State the ShiftMapper version your package was built against. An application on an older one
    gets SM0033 and SM0028, and nothing from you.
