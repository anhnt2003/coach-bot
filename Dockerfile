FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Optional extra root CAs (TLS-inspecting networks); see certs/README.md.
COPY certs/ /usr/local/share/ca-certificates/coachbot/
RUN update-ca-certificates
WORKDIR /src
# Restore first (cached unless project files change).
COPY Directory.Build.props Directory.Packages.props ./
COPY src/CoachBot.Core/CoachBot.Core.csproj src/CoachBot.Core/
COPY src/CoachBot.Infrastructure/CoachBot.Infrastructure.csproj src/CoachBot.Infrastructure/
COPY src/CoachBot.Host/CoachBot.Host.csproj src/CoachBot.Host/
RUN dotnet restore src/CoachBot.Host/CoachBot.Host.csproj
COPY src/ src/
RUN dotnet publish src/CoachBot.Host/CoachBot.Host.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# Claude Code CLI for Llm__Provider=claude_cli: the official binary, checksum-verified against the release manifest.
# Signed in at runtime through CLAUDE_CODE_OAUTH_TOKEN (see README, "LLM").
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS claude
ARG CLAUDE_CODE_VERSION=2.1.332
COPY certs/ /usr/local/share/ca-certificates/coachbot/
RUN update-ca-certificates
RUN set -eu; \
    base=https://downloads.claude.ai/claude-code-releases/$CLAUDE_CODE_VERSION; \
    case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; *) echo "unsupported: $(uname -m)" >&2; exit 1 ;; esac; \
    sum="$(curl -fsSL "$base/manifest.json" | tr -d ' \n' | grep -o "\"linux-$arch\":{[^}]*}" | grep -o '"checksum":"[0-9a-f]\{64\}"' | cut -d'"' -f4)"; \
    curl -fsSL -o /claude "$base/linux-$arch/claude"; \
    echo "$sum  /claude" | sha256sum -c -; \
    chmod 755 /claude

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY certs/ /usr/local/share/ca-certificates/coachbot/
RUN update-ca-certificates
COPY --from=claude /claude /usr/local/bin/claude
WORKDIR /app
# The CLI cannot update itself in the image (rebuild with another CLAUDE_CODE_VERSION) and trusts certs/ like the app.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_hostBuilder__reloadConfigOnChange=false \
    DISABLE_AUTOUPDATER=1 \
    NODE_EXTRA_CA_CERTS=/etc/ssl/certs/ca-certificates.crt
COPY --from=build /app .
# Non-root user provided by the .NET base image.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=60s --timeout=10s --start-period=30s --retries=3 CMD ["dotnet", "CoachBot.Host.dll", "health"]
ENTRYPOINT ["dotnet", "CoachBot.Host.dll"]
