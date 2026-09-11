# Multi-stage build for SignalingServer
# Stage 1 (build): Uses the full .NET SDK to restore, build, and publish the app.
# Stage 2 (runtime): Uses the lightweight ASP.NET runtime image for production.

# ---- BUILD STAGE ----
# This stage compiles the application and produces optimized release binaries.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy all source files into the build container
COPY . .

# Restore NuGet packages for the entire solution
RUN dotnet restore DeskShare.sln

# Publish SignalingServer in Release mode to /app/publish
# This creates a framework-dependent deployment (smaller image size)
RUN dotnet publish src/SignalingServer/DeskShare.SignalingServer.csproj -c Release -o /app/publish

# ---- RUNTIME STAGE ----
# This stage uses the minimal ASP.NET runtime image (no SDK, no build tools).
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy the published application from the build stage
COPY --from=build /app/publish .

# Copy WebClient for static file serving (HTML/JS/CSS client)
COPY --from=build /src/src/WebClient /app/WebClient

# Expose ports:
# 5151 - HTTPS signaling server
# 9090 - Additional service port (e.g., metrics or WebSocket)
EXPOSE 5151
EXPOSE 9090

# Health check: verify the server is responding every 30 seconds.
# If /health endpoint fails 3 times in a row, Docker marks container as unhealthy.
HEALTHCHECK --interval=30s --timeout=5s CMD curl -kf https://localhost:5151/health || exit 1

# Start the SignalingServer when the container runs
ENTRYPOINT ["dotnet", "DeskShare.SignalingServer.dll"]
