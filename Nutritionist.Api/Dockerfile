
# Build stage
<<<<<<< HEAD
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
=======
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
>>>>>>> origin/main

WORKDIR /src

COPY . .

<<<<<<< HEAD
RUN dotnet restore "Nutritionist.Api.csproj"

RUN dotnet publish "Nutritionist.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
=======
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
>>>>>>> origin/main

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
<<<<<<< HEAD
=======
ENV ASPNETCORE_ENVIRONMENT=Production
>>>>>>> origin/main

EXPOSE 10000

ENTRYPOINT ["dotnet", "Nutritionist.Api.dll"]
