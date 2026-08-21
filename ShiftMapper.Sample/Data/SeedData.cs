using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Entities;

namespace ShiftMapper.Sample.Data;

/// <summary>
/// Demo data applied through EF Core's <c>HasData</c>. Because every row has an
/// explicit primary key and only scalar / foreign-key values are set (never
/// navigation properties), EF can insert it deterministically when the database
/// is created.
/// </summary>
public static class SeedData
{
    // Fixed timestamps — HasData needs constant values, so we never use DateTime.Now here.
    private static readonly DateTime Jan = new(2026, 1, 12, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb = new(2026, 2, 3, 14, 5, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar = new(2026, 3, 20, 9, 45, 0, DateTimeKind.Utc);

    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Brand>().HasData(
            new Brand { Id = 1, Name = "Apple", Country = "United States", FoundedYear = 1976, ISOCode = "US", Tags = ["premium", "mobile", "audio"] },
            new Brand { Id = 2, Name = "Samsung", Country = "South Korea", FoundedYear = 1938, ISOCode = "KR", Tags = ["mobile", "displays"] },
            new Brand { Id = 3, Name = "Sony", Country = "Japan", FoundedYear = 1946, ISOCode = "JP", Tags = ["audio", "imaging"] },
            new Brand { Id = 4, Name = "Dell", Country = "United States", FoundedYear = 1984, ISOCode = "US", Tags = ["computing", "enterprise"] },
            new Brand { Id = 5, Name = "LG", Country = "South Korea", FoundedYear = 1958, ISOCode = "KR", Tags = ["displays", "appliances"] },
            new Brand { Id = 6, Name = "Logitech", Country = "Switzerland", FoundedYear = 1981, ISOCode = "CH", Tags = ["accessories", "peripherals"] },
            new Brand { Id = 7, Name = "Anker", Country = "China", FoundedYear = 2011, ISOCode = "CN", Tags = ["accessories", "charging"] },
            new Brand { Id = 8, Name = "Bose", Country = "United States", FoundedYear = 1964, ISOCode = "US", Tags = ["premium", "audio"] }
        );

        modelBuilder.Entity<Stock>().HasData(
            new Stock { Id = 1, Name = "Central Warehouse", City = "Erbil", Code = "ERB-WH" },
            new Stock { Id = 2, Name = "Baghdad Retail Store", City = "Baghdad", Code = "BGD-RS" },
            new Stock { Id = 3, Name = "Sulaymaniyah Depot", City = "Sulaymaniyah", Code = "SUL-DP" },
            new Stock { Id = 4, Name = "Online Fulfillment", City = "Erbil", Code = "ERB-OF" }
        );

        modelBuilder.Entity<Product>().HasData(
            // Apple (brand 1)
            new Product { Id = 1, Name = "iPhone 15 Pro", Sku = "APL-IP15P", Price = 999.00m, QuantityOnHand = 40, BrandId = 1, StockId = 1 },
            new Product { Id = 2, Name = "MacBook Air M3", Sku = "APL-MBA-M3", Price = 1099.00m, QuantityOnHand = 25, BrandId = 1, StockId = 1 },
            new Product { Id = 3, Name = "iPad Air 11\"", Sku = "APL-IPADAIR", Price = 599.00m, QuantityOnHand = 30, BrandId = 1, StockId = 2 },
            new Product { Id = 4, Name = "AirPods Pro (2nd gen)", Sku = "APL-APP2", Price = 249.00m, QuantityOnHand = 120, BrandId = 1, StockId = 4 },
            new Product { Id = 5, Name = "Apple Watch Series 9", Sku = "APL-AWS9", Price = 399.00m, QuantityOnHand = 55, BrandId = 1, StockId = 2 },

            // Samsung (brand 2)
            new Product { Id = 6, Name = "Galaxy S24 Ultra", Sku = "SAM-S24U", Price = 1199.00m, QuantityOnHand = 35, BrandId = 2, StockId = 1 },
            new Product { Id = 7, Name = "Galaxy Tab S9", Sku = "SAM-TABS9", Price = 799.00m, QuantityOnHand = 20, BrandId = 2, StockId = 2 },
            new Product { Id = 8, Name = "Galaxy Buds2 Pro", Sku = "SAM-BUDS2P", Price = 229.00m, QuantityOnHand = 90, BrandId = 2, StockId = 4 },
            new Product { Id = 9, Name = "Odyssey G9 Monitor", Sku = "SAM-ODYG9", Price = 1299.00m, QuantityOnHand = 12, BrandId = 2, StockId = 3 },

            // Sony (brand 3)
            new Product { Id = 10, Name = "WH-1000XM5 Headphones", Sku = "SNY-WH1KXM5", Price = 399.00m, QuantityOnHand = 60, BrandId = 3, StockId = 4 },
            new Product { Id = 11, Name = "PlayStation 5 Slim", Sku = "SNY-PS5S", Price = 499.00m, QuantityOnHand = 45, BrandId = 3, StockId = 1 },
            new Product { Id = 12, Name = "Alpha a7 IV Camera", Sku = "SNY-A7IV", Price = 2499.00m, QuantityOnHand = 8, BrandId = 3, StockId = 3 },
            new Product { Id = 13, Name = "Bravia XR A80L TV", Sku = "SNY-XRA80L", Price = 1799.00m, QuantityOnHand = 10, BrandId = 3, StockId = 2 },

            // Dell (brand 4)
            new Product { Id = 14, Name = "XPS 15 Laptop", Sku = "DEL-XPS15", Price = 1899.00m, QuantityOnHand = 18, BrandId = 4, StockId = 1 },
            new Product { Id = 15, Name = "UltraSharp U2723QE Monitor", Sku = "DEL-U2723QE", Price = 579.00m, QuantityOnHand = 22, BrandId = 4, StockId = 3 },
            new Product { Id = 16, Name = "Latitude 7440", Sku = "DEL-LAT7440", Price = 1499.00m, QuantityOnHand = 15, BrandId = 4, StockId = 1 },

            // LG (brand 5)
            new Product { Id = 17, Name = "OLED C4 55\" TV", Sku = "LG-OLEDC4", Price = 1499.00m, QuantityOnHand = 14, BrandId = 5, StockId = 2 },
            new Product { Id = 18, Name = "Gram 17 Laptop", Sku = "LG-GRAM17", Price = 1699.00m, QuantityOnHand = 10, BrandId = 5, StockId = 1 },
            new Product { Id = 19, Name = "UltraGear 27\" Monitor", Sku = "LG-UG27", Price = 399.00m, QuantityOnHand = 26, BrandId = 5, StockId = 3 },

            // Logitech (brand 6)
            new Product { Id = 20, Name = "MX Master 3S Mouse", Sku = "LOG-MXM3S", Price = 99.00m, QuantityOnHand = 150, BrandId = 6, StockId = 4 },
            new Product { Id = 21, Name = "MX Keys S Keyboard", Sku = "LOG-MXKEYSS", Price = 109.00m, QuantityOnHand = 130, BrandId = 6, StockId = 4 },
            new Product { Id = 22, Name = "Brio 4K Webcam", Sku = "LOG-BRIO4K", Price = 199.00m, QuantityOnHand = 40, BrandId = 6, StockId = 2 },

            // Anker (brand 7)
            new Product { Id = 23, Name = "PowerCore 26800 Power Bank", Sku = "ANK-PC26800", Price = 65.00m, QuantityOnHand = 200, BrandId = 7, StockId = 4 },
            new Product { Id = 24, Name = "737 GaNPrime Charger", Sku = "ANK-737GAN", Price = 109.00m, QuantityOnHand = 110, BrandId = 7, StockId = 4 },
            new Product { Id = 25, Name = "Soundcore Liberty 4", Sku = "ANK-SCLIB4", Price = 99.00m, QuantityOnHand = 85, BrandId = 7, StockId = 2 },

            // Bose (brand 8)
            new Product { Id = 26, Name = "QuietComfort Ultra Headphones", Sku = "BOS-QCULTRA", Price = 429.00m, QuantityOnHand = 33, BrandId = 8, StockId = 2 }
        );

        modelBuilder.Entity<Invoice>().HasData(
            new Invoice { Id = 1, Number = "INV-2026-0001", CustomerName = "Ahmed Karim", CustomerEmail = "ahmed.karim@example.com", IssuedAt = Jan },
            new Invoice { Id = 2, Number = "INV-2026-0002", CustomerName = "Sara Hassan", CustomerEmail = "sara.hassan@example.com", IssuedAt = Feb },
            new Invoice { Id = 3, Number = "INV-2026-0003", CustomerName = "Omar Ali", CustomerEmail = "omar.ali@example.com", IssuedAt = Mar }
        );

        modelBuilder.Entity<InvoiceLine>().HasData(
            // Invoice 1
            new InvoiceLine { Id = 1, InvoiceId = 1, ProductId = 1, Quantity = 1, UnitPrice = 999.00m },
            new InvoiceLine { Id = 2, InvoiceId = 1, ProductId = 4, Quantity = 2, UnitPrice = 249.00m },
            new InvoiceLine { Id = 3, InvoiceId = 1, ProductId = 20, Quantity = 1, UnitPrice = 99.00m },

            // Invoice 2
            new InvoiceLine { Id = 4, InvoiceId = 2, ProductId = 6, Quantity = 1, UnitPrice = 1199.00m },
            new InvoiceLine { Id = 5, InvoiceId = 2, ProductId = 8, Quantity = 1, UnitPrice = 229.00m },

            // Invoice 3
            new InvoiceLine { Id = 6, InvoiceId = 3, ProductId = 11, Quantity = 2, UnitPrice = 499.00m },
            new InvoiceLine { Id = 7, InvoiceId = 3, ProductId = 10, Quantity = 1, UnitPrice = 399.00m },
            new InvoiceLine { Id = 8, InvoiceId = 3, ProductId = 26, Quantity = 1, UnitPrice = 429.00m },
            new InvoiceLine { Id = 9, InvoiceId = 3, ProductId = 23, Quantity = 3, UnitPrice = 65.00m }
        );
    }
}
