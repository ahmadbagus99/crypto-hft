FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY backend/CryptoHft.sln backend/CryptoHft.sln
COPY backend/src backend/src
COPY backend/tests backend/tests
RUN dotnet restore backend/CryptoHft.sln
RUN dotnet publish backend/src/CryptoHft.Api/CryptoHft.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoHft.Api.dll"]
