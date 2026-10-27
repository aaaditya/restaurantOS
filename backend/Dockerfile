FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json RestaurantOS.slnx ./
COPY src/RestaurantOS.Domain/RestaurantOS.Domain.csproj src/RestaurantOS.Domain/
COPY src/RestaurantOS.Infrastructure/RestaurantOS.Infrastructure.csproj src/RestaurantOS.Infrastructure/
COPY src/RestaurantOS.Api/RestaurantOS.Api.csproj src/RestaurantOS.Api/
RUN dotnet restore src/RestaurantOS.Api/RestaurantOS.Api.csproj
COPY src/ src/
RUN dotnet publish src/RestaurantOS.Api/RestaurantOS.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "RestaurantOS.Api.dll"]
