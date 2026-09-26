# syntax=docker/dockerfile:1
# Rochell Core image (B-03, E-B03-5): the API host with the web UI's static export, plus the deployment CLI (rochell-migrate).
# The environment is chosen at run time (ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT); secrets never enter the image.

FROM node:24-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Rochell.slnx ./
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/Rochell.Api/Rochell.Api.csproj -c Release -o /out/api \
 && dotnet publish src/Rochell.Migrations.Cli/Rochell.Migrations.Cli.csproj -c Release -o /out/migrate

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /out/api /app/api
COPY --from=build /out/migrate /app/migrate
COPY --from=web /src/web/out /app/web
# Data Protection keys persist in a volume owned by the non-root user of the .NET images (app, uid 1654).
RUN mkdir -p /var/lib/rochell/keys && chown app:app /var/lib/rochell/keys
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Rochell__WebRoot=/app/web \
    Rochell__DataProtectionKeysPath=/var/lib/rochell/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/api/Rochell.Api.dll"]
