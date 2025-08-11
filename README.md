# Transcription Tool (Scaffold)

- Backend: .NET 8 Web API (server/TranscriptionTool.Api)
- Database: SQL Server (prod) with SQLite fallback for dev (TRANSC_DB=sqlite)
- Client: Python (client/)

Step 1: API contracts and DB schema stubs.

Endpoints (stubs):
- POST /transcriptions
- GET /transcriptions/{id}
- GET /transcriptions/{id}/export?format=musicxml|pdf|midi|json
- POST /transcriptions/{id}/transpose?target=alto|tenor|baritone|soprano
- PATCH /transcriptions/{id}/notes

Run API (dev, SQLite):
cd server/TranscriptionTool.Api
export ASPNETCORE_ENVIRONMENT=Development
export TRANSC_DB=sqlite
dotnet restore
dotnet run
