using Microsoft.EntityFrameworkCore;

namespace PolyEdge.Web.Data;

public static class WeatherSchemaInitializer
{
    // EnsureCreated does not add tables to an already-existing database. These idempotent
    // CREATE statements add only the Weather tables and preserve all V1 trading data.
    public static Task InitializeAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "WeatherMarkets" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WeatherMarkets" PRIMARY KEY AUTOINCREMENT,
                "GammaMarketId" TEXT NOT NULL, "EventId" TEXT NULL, "Slug" TEXT NOT NULL,
                "Question" TEXT NOT NULL, "Description" TEXT NULL, "City" TEXT NULL,
                "Country" TEXT NULL, "Metric" TEXT NULL, "BucketMin" TEXT NULL,
                "BucketMax" TEXT NULL, "Unit" TEXT NULL, "ResolutionSource" TEXT NULL,
                "EndDateUtc" TEXT NULL, "IsActive" INTEGER NOT NULL, "IsClosed" INTEGER NOT NULL,
                "MappingStatus" TEXT NOT NULL, "FirstDiscoveredUtc" TEXT NOT NULL,
                "LastDiscoveredUtc" TEXT NOT NULL, "RawJson" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WeatherMarkets_GammaMarketId" ON "WeatherMarkets" ("GammaMarketId");
            CREATE INDEX IF NOT EXISTS "IX_WeatherMarkets_Slug" ON "WeatherMarkets" ("Slug");
            CREATE INDEX IF NOT EXISTS "IX_WeatherMarkets_IsActive_IsClosed" ON "WeatherMarkets" ("IsActive", "IsClosed");
            CREATE INDEX IF NOT EXISTS "IX_WeatherMarkets_MappingStatus" ON "WeatherMarkets" ("MappingStatus");

            CREATE TABLE IF NOT EXISTS "WeatherStations" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WeatherStations" PRIMARY KEY AUTOINCREMENT,
                "ICAO" TEXT NOT NULL, "IATA" TEXT NULL, "Name" TEXT NOT NULL,
                "City" TEXT NULL, "Country" TEXT NULL, "Latitude" REAL NULL,
                "Longitude" REAL NULL, "ElevationMeters" REAL NULL,
                "IsActive" INTEGER NOT NULL, "LastUpdatedUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WeatherStations_ICAO" ON "WeatherStations" ("ICAO");
            CREATE INDEX IF NOT EXISTS "IX_WeatherStations_IATA" ON "WeatherStations" ("IATA");

            CREATE TABLE IF NOT EXISTS "WeatherMarketStationMappings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WeatherMarketStationMappings" PRIMARY KEY AUTOINCREMENT,
                "WeatherMarketId" INTEGER NOT NULL, "WeatherStationId" INTEGER NOT NULL,
                "MatchMethod" TEXT NOT NULL, "Confidence" TEXT NOT NULL,
                "IsVerified" INTEGER NOT NULL, "VerifiedBy" TEXT NULL,
                "VerifiedAtUtc" TEXT NULL, "CreatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "FK_WeatherMarketStationMappings_WeatherMarkets_WeatherMarketId"
                    FOREIGN KEY ("WeatherMarketId") REFERENCES "WeatherMarkets" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_WeatherMarketStationMappings_WeatherStations_WeatherStationId"
                    FOREIGN KEY ("WeatherStationId") REFERENCES "WeatherStations" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WeatherMarketStationMappings_WeatherMarketId_WeatherStationId"
                ON "WeatherMarketStationMappings" ("WeatherMarketId", "WeatherStationId");
            """, ct);
}
