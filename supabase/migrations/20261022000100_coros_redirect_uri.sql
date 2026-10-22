-- The redirect URI the OAuth client was registered with. A stored client is reused only while the redirect URI still
-- matches (it changes with Coach__PublicBaseUrl); otherwise COROS rejects the authorize request as unregistered.
alter table coach.coros_connections add column redirect_uri text;
