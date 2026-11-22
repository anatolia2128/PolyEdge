# Build ASP.NET Core 8 application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/PolyEdge.Web/PolyEdge.Web.csproj", "src/PolyEdge.Web/"]
RUN dotnet restore "src/PolyEdge.Web/PolyEdge.Web.csproj"
COPY . .
WORKDIR /src/src/PolyEdge.Web
RUN dotnet publish "PolyEdge.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    ConnectionStrings__Default="Data Source=/app/data/polyedge.db"
EXPOSE 8080
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
USER $APP_UID
ENTRYPOINT ["dotnet", "PolyEdge.Web.dll"]
