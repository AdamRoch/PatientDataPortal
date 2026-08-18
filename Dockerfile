FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build

WORKDIR /src

COPY global.json ./
COPY api/PatientDataPortal.Api.csproj api/
RUN dotnet restore api/PatientDataPortal.Api.csproj

COPY api/ api/
COPY infra/migrations/ infra/migrations/
RUN dotnet publish api/PatientDataPortal.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime

WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "PatientDataPortal.Api.dll"]
