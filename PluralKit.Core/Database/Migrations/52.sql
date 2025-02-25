-- database version 52
--
-- add nullable official_api_token field to systems table

alter table systems add column official_api_token text;

update info set schema_version = 52;
