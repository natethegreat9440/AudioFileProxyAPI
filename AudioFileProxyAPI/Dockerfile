# Use .NET SDK to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy only the .csproj file first (better caching)
COPY ["AudioFileProxyAPI.csproj", "./"]
RUN dotnet restore "AudioFileProxyAPI.csproj"

# Copy the rest of the project and build it
COPY . .
RUN dotnet publish -c Release -o /out

# Final stage: Run the application
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "AudioFileProxyAPI.dll"]


# Use .NET SDK to build the app
# FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# WORKDIR /AudioFileProxyAPI

# # Copy only the .csproj file first (ensures faster builds with caching)
# COPY ["AudioFileProxyAPI/AudioFileProxyAPI.csproj", "AudioFileProxyAPI/"]
# #WORKDIR /src/AudioFileProxyAPI
# RUN dotnet restore "AudioFileProxyAPI.csproj"

# # Copy the entire project and build it
# COPY ./AudioFileProxyAPI ./
# #WORKDIR /src/AudioFileProxyAPI
# #RUN dotnet publish -c Release -o /app/build
# RUN dotnet publish -c Release -o /out

# # Final stage: Run the application
# FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
# WORKDIR /app
# COPY --from=build /out .
# ENTRYPOINT ["dotnet", "AudioFileProxyAPI.dll"]



