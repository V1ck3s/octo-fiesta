# Octo-Fiesta

A lightweight ASP.NET Core bridge service that extends Subsonic API servers with streaming and downloading capabilities for music not available locally. This application acts as an intermediary between Subsonic-compatible clients and music servers, maintaining full API compatibility while adding new features for accessing external music sources.

## Features

- 🎵 **Stream and download music not available locally** - Access external music sources through the Subsonic API
- 🔄 Full Subsonic API compatibility - Works seamlessly with existing Subsonic clients
- 🌉 Bridge architecture - Maintains API compatibility while extending functionality
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

The application uses a **wildcard endpoint pattern** to forward all Subsonic API calls, with specific endpoints overloaded for enhanced functionality:

#### Overloaded Endpoints

- **`/ping`** - Test server connectivity with custom response parsing
  - Parses XML response and returns status
  - Supports both GET and POST methods

#### Wildcard Endpoint

- **`/{**endpoint}`** - Generic handler for all other Subsonic API endpoints
  - Forwards all parameters (query and JSON body) to the configured Subsonic server
  - Returns raw response with appropriate content type
  - Supports streaming (`/stream`) and download operations
  - Handles all standard Subsonic API calls:
    - `/getMusicFolders` - Get music folders
    - `/getArtists` - Get artists
    - `/getAlbum?id={albumId}` - Get album details
    - `/stream?id={trackId}` - Stream music tracks
    - `/download?id={trackId}` - Download music files
    - And all other Subsonic API endpoints

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

The application is configured with permissive CORS settings to allow requests from any origin. This is useful for development but should be restricted for production use.

Current configuration in `Program.cs`:

```csharp
policy.AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader()
    .WithExposedHeaders("X-Content-Duration", "X-Total-Count", "X-Nd-Authorization");
```

For production, consider restricting origins:

```csharp
policy.WithOrigins("https://your-frontend-domain.com")
    .AllowAnyMethod()
    .AllowAnyHeader()
    .WithExposedHeaders("X-Content-Duration", "X-Total-Count", "X-Nd-Authorization");
```

## License

This project is open source and available under the MIT License.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
