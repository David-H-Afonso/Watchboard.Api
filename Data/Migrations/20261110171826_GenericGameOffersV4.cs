using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GenericGameOffersV4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivationPlatform",
                table: "Prices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryType",
                table: "Prices",
                type: "TEXT",
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "LanguageRestriction",
                table: "Prices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CounterpartDiscoveryAttemptAt",
                table: "Games",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CounterpartOptOut",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CounterpartUrl",
                table: "Games",
                type: "TEXT",
                nullable: true);

            // Metadata only: no old amount/date/ID/goal/config is rewritten and no observations are
            // manufactured. V3's strict parser provenance is evidence of Steam keys, not gifts/other DRM.
            migrationBuilder.Sql("""
                UPDATE Prices SET ActivationPlatform = 'steam', DeliveryType = 'key'
                WHERE Provenance IN ('aks-published-payment-total', 'legacy-aks-filtered-payment-total')
                    AND Currency = 'EUR' AND MatchingOffers > 0 AND PriceCents > 0 AND PriceCents <> 2
                    AND upper(trim(Merchant)) <> 'STEAM' AND Region IN ('GLOBAL', 'EUROPE')
                    AND GameWatchId IN (SELECT Id FROM Games WHERE Provider = 'allkeyshop');
                UPDATE Prices SET ActivationPlatform = 'steam', DeliveryType = 'direct'
                WHERE Provenance IN ('steam-official-es-eur', 'legacy-steam-official-es-eur')
                    AND Currency = 'EUR' AND MatchingOffers > 0 AND upper(trim(Merchant)) = 'STEAM' AND Region = 'España'
                    AND GameWatchId IN (SELECT Id FROM Games WHERE Provider = 'steam');

                UPDATE Games SET CounterpartUrl = 'https://store.steampowered.com/app/' || SteamAppId || '/'
                WHERE Provider = 'allkeyshop' AND CounterpartStatus = 'verified' AND upper(trim(Edition)) = 'STANDARD'
                    AND SteamAppId IS NOT NULL AND length(SteamAppId) BETWEEN 1 AND 10
                    AND SteamAppId NOT GLOB '*[^0-9]*' AND substr(SteamAppId, 1, 1) <> '0';
                UPDATE Games AS original SET CounterpartUrl = (
                    SELECT min(other.SourceUrl) FROM Games AS other
                    WHERE other.OwnerId = original.OwnerId AND other.Provider = 'allkeyshop'
                        AND other.SteamAppId = original.SteamAppId AND other.CounterpartStatus = 'verified'
                        AND upper(trim(other.Edition)) = 'STANDARD'
                    HAVING count(DISTINCT other.SourceUrl) = 1)
                WHERE original.Provider = 'steam' AND original.CounterpartStatus = 'verified' AND upper(trim(original.Edition)) = 'STANDARD';
                UPDATE Games SET CounterpartUrl = CASE SteamAppId
                    WHEN '2988580' THEN 'https://www.allkeyshop.com/blog/buy-yakuza-0-directors-cut-cd-key-compare-prices/'
                    WHEN '1941540' THEN 'https://www.allkeyshop.com/blog/buy-mafia-the-old-country-cd-key-compare-prices/'
                    WHEN '1388590' THEN 'https://www.allkeyshop.com/blog/buy-yakuza-6-the-song-of-life-cd-key-compare-prices/' END
                WHERE Provider = 'steam' AND CounterpartStatus = 'verified' AND upper(trim(Edition)) = 'STANDARD'
                    AND CounterpartUrl IS NULL AND SteamAppId IN ('2988580', '1941540', '1388590');

                UPDATE Games AS original SET CounterpartOptOut = 1
                WHERE original.CounterpartStatus = 'verified' AND original.CounterpartPending = 0
                    AND upper(trim(original.Edition)) = 'STANDARD'
                    AND NOT EXISTS (SELECT 1 FROM Games AS other WHERE other.OwnerId = original.OwnerId
                        AND other.Provider <> original.Provider AND other.CounterpartStatus = 'verified'
                        AND upper(trim(other.Edition)) = 'STANDARD' AND other.SteamAppId = original.SteamAppId);
                UPDATE Games SET CounterpartPending = 1,
                    CounterpartNote = 'Pendiente de una única búsqueda pública exacta en la siguiente comprobación de precios; nunca se inventan observaciones.'
                WHERE CounterpartStatus IN ('needs_review', 'not_found') AND upper(trim(Edition)) = 'STANDARD'
                    AND CounterpartOptOut = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("A downgrade would discard platform/delivery/language provenance and deletion/opt-out safety. Stop Watchboard and restore the complete approved pre-v4 data/runtime snapshot instead.");
        }
    }
}
