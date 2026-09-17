FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY OwnDay.slnx ./
COPY src/OwnDay.Domain/OwnDay.Domain.csproj src/OwnDay.Domain/
COPY src/OwnDay.Application/OwnDay.Application.csproj src/OwnDay.Application/
COPY src/OwnDay.Infrastructure/OwnDay.Infrastructure.csproj src/OwnDay.Infrastructure/
COPY src/OwnDay.Host/OwnDay.Host.csproj src/OwnDay.Host/
COPY tests/OwnDay.UnitTests/OwnDay.UnitTests.csproj tests/OwnDay.UnitTests/
COPY tests/OwnDay.IntegrationTests/OwnDay.IntegrationTests.csproj tests/OwnDay.IntegrationTests/

RUN dotnet restore OwnDay.slnx

COPY . ./
RUN dotnet publish src/OwnDay.Host/OwnDay.Host.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM build AS migrations

RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
RUN /tools/dotnet-ef migrations bundle \
    --project src/OwnDay.Infrastructure/OwnDay.Infrastructure.csproj \
    --startup-project src/OwnDay.Host/OwnDay.Host.csproj \
    --configuration Release \
    --self-contained \
    --target-runtime linux-x64 \
    --output /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN useradd --create-home --shell /bin/bash appuser

COPY --from=build /app/publish ./
COPY --from=migrations /app/efbundle ./efbundle

USER appuser

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OwnDay.Host.dll"]
