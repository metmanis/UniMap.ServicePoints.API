# HERE Routing v8 - verified request shapes (Task 0)

Verified 2026-11-03 with one real call each. Origin fixed at Ankara `39.92,32.85`.
The API key is passed as `apiKey=<HERE_KEY>` (redacted here; never commit it). The key was not echoed in any response body.
Raw responses are saved unchanged in `tests/UniMap.ServicePoints.Tests/Here/Fixtures/`.

## Geometry format: FLEXIBLE POLYLINE, not GeoJSON

Isoline v8 returned `polygons[].outer` as a **flexible polyline encoded string** (e.g. `BG-smksC2ihz-...`). There is no GeoJSON.
Response shape (all three isoline calls):

```
{ "departure": { "time": "...", "place": { "type": "place", "location": {lat,lng}, "originalLocation": {lat,lng} } },
  "isolines": [ { "range": { "type": "time|distance", "value": N },
                  "polygons": [ { "outer": "<flexible polyline>" } ] } ] }
```

Observed: one polygon per range value, only `outer` present (no `inner` in these samples; Task 5 should still tolerate `inner`).
No `notices` field. The flexible polyline decodes to (lat, lng) pairs (lat first); GeoJSON/PostGIS need (lng, lat), so Task 5 must decode
the polyline and swap order, close the ring, and build the polygon itself (a decoder is needed: HERE's flexible-polyline algorithm, no GeoJSON shortcut).
`departure.time` is set to the current time (traffic-aware default); results can vary run to run.

## Isoline calls (GET https://isoline.router.hereapi.com/v8/isolines), all HTTP 200

| Fixture | Query (besides `apiKey`) |
|---|---|
| `isoline-car-time.json` | `transportMode=car&origin=39.92,32.85&range[type]=time&range[values]=300,600` |
| `isoline-car-distance.json` | `transportMode=car&origin=39.92,32.85&range[type]=distance&range[values]=2000,5000` |
| `isoline-truck-time.json` | `transportMode=truck&origin=39.92,32.85&range[type]=time&range[values]=300,600&vehicle[grossWeight]=12000&vehicle[height]=350&vehicle[width]=250&vehicle[length]=900` |

Notes: `range[values]` is comma-separated and returns one isoline per value in order; time is in seconds, distance in metres.
Vehicle units: grossWeight kg, height/width/length cm. Brackets were sent literally (unencoded, `curl -g` to disable globbing) in all three calls and HERE accepted them; the URL-encoded form (`%5B`/`%5D`) was not tested.

## Matrix call (POST https://matrix.router.hereapi.com/v8/matrix?apiKey=...&async=false), HTTP 200

Request body (fixture `matrix-car.json`):

```
{"origins":[{"lat":39.92,"lng":32.85}],
 "destinations":[{"lat":39.93,"lng":32.86},{"lat":39.95,"lng":32.80}],
 "regionDefinition":{"type":"world"},
 "transportMode":"car",
 "matrixAttributes":["distances","travelTimes"]}
```

Response: `{"matrixId":"...","matrix":{"numOrigins":1,"numDestinations":2,"travelTimes":[348,811],"distances":[2469,9242]},"regionDefinition":{"type":"world"}}`.
`distances` (metres) and `travelTimes` (seconds) are flat arrays, index = originIndex * numDestinations + destinationIndex.
`regionDefinition` is required for the sync call. Pair distance is confirmed to work (cars).

## Environment (Task 0 confirmations)

- Database: `UnicoServicePoints`, role `postgres` (superuser, so it has write access; credentials only in the git-ignored `.env`).
- PostgreSQL 18.6, PostGIS 3.6.3, `pg_trgm` 1.6 available (not installed in this database yet).
- `Administrative."Cities"` (81 rows) and `"Districts"` (929 rows): columns `Id integer`, `Name varchar`, `Geometry` (MultiPolygon, SRID 4326); all geometries `ST_IsValid`, none null.

## `Services."ServicePoints"` (restored by owner; checked read-only)

Columns (in order): `Id integer NOT NULL` (no default, no identity), `Code bigint null`, `Name text null`, `Type text null`, `Address text null`, `City text null`, `District text null`, `Phone text null`, `IsActive boolean null` (no default), `Geometry geometry(Point, 4326) null` (2D).
- Constraints: only the NOT NULL on `Id`. There is NO primary key, no unique constraint, no foreign keys, no indexes (not even on Geometry), no triggers.
- Rows: 1736 total; 391 null geometries; 0 invalid geometries (`ST_IsValid` on the 1345 non-null); 0 with a SRID other than 4326. `Id` is distinct for all rows. `Code` has 1729 distinct values and 1 null, so it is not unique.
- `Type`: 2 distinct values, none null.
- Active flag: `"IsActive"` exists and is the one clear candidate. Nullable boolean; values: true = 1736, false = 0, null = 0. Task 1 should NOT add `IsActive`; it should (optionally) set NOT NULL DEFAULT true and handle the 391 rows without geometry.
- Spec columns: code (`Code`), name (`Name`), type (`Type`), address (`Address`), city (`City`), district (`District`), phone (`Phone`) all exist. NO brand column exists (spec §8.1 `Brand` is missing; Task 1 `Brands."Brands"`/`Services."ServicePointsBrands"` cover it).
- Other objects in schema `Services`: none (no other tables, views or functions). So `Brands."Brands"`, `Services."ServicePointsBrands"`, `ServicePointVehicleTypes`, `ServicePointIsochrones` and `IsStale` do not exist yet.
- Task 1 notes: add a primary key on `Id` (rows are distinct, so it is safe) and a GiST index on `Geometry`; the role `postgres` has write privileges.

## Confirmed project decisions

- Migration tool: EF Core, with raw SQL for the PostGIS objects. Chosen because the plan assumes EF Core; the owner has not separately confirmed it beyond that assumption. No EF packages or migrations exist yet.
