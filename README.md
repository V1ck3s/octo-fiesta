# Octo-Fiesta

A lightweight ASP.NET Core proxy/relay service for Subsonic API servers. This application acts as an intermediary between clients and Subsonic-compatible music streaming servers, forwarding API requests and responses.

## Features

- 🔄 Full Subsonic API relay support
- 🌐 CORS enabled for cross-origin requests
- 📝 Swagger/OpenAPI documentation (in development mode)
- 🔌 HTTP and HTTPS support
- 📦 Supports both GET and POST requests
- 🎯 Query parameter and JSON body parameter extraction
- ⚡ Built on .NET 9.0 for high performance

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- A Subsonic-compatible music server (e.g., Subsonic, Navidrome, Airsonic)

## Installation

1. Clone the repository:
```bash
git clone https://github.com/V1ck3s/octo-fiesta.git
cd octo-fiesta
```

2. Restore dependencies:
```bash
dotnet restore
```

## Configuration

Add the Subsonic server configuration to `appsettings.json`:

```json
{
  "Subsonic": {
    "Url": "http://your-subsonic-server:4533"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Or use an environment variable (recommended for production):
```bash
export Subsonic__Url="http://your-subsonic-server:4533"
```

## Usage

### Running the Application

**Development mode:**
```bash
cd octo-fiesta
dotnet run
```

The API will be available at:
- HTTP: `http://localhost:5274`
- HTTPS: `https://localhost:7248`
- Swagger UI: `http://localhost:5274/swagger` (development only)

**Production build:**
```bash
dotnet build --configuration Release
dotnet run --configuration Release
```

### API Endpoints

The proxy forwards all Subsonic API endpoints. Common examples:

- **Ping**: `GET/POST /ping` - Test server connectivity
- **Get Music Folders**: `GET/POST /getMusicFolders`
- **Get Artists**: `GET/POST /getArtists`
- **Get Album**: `GET/POST /getAlbum?id={albumId}`
- **Stream**: `GET/POST /stream?id={trackId}`

All parameters (query or JSON body) are forwarded to the configured Subsonic server.

### Example Requests

```bash
# Ping the server
curl "http://localhost:5274/ping?u=username&p=password&c=client&v=1.16.1"

# Get music folders
curl "http://localhost:5274/getMusicFolders?u=username&p=password&c=client&v=1.16.1&f=json"
```

## Development

### Project Structure

```
octo-fiesta/
├── Controllers/
│   └── SubSonicController.cs  # Main API controller
├── Models/
│   └── SubsonicSettings.cs    # Configuration model
├── Program.cs                  # Application entry point
├── appsettings.json           # Configuration file
└── octo-fiesta.csproj         # Project file
```

### Building

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

## CORS Configuration

The application is configured with permissive CORS settings to allow requests from any origin. Modify the CORS policy in `Program.cs` for production use:

```csharp
policy.AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader();
```

## License

This project is open source and available under the MIT License.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
