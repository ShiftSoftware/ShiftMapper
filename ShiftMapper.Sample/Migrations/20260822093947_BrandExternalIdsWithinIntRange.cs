using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShiftMapper.Sample.Migrations
{
    /// <inheritdoc />
    public partial class BrandExternalIdsWithinIntRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Brands",
                keyColumn: "Id",
                keyValue: 1,
                column: "ExternalIds",
                value: "[10010,10011]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Brands",
                keyColumn: "Id",
                keyValue: 1,
                column: "ExternalIds",
                value: "[4000000001,4000000002]");
        }
    }
}
