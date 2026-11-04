-- Schema-only baseline of the existing database before Task 1. No rows.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE SCHEMA IF NOT EXISTS "Services";

CREATE TABLE "Services"."ServicePoints" (
    "Id" integer NOT NULL,
    "Code" bigint,
    "Name" text,
    "Type" text,
    "Address" text,
    "City" text,
    "District" text,
    "Phone" text,
    "IsActive" boolean,
    "Geometry" geometry(Point, 4326)
);

-- Administrative boundaries (owned by another feature). Column types copied from the real database
-- by a read-only metadata query. Schema only, no rows.
CREATE SCHEMA IF NOT EXISTS "Administrative";

CREATE TABLE "Administrative"."Cities" (
    "Id" integer NOT NULL,
    "Geometry" geometry(MultiPolygon, 4326),
    "Name" character varying,
    CONSTRAINT "Cities_pkey" PRIMARY KEY ("Id")
);

CREATE TABLE "Administrative"."Districts" (
    "Id" integer NOT NULL,
    "Geometry" geometry(MultiPolygon, 4326),
    "Name" character varying,
    CONSTRAINT "Districts_pkey" PRIMARY KEY ("Id")
);
