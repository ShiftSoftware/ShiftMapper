using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Entities;

namespace ShiftMapper.Sample.Data;

/// <summary>
/// The EF Core database context for the sample. Holds the five tables and their
/// relationships, and applies the seed data defined in <see cref="SeedData"/>.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    /// <summary>
    /// The TABLE-PER-HIERARCHY set — physical and digital items share one table and are told apart
    /// by a Discriminator column. Querying it gives you an IQueryable&lt;CatalogItem&gt; whose rows
    /// are really derived types, which is the situation Include and OfType are both answers to.
    /// </summary>
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Brand>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Country).HasMaxLength(100);
            b.Property(x => x.ISOCode).HasMaxLength(2);
        });

        modelBuilder.Entity<Stock>(s =>
        {
            s.Property(x => x.Name).HasMaxLength(100).IsRequired();
            s.Property(x => x.City).HasMaxLength(100);
            s.Property(x => x.Code).HasMaxLength(20);
        });

        modelBuilder.Entity<Product>(p =>
        {
            p.Property(x => x.Name).HasMaxLength(150).IsRequired();
            p.Property(x => x.Sku).HasMaxLength(50);
            // Store money with a fixed precision (SQL Server maps this to decimal(18,2)).
            p.Property(x => x.Price).HasColumnType("decimal(18,2)");

            p.HasOne(x => x.Brand)
                .WithMany(x => x.Products)
                .HasForeignKey(x => x.BrandId)
                .OnDelete(DeleteBehavior.Restrict);

            p.HasOne(x => x.Stock)
                .WithMany(x => x.Products)
                .HasForeignKey(x => x.StockId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Invoice>(i =>
        {
            i.Property(x => x.Number).HasMaxLength(30).IsRequired();
            i.Property(x => x.CustomerName).HasMaxLength(150).IsRequired();
            i.Property(x => x.CustomerEmail).HasMaxLength(150);
            i.HasIndex(x => x.Number).IsUnique();
        });

        modelBuilder.Entity<InvoiceLine>(l =>
        {
            l.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");

            l.HasOne(x => x.Invoice)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            l.HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // TPH. EF needs no instruction beyond discovering the derived types — it adds the
        // Discriminator column itself — but the lengths are worth setting like any other.
        modelBuilder.Entity<CatalogItem>(c =>
        {
            c.Property(x => x.Sku).HasMaxLength(50).IsRequired();
            c.Property(x => x.Name).HasMaxLength(150).IsRequired();
        });

        modelBuilder.Entity<PhysicalItem>()
            .Property(x => x.WeightKg).HasColumnType("decimal(18,3)");

        // Insert the demo rows (brands, stocks, products, a few invoices).
        SeedData.Apply(modelBuilder);
    }
}
