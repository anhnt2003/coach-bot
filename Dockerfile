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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY certs/ /usr/local/share/ca-certificates/coachbot/
RUN update-ca-certificates
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_hostBuilder__reloadConfigOnChange=false
COPY --from=build /app .
# Non-root user provided by the .NET base image.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=60s --timeout=10s --start-period=30s --retries=3 CMD ["dotnet", "CoachBot.Host.dll", "health"]
ENTRYPOINT ["dotnet", "CoachBot.Host.dll"]
