# syntax=docker/dockerfile:1.7
# Multi-stage build: bundles the Vite SPA into the .NET image so the whole
# game runs as a single process on a single origin (no CORS, no second host).

# ---- 1) Build the SPA ----
FROM node:22-alpine AS spa
WORKDIR /spa
COPY client/package.json client/package-lock.json* ./
RUN npm ci
COPY client/ .
# Empty VITE_SERVER_URL → the SPA hits its own origin, which is the .NET process.
ENV VITE_SERVER_URL=""
RUN npm run build

# ---- 2) Build & publish the .NET server ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS srv
WORKDIR /src
COPY server/Dicerio.sln ./
COPY server/Dicerio.Engine/ ./Dicerio.Engine/
COPY server/Dicerio.Engine.Tests/ ./Dicerio.Engine.Tests/
COPY server/Dicerio.Server/ ./Dicerio.Server/
RUN dotnet publish Dicerio.Server/Dicerio.Server.csproj \
    -c Release \
    -o /out \
    --no-self-contained \
    /p:UseAppHost=false

# ---- 3) Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=srv /out ./
COPY --from=spa /spa/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production
# Render injects PORT (typically 10000); locally defaults to 8080.
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT} dotnet Dicerio.Server.dll"]
