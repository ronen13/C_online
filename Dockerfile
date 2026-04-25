FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY QuizSystem.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Create persistent directory for SQLite DB
RUN mkdir -p /data
ENV DATABASE_PATH=/data/quiz.db

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "QuizSystem.dll"]
