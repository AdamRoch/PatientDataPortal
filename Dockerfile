FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "PatientDataPortal.Api.dll"]
