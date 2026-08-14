-- One database per owning service.
--
-- This is the rule that separates a service architecture from a distributed
-- monolith: exactly one service may read or write a given store, and everyone
-- else goes through that service's API. The moment two services share a table,
-- they can no longer be deployed, migrated or scaled independently — and you
-- have taken on all the cost of distribution with none of the benefit.
--
-- Locally these are three databases on one Postgres server. In production they
-- would be three separate clusters with different instance classes, backup
-- policies and scaling curves. Because no query ever joins across them, that
-- swap requires no application change.
--
--   jamex_users       Identity    strong consistency, low volume
--   jamex_catalog     Catalog     eventually consistent, read-heavy, huge
--   jamex_engagement  Engagement  comment writes; counters live in DynamoDB

CREATE DATABASE jamex_users      OWNER jamex;
CREATE DATABASE jamex_catalog    OWNER jamex;
CREATE DATABASE jamex_engagement OWNER jamex;

-- Trigram similarity, used by Catalog for the Postgres full-text search path
-- that is compared against the DynamoDB inverted index, and later for
-- near-duplicate title detection (chapter 4).
\connect jamex_catalog
CREATE EXTENSION IF NOT EXISTS pg_trgm;

\connect jamex_engagement
CREATE EXTENSION IF NOT EXISTS pg_trgm;
