-- SAMPLE DATA FOR DEVELOPMENT ONLY.
--
-- This script invents vehicle types and brands for service points so the coverage feature can be tried
-- before the real relationships are loaded. None of it is real: it is derived from the point's "Id" and says
-- nothing about what any service point actually serves or sells. Do not treat it as fact and do not use it
-- for decisions.
--
-- What it does, for every row in "Services"."ServicePoints":
--   * "Services"."ServicePointVehicleTypes": PassengerCar for every point, plus HeavyDuty when "Id" % 5 = 0.
--   * "Services"."ServicePointsBrands": one or two brands taken from "Brands"."Brands" by "Id".
--       - HeavyDuty points get one heavy-duty brand (BMC, DAF, Iveco, MAN, Mercedes-Benz, Scania, Volvo,
--         Anadolu Isuzu, Karsan, Otokar, Temsa). Every other point gets one other brand.
--       - Every point with "Id" % 3 = 0 gets a second, non-heavy-duty brand.
--     Brand names that are missing from "Brands"."Brands" are simply not used, so run the KnownBrands migration first.
--
-- Deterministic and idempotent: the same data gives the same rows, and a re-run changes nothing.
-- A point is seeded only when it has no row at all in the table concerned, checked separately for
-- "ServicePointVehicleTypes" and "ServicePointsBrands". So a re-run stays stable however the brand list was
-- edited in between (adding or removing a name would otherwise shift which brand a point gets), and it never
-- adds to or overwrites a link that was adjusted by hand. A point that has lost all its links is seeded again.
-- Nothing is deleted. It runs in one transaction and touches only the two link tables and "IsStale".
--
-- "IsStale": inserting links fires the stale triggers, but seeding is not a real change, so the flag is put
-- back. Points that were already stale before the script ran keep their flag (a real change set it); the
-- script sets false only on the points the inserts flipped.
-- The vehicle type triggers also delete any cached isochrones of the vehicle types that were just added.
--
-- Run with:  psql "$CONNECTION" -v ON_ERROR_STOP=1 -f scripts/seed-sample-service-point-data.sql

BEGIN;

CREATE TEMP TABLE _seed_previously_stale ON COMMIT DROP AS
SELECT "Id" FROM "Services"."ServicePoints" WHERE "IsStale";

-- Position 0, 1, 2 ... within each pool, in a stable order.
CREATE TEMP TABLE _seed_brand_pool ON COMMIT DROP AS
SELECT pool, "Id" AS brand_id, row_number() OVER (PARTITION BY pool ORDER BY "NormalizedName") - 1 AS pos
FROM (
    SELECT b."Id", b."NormalizedName",
           CASE WHEN b."NormalizedName" IN (
                    SELECT "Services".normalize_text(n)
                    FROM unnest(ARRAY['BMC', 'DAF', 'Iveco', 'MAN', 'Mercedes-Benz', 'Scania', 'Volvo',
                                      'Anadolu Isuzu', 'Karsan', 'Otokar', 'Temsa']) AS n)
                THEN 'heavy' ELSE 'passenger' END AS pool
    FROM "Brands"."Brands" b
) classified;

INSERT INTO "Services"."ServicePointVehicleTypes" ("ServicePointId", "VehicleType")
SELECT sp."Id", 'PassengerCar' FROM "Services"."ServicePoints" sp
WHERE NOT EXISTS (SELECT 1 FROM "Services"."ServicePointVehicleTypes" v WHERE v."ServicePointId" = sp."Id")
UNION ALL
SELECT sp."Id", 'HeavyDuty' FROM "Services"."ServicePoints" sp
WHERE sp."Id" % 5 = 0
  AND NOT EXISTS (SELECT 1 FROM "Services"."ServicePointVehicleTypes" v WHERE v."ServicePointId" = sp."Id")
ON CONFLICT DO NOTHING;

INSERT INTO "Services"."ServicePointsBrands" ("ServicePointId", "BrandId")
SELECT wanted.point_id, pool.brand_id
FROM (
    -- The first brand: heavy-duty points take a heavy-duty brand, the others a non-heavy-duty one.
    SELECT sp."Id" AS point_id,
           CASE WHEN sp."Id" % 5 = 0 THEN 'heavy' ELSE 'passenger' END AS pool,
           CASE WHEN sp."Id" % 5 = 0 THEN (sp."Id" / 5) % NULLIF(sizes.heavy, 0)
                ELSE sp."Id" % NULLIF(sizes.passenger, 0) END AS pos
    FROM "Services"."ServicePoints" sp
    CROSS JOIN (SELECT count(*) FILTER (WHERE pool = 'heavy') AS heavy,
                       count(*) FILTER (WHERE pool = 'passenger') AS passenger
                FROM _seed_brand_pool) sizes
    WHERE NOT EXISTS (SELECT 1 FROM "Services"."ServicePointsBrands" l WHERE l."ServicePointId" = sp."Id")
    UNION ALL
    -- The second brand, on every third point: always non-heavy-duty.
    SELECT sp."Id", 'passenger', (sp."Id" + 7) % NULLIF(sizes.passenger, 0)
    FROM "Services"."ServicePoints" sp
    CROSS JOIN (SELECT count(*) AS passenger FROM _seed_brand_pool WHERE pool = 'passenger') sizes
    WHERE sp."Id" % 3 = 0
      AND NOT EXISTS (SELECT 1 FROM "Services"."ServicePointsBrands" l WHERE l."ServicePointId" = sp."Id")
) wanted
JOIN _seed_brand_pool pool ON pool.pool = wanted.pool AND pool.pos = wanted.pos
ON CONFLICT DO NOTHING;

UPDATE "Services"."ServicePoints"
SET "IsStale" = false
WHERE "IsStale" AND "Id" NOT IN (SELECT "Id" FROM _seed_previously_stale);

COMMIT;
