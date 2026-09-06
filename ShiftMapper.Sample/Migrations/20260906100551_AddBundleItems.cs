using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShiftMapper.Sample.Migrations
{
    /// <inheritdoc />
    public partial class AddBundleItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ItemCount",
                table: "CatalogItems",
                type: "int",
                nullable: true);

            migrationBuilder.InsertData(
                table: "CatalogItems",
                columns: new[] { "Id", "Discriminator", "ItemCount", "Name", "Sku", "WeightKg" },
                values: new object[] { 5, "BundleItem", 2, "iPhone + AirPods Bundle", "apl-bndl", 0.24m });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "CatalogItems",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DropColumn(
                name: "ItemCount",
                table: "CatalogItems");
        }
    }
}
