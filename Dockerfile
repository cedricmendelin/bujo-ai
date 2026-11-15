FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=80
EXPOSE 80
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Bujo.Host/Bujo.Host.csproj
RUN dotnet publish src/Bujo.Host/Bujo.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM runtime AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Bujo.Host.dll"]
