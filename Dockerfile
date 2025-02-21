# Use .NET SDK to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AudioFileProxyAPI/AudioFileProxyAPI.csproj", "./"]  #Change path if inside `src/`
RUN dotnet restore "./AudioFileProxyAPI.csproj"
COPY ./src ./
RUN dotnet publish -c Release -o /app/build

# Final stage: Run the application
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
COPY --from=build /app/build .
ENTRYPOINT ["dotnet", "AudioFileProxyAPI.dll"]
