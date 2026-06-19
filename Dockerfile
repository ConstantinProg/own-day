FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY OwnDay.sln ./
COPY src/OwnDay.Domain/OwnDay.Domain.csproj src/OwnDay.Domain/
COPY src/OwnDay.Application/OwnDay.Application.csproj src/OwnDay.Application/
COPY src/OwnDay.Infrastructure/OwnDay.Infrastructure.csproj src/OwnDay.Infrastructure/
COPY src/OwnDay.App/OwnDay.App.csproj src/OwnDay.App/

RUN dotnet restore OwnDay.sln

COPY . ./
RUN dotnet publish src/OwnDay.App/OwnDay.App.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN useradd --create-home --shell /bin/bash appuser

COPY --from=build /app/publish ./

USER appuser

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OwnDay.App.dll"]