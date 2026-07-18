using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Endpoints;

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

app.Run();
