# syntax=docker/dockerfile:1.7
ARG RELEASE_REVISION=development

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS api
ARG RELEASE_REVISION
LABEL org.opencontainers.image.revision=$RELEASE_REVISION
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

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30 AS migrator
ARG RELEASE_REVISION
LABEL org.opencontainers.image.revision=$RELEASE_REVISION
WORKDIR /app
COPY --from=build /out/migrations/efbundle /app/efbundle
COPY backend/docker/migrate-entrypoint.sh /app/migrate-entrypoint.sh
RUN chmod 0555 /app/efbundle /app/migrate-entrypoint.sh
USER $APP_UID
ENTRYPOINT ["/app/migrate-entrypoint.sh"]
