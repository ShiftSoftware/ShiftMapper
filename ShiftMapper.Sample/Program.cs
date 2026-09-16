using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Endpoints;
using ShiftMapper.Sample.Mapping;
using ShiftMapper.Sample.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? @"Server=.\SQLEXPRESS;Database=ShiftMapperSample;Trusted_Connection=True;TrustServerCertificate=True";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

// Indent the JSON response so it is easy to read while learning.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
});

// --- ShiftMapper -------------------------------------------------------------
// AppMapper (Mapping/AppMapper.cs) declares its maps in its CONSTRUCTOR and is an ordinary
// DI service, so it can inject whatever it needs. This call also fills in its Services
// property. The source generator reads those CreateMap lines at compile time and writes
// the Map methods onto it, plus extension methods so you can write brand.Map<BrandDto>(mapper).
// Try it: add a CreateMap line, rebuild, and look in Generated/ to see it appear.
// An ordinary service. AppMapper takes it through its constructor, and uses it inside a
// custom mapping — no ShiftMapper-specific registration involved.
builder.Services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();

// THE PACKAGE REGISTERS ITSELF. Contoso.Platform — the sample's stand-in for a framework package,
// modelled on ShiftFramework — ships this one call, the way a framework's AddXxx does, and it does
// two things. It registers the package's OWN mapper, PlatformMapper, from the package's own
// assembly, so it can be injected on its own (GET /api/framework/files). And it SHARES the
// package's pack — hash ids, the JSON column, the SelectDto convention — with every project that
// references the package: the package's build wrote that down as metadata, this project's
// generator read it, and every mapper the call below registers gets the pack as if
// o.AddConversions<PlatformConversions>() had been written at the end of it. The build says so
// (SM0043, an info at the call below), and a rule this project writes for the same pair wins.
//
// Nothing of the package's appears in the registration below, and nothing can be forgotten. That
// is the difference from a runtime mapper: there a framework could register its profiles itself,
// but a pack is applied by THIS project's generator, into Map methods and SQL projections, so the
// package has to reach that generator — which is what the shared-pack metadata is for.
builder.Services.AddContosoPlatform();

// THE REGISTRATION — and the generator reads this lambda as well as running it: anything written
// in it is baked into the mappers at compile time, exactly as if it had been written in their
// constructors. AppMapper is the mapper the endpoints inject. What it INCLUDES (CatalogMapper,
// InvoiceLabelMapper) and the pack it ADDS are registered along with it, so InvoiceLabelMapper's
// IInvoiceNumbering dependency is injected the first time anything is mapped without a
// registration of its own.
//
// Two calls, two mappers, one registry: IShiftMapper resolves to a composite that asks each one
// which pairs it maps. The order of the two calls does not matter.
builder.Services.AddShiftMapper(o => o.AddMapper<AppMapper>());

builder.Services.AddOpenApi();

var app = builder.Build();

// --- Apply migrations (creates the database + seed data) on startup ---------
// Database.Migrate() creates the "ShiftMapperSample" database on the local
// SQL Server Express instance if it is missing and applies any pending EF Core
// migrations, including the HasData seed rows. Create a new migration after
// changing the model with:  dotnet ef migrations add <Name>
// To reset from scratch:      dotnet ef database drop -f   (then run again)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// --- HTTP pipeline ----------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHomeEndpoints();
app.MapInvoiceEndpoints();
app.MapProductEndpoints();

// These two use the ShiftMapper-generated IShiftMapper.
app.MapBrandEndpoints();
app.MapStockEndpoints();

// Dictionaries, on a pair with no table behind it.
app.MapSupplierFeedEndpoints();

// Inheritance, polymorphism and open generics, over a table-per-hierarchy table.
app.MapCatalogEndpoints();

// Global type-pair conversions — one rule, and the two halves of the query-form decision.
app.MapConversionEndpoints();

// Rules arriving from a REFERENCED ASSEMBLY, through metadata rather than source.
app.MapFrameworkEndpoints();

// A member-shaped convention from that same assembly, filling shaped members in both backends.
app.MapProductListEndpoints();

app.Run();
