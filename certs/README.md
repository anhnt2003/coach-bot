# Extra root CAs (optional)

Only needed behind a TLS-inspecting proxy/firewall (e.g. a corporate FortiGate): put the proxy's root CA here as
`*.crt` (PEM). The Docker image trusts every `*.crt` in this folder during the build (NuGet restore) and at runtime
(COROS, Telegram, the LLM API). `*.crt` files are gitignored; on a normal VPS leave this folder empty.
