FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["SeeNGO-Backend.csproj", "./"]
RUN dotnet restore "SeeNGO-Backend.csproj"

COPY . .
RUN dotnet publish "SeeNGO-Backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SeeNGO-Backend.dll"]