using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountsAndWatchV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Software_LocalTool",
                table: "Software");

            migrationBuilder.AddColumn<bool>(
                name: "IncludeInWidget",
                table: "Software",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Software",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<long>(
                name: "ReleasePublishedAt",
                table: "Software",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscountPercent",
                table: "Prices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalPriceCents",
                table: "Prices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SaleEndsAt",
                table: "Prices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditionKey",
                table: "Games",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IncludeInWidget",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Games",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<string>(
                name: "SteamAppId",
                table: "Games",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SessionStamp = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedWatchesSeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnlyActionable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowVersions = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowGames = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowSales = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowPriceDrops = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowErrors = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxItems = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            // A reserved account, NOT a default password. Setup persists its first locally chosen hash once.
            // Existing rows receive this exact owner through the non-null OwnerId column default above.
            migrationBuilder.Sql("""
                INSERT INTO Users (Id, Username, NormalizedUsername, DisplayName, PasswordHash, IsAdmin, Enabled,
                    SessionStamp, RequestedWatchesSeeded, OnlyActionable, ShowVersions, ShowGames, ShowSales,
                    ShowPriceDrops, ShowErrors, MaxItems)
                VALUES ('00000000-0000-0000-0000-000000000001', 'admin', 'ADMIN', 'Administrador', NULL,
                    1, 1, 'setup-pending', 0, 1, 1, 1, 0, 0, 0, 8);
                UPDATE Games SET EditionKey = upper(trim(Edition));
                """);
            // Preserve even manually duplicated legacy rows. One canonical key blocks new duplicate filters;
            // any pre-existing duplicates retain their IDs/history under an internal legacy discriminator.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT Id, row_number() OVER (
                        PARTITION BY OwnerId, SourceUrl, EditionKey, Region, Payment, OfficialOnly ORDER BY Id) AS position
                    FROM Games
                )
                UPDATE Games SET EditionKey = EditionKey || ':legacy:' || Id
                WHERE Id IN (SELECT Id FROM ranked WHERE position > 1);
                UPDATE Games SET SteamAppId = '1388590'
                WHERE Provider = 'allkeyshop'
                    AND SourceUrl = 'https://www.allkeyshop.com/blog/buy-yakuza-6-the-song-of-life-cd-key-compare-prices/';
                UPDATE Software SET Source = 'mamesj/pokemon-fulldex'
                WHERE Provider = 'github' AND lower(Source) = 'mamesj/pokemon-ds-widescreen';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Software_OwnerId_LocalTool",
                table: "Software",
                columns: new[] { "OwnerId", "LocalTool" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_OwnerId_SourceUrl_EditionKey_Region_Payment_OfficialOnly",
                table: "Games",
                columns: new[] { "OwnerId", "SourceUrl", "EditionKey", "Region", "Payment", "OfficialOnly" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Games_Users_OwnerId",
                table: "Games",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Software_Users_OwnerId",
                table: "Software",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Flattening per-user data into v1 would discard account security/ownership and can collide
            // with v1's global LocalTool index. Do not silently drop users or lose their configuration.
            throw new NotSupportedException("Account ownership cannot be safely downgraded. Stop Watchboard and restore the complete pre-v2 data/runtime backup instead.");
        }
    }
}
