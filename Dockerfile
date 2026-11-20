FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__Default="Data Source=/app/data/polyedge.db"
RUN mkdir -p /app/data
VOLUME ["/app/data"]

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/PolyEdge.Web/PolyEdge.Web.csproj", "src/PolyEdge.Web/"]
RUN dotnet restore "src/PolyEdge.Web/PolyEdge.Web.csproj"
COPY . .
WORKDIR "/src/src/PolyEdge.Web"
RUN dotnet publish "PolyEdge.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PolyEdge.Web.dll"]
