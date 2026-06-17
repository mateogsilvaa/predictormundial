# --- Build ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore as a separate layer for caching.
COPY Oloraculo.Web/Oloraculo.Web.csproj Oloraculo.Web/
RUN dotnet restore Oloraculo.Web/Oloraculo.Web.csproj

# Build and publish.
COPY Oloraculo.Web/ Oloraculo.Web/
RUN dotnet publish Oloraculo.Web/Oloraculo.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# --- Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Render injects the port to listen on via $PORT (defaults to 8080 locally).
# exec keeps dotnet as PID 1 so it receives SIGTERM for a clean shutdown.
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet Oloraculo.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
