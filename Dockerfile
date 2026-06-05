# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /src

# Copy project files
COPY Backend/Backend.csproj Backend/
COPY Frontend Frontend/
COPY Database Database/

# Restore and build
WORKDIR /src/Backend
RUN dotnet restore
RUN dotnet publish -c Release -o /app/out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Copy built app
COPY --from=builder /app/out .

# Copy Frontend for static serving
COPY --from=builder /src/Frontend ../Frontend

# Expose port (Fly.io uses 8080)
EXPOSE 8080

# Set environment
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Run
ENTRYPOINT ["dotnet", "Backend.dll"]
