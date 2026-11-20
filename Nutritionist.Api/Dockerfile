
# Stage 1: Build the ASP.NET Core API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY . .

RUN dotnet restore "Nutritionist.Api.csproj"

RUN dotnet publish "Nutritionist.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 2: Run the API
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:10000

EXPOSE 10000

ENTRYPOINT ["dotnet", "Nutritionist.Api.dll"]
