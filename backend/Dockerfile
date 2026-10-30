# syntax=docker/dockerfile:1.7
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY . .
RUN dotnet restore ComicManagement.slnx
RUN dotnet publish backend/src/ComicManagement.Api/ComicManagement.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /out/api \
    /p:UseAppHost=false
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
RUN dotnet restore backend/src/ComicManagement.Persistence/ComicManagement.Persistence.csproj \
    --runtime linux-x64
RUN /tools/dotnet-ef migrations bundle \
    --project backend/src/ComicManagement.Persistence/ComicManagement.Persistence.csproj \
    --startup-project backend/src/ComicManagement.Persistence/ComicManagement.Persistence.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --output /out/migrations/efbundle \
    --force

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS api
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ComicManagement.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS migrator
WORKDIR /app
COPY --from=build /out/migrations/efbundle /app/efbundle
COPY backend/docker/migrate-entrypoint.sh /app/migrate-entrypoint.sh
RUN chmod 0555 /app/efbundle /app/migrate-entrypoint.sh
USER $APP_UID
ENTRYPOINT ["/app/migrate-entrypoint.sh"]
