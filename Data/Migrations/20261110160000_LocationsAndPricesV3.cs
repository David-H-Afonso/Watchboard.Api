using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocationsAndPricesV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SoftwareLocationIdsJson",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SoftwareUpdatesOnly",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Software",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BasePriceCents",
                table: "Prices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeeBasis",
                table: "Prices",
                type: "TEXT",
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<int>(
                name: "FeeCents",
                table: "Prices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Prices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "Prices",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy-unknown");

            migrationBuilder.AddColumn<string>(
                name: "CounterpartNote",
                table: "Games",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CounterpartPending",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CounterpartStatus",
                table: "Games",
                type: "TEXT",
                nullable: false,
                defaultValue: "needs_review");

            // Legacy AKS observations were already strict Steam-key card/PayPal TOTALS. Preserve that
            // known provenance without fabricating a base/fee split or adding any historical readings.
            migrationBuilder.Sql("""
                UPDATE Prices SET FeeBasis = 'not_applicable', Provenance = 'legacy-steam-official-es-eur'
                WHERE GameWatchId IN (SELECT Id FROM Games WHERE Provider = 'steam');
                UPDATE Prices SET FeeBasis = 'included',
                    PaymentMethod = (SELECT Payment FROM Games WHERE Games.Id = Prices.GameWatchId),
                    Provenance = 'legacy-aks-filtered-payment-total'
                WHERE Currency = 'EUR' AND MatchingOffers > 0 AND PriceCents <> 2
                    AND GameWatchId IN (SELECT Id FROM Games WHERE Provider = 'allkeyshop' AND Payment IN ('card', 'paypal'));
                UPDATE Games SET CounterpartNote = 'Sin coincidencia recíproca verificada. Puedes indicar el enlace de la otra tienda para validarlo.';
                UPDATE Games SET SteamAppId = CASE
                    WHEN SourceUrl IN ('https://store.steampowered.com/app/2988580/', 'https://www.allkeyshop.com/blog/buy-yakuza-0-directors-cut-cd-key-compare-prices/') THEN '2988580'
                    WHEN SourceUrl IN ('https://store.steampowered.com/app/1941540/', 'https://www.allkeyshop.com/blog/buy-mafia-the-old-country-cd-key-compare-prices/') THEN '1941540'
                    ELSE '1388590' END,
                    CounterpartStatus = 'verified', CounterpartPending = 1,
                    CounterpartNote = 'Identidad base verificada. La siguiente comprobación de precios completará una única asociación conservando IDs, objetivos e historial.'
                WHERE upper(trim(Edition)) = 'STANDARD' AND SourceUrl IN (
                    'https://store.steampowered.com/app/2988580/',
                    'https://www.allkeyshop.com/blog/buy-yakuza-0-directors-cut-cd-key-compare-prices/',
                    'https://store.steampowered.com/app/1941540/',
                    'https://www.allkeyshop.com/blog/buy-mafia-the-old-country-cd-key-compare-prices/',
                    'https://store.steampowered.com/app/1388590/',
                    'https://www.allkeyshop.com/blog/buy-yakuza-6-the-song-of-life-cd-key-compare-prices/');
                """);

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.UniqueConstraint("AK_Locations_OwnerId_Id", x => new { x.OwnerId, x.Id });
                    table.ForeignKey(
                        name: "FK_Locations_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Software_OwnerId_LocationId",
                table: "Software",
                columns: new[] { "OwnerId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_OwnerId_NormalizedName",
                table: "Locations",
                columns: new[] { "OwnerId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Software_Locations_OwnerId_LocationId",
                table: "Software",
                columns: new[] { "OwnerId", "LocationId" },
                principalTable: "Locations",
                principalColumns: new[] { "OwnerId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("A downgrade would discard location assignments and quote provenance. Stop Watchboard and restore the complete pre-v3 data/runtime backup instead.");
        }
    }
}
