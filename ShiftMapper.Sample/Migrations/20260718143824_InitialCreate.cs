using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ShiftMapper.Sample.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Brands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FoundedYear = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CustomerEmail = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    QuantityOnHand = table.Column<int>(type: "int", nullable: false),
                    BrandId = table.Column<int>(type: "int", nullable: false),
                    StockId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Products_Brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "Brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Products_Stocks_StockId",
                        column: x => x.StockId,
                        principalTable: "Stocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Brands",
                columns: new[] { "Id", "Country", "FoundedYear", "Name" },
                values: new object[,]
                {
                    { 1, "United States", 1976, "Apple" },
                    { 2, "South Korea", 1938, "Samsung" },
                    { 3, "Japan", 1946, "Sony" },
                    { 4, "United States", 1984, "Dell" },
                    { 5, "South Korea", 1958, "LG" },
                    { 6, "Switzerland", 1981, "Logitech" },
                    { 7, "China", 2011, "Anker" },
                    { 8, "United States", 1964, "Bose" }
                });

            migrationBuilder.InsertData(
                table: "Invoices",
                columns: new[] { "Id", "CustomerEmail", "CustomerName", "IssuedAt", "Number" },
                values: new object[,]
                {
                    { 1, "ahmed.karim@example.com", "Ahmed Karim", new DateTime(2026, 1, 12, 10, 30, 0, 0, DateTimeKind.Utc), "INV-2026-0001" },
                    { 2, "sara.hassan@example.com", "Sara Hassan", new DateTime(2026, 2, 3, 14, 5, 0, 0, DateTimeKind.Utc), "INV-2026-0002" },
                    { 3, "omar.ali@example.com", "Omar Ali", new DateTime(2026, 3, 20, 9, 45, 0, 0, DateTimeKind.Utc), "INV-2026-0003" }
                });

            migrationBuilder.InsertData(
                table: "Stocks",
                columns: new[] { "Id", "City", "Code", "Name" },
                values: new object[,]
                {
                    { 1, "Erbil", "ERB-WH", "Central Warehouse" },
                    { 2, "Baghdad", "BGD-RS", "Baghdad Retail Store" },
                    { 3, "Sulaymaniyah", "SUL-DP", "Sulaymaniyah Depot" },
                    { 4, "Erbil", "ERB-OF", "Online Fulfillment" }
                });

            migrationBuilder.InsertData(
                table: "Products",
                columns: new[] { "Id", "BrandId", "Name", "Price", "QuantityOnHand", "Sku", "StockId" },
                values: new object[,]
                {
                    { 1, 1, "iPhone 15 Pro", 999.00m, 40, "APL-IP15P", 1 },
                    { 2, 1, "MacBook Air M3", 1099.00m, 25, "APL-MBA-M3", 1 },
                    { 3, 1, "iPad Air 11\"", 599.00m, 30, "APL-IPADAIR", 2 },
                    { 4, 1, "AirPods Pro (2nd gen)", 249.00m, 120, "APL-APP2", 4 },
                    { 5, 1, "Apple Watch Series 9", 399.00m, 55, "APL-AWS9", 2 },
                    { 6, 2, "Galaxy S24 Ultra", 1199.00m, 35, "SAM-S24U", 1 },
                    { 7, 2, "Galaxy Tab S9", 799.00m, 20, "SAM-TABS9", 2 },
                    { 8, 2, "Galaxy Buds2 Pro", 229.00m, 90, "SAM-BUDS2P", 4 },
                    { 9, 2, "Odyssey G9 Monitor", 1299.00m, 12, "SAM-ODYG9", 3 },
                    { 10, 3, "WH-1000XM5 Headphones", 399.00m, 60, "SNY-WH1KXM5", 4 },
                    { 11, 3, "PlayStation 5 Slim", 499.00m, 45, "SNY-PS5S", 1 },
                    { 12, 3, "Alpha a7 IV Camera", 2499.00m, 8, "SNY-A7IV", 3 },
                    { 13, 3, "Bravia XR A80L TV", 1799.00m, 10, "SNY-XRA80L", 2 },
                    { 14, 4, "XPS 15 Laptop", 1899.00m, 18, "DEL-XPS15", 1 },
                    { 15, 4, "UltraSharp U2723QE Monitor", 579.00m, 22, "DEL-U2723QE", 3 },
                    { 16, 4, "Latitude 7440", 1499.00m, 15, "DEL-LAT7440", 1 },
                    { 17, 5, "OLED C4 55\" TV", 1499.00m, 14, "LG-OLEDC4", 2 },
                    { 18, 5, "Gram 17 Laptop", 1699.00m, 10, "LG-GRAM17", 1 },
                    { 19, 5, "UltraGear 27\" Monitor", 399.00m, 26, "LG-UG27", 3 },
                    { 20, 6, "MX Master 3S Mouse", 99.00m, 150, "LOG-MXM3S", 4 },
                    { 21, 6, "MX Keys S Keyboard", 109.00m, 130, "LOG-MXKEYSS", 4 },
                    { 22, 6, "Brio 4K Webcam", 199.00m, 40, "LOG-BRIO4K", 2 },
                    { 23, 7, "PowerCore 26800 Power Bank", 65.00m, 200, "ANK-PC26800", 4 },
                    { 24, 7, "737 GaNPrime Charger", 109.00m, 110, "ANK-737GAN", 4 },
                    { 25, 7, "Soundcore Liberty 4", 99.00m, 85, "ANK-SCLIB4", 2 },
                    { 26, 8, "QuietComfort Ultra Headphones", 429.00m, 33, "BOS-QCULTRA", 2 }
                });

            migrationBuilder.InsertData(
                table: "InvoiceLines",
                columns: new[] { "Id", "InvoiceId", "ProductId", "Quantity", "UnitPrice" },
                values: new object[,]
                {
                    { 1, 1, 1, 1, 999.00m },
                    { 2, 1, 4, 2, 249.00m },
                    { 3, 1, 20, 1, 99.00m },
                    { 4, 2, 6, 1, 1199.00m },
                    { 5, 2, 8, 1, 229.00m },
                    { 6, 3, 11, 2, 499.00m },
                    { 7, 3, 10, 1, 399.00m },
                    { 8, 3, 26, 1, 429.00m },
                    { 9, 3, 23, 3, 65.00m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId",
                table: "InvoiceLines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_ProductId",
                table: "InvoiceLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                table: "Invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_BrandId",
                table: "Products",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_StockId",
                table: "Products",
                column: "StockId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceLines");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Brands");

            migrationBuilder.DropTable(
                name: "Stocks");
        }
    }
}
