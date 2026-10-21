-- COROS' authorization server rejects public clients at the device-authorization endpoint (verified live: 401),
-- so CoachBot registers as a confidential client. The secret is stored AES-GCM encrypted like the tokens.
alter table coach.coros_connections add column encrypted_client_secret text;
