# --- Build stage ---
# The project multi-targets net8.0 and net8.0-windows; the -f net8.0 flag
# forces the cross-platform TFM so Docker (Linux) can build it.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore with just the csproj first to leverage the layer cache.
COPY YuSwitch.csproj .
RUN dotnet restore YuSwitch.csproj

# Copy the rest of the source and publish.
COPY . .
RUN dotnet publish YuSwitch.csproj -c Release -f net8.0 -o /app/publish

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 5078
ENTRYPOINT ["dotnet", "YuSwitch.dll"]
