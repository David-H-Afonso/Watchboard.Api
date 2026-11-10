using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Edition = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    Payment = table.Column<string>(type: "TEXT", nullable: false),
                    OfficialOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetCents = table.Column<int>(type: "INTEGER", nullable: true),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Software",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Group = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    LocalTool = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledVersion = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledOn = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledMethod = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DetectionError = table.Column<string>(type: "TEXT", nullable: true),
                    LatestVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Software", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Prices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameWatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Merchant = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    Edition = table.Column<string>(type: "TEXT", nullable: false),
                    OfferUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Coupon = table.Column<string>(type: "TEXT", nullable: true),
                    MatchingOffers = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Prices_Games_GameWatchId",
                        column: x => x.GameWatchId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Prices_GameWatchId_ObservedAt",
                table: "Prices",
                columns: new[] { "GameWatchId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Software_LocalTool",
                table: "Software",
                column: "LocalTool",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Prices");

            migrationBuilder.DropTable(
                name: "Software");

            migrationBuilder.DropTable(
                name: "Games");
        }
    }
}
