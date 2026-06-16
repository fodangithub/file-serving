# fodan-file-serving

A lightweight file browsing and download server built with **C# / ASP.NET Core Blazor Server**. Provides secure file access with HTTP Basic Authentication, HTTPS support, and a clean responsive UI with light/dark theme.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)

## Features

- **HTTP Basic Authentication** — Browser-native auth popup (like Apache httpd), no login page needed
- **YAML Configuration** — Simple `appsettings.yaml` for all settings
- **SHA256 + Salt Password** — Passwords stored as `SHA256(username + password + "2026")` hashes
- **Directory Browsing** — Left-right split layout with recursive directory tree and file list
- **File Download** — Direct download links with path traversal protection
- **Hidden File Filtering** — Hides both Windows `Hidden` attribute files and dot-prefixed (`.git`, `.env`) entries
- **HTTPS with Self-Signed Certificates** — Auto-generates CA + server certificates on first run
- **Light/Dark Theme** — Toggle in the top-right corner, remembers preference via localStorage
- **Responsive UI** — Works on desktop and mobile browsers

## Project Structure

```
file-sharing-server/
├── FileSharingServer.slnx                    # Solution file
├── src/FileSharingServer/
│   ├── Program.cs                             # Entry point, middleware, endpoints
│   ├── FileSharingServer.csproj
│   ├── appsettings.yaml                       # Configuration (port, auth, HTTPS)
│   ├── Configuration/
│   │   └── AppSettings.cs                     # Strongly-typed config POCOs
│   ├── Auth/
│   │   └── BasicAuthMiddleware.cs             # HTTP Basic Auth middleware
│   ├── Services/
│   │   ├── FileService.cs                     # File/directory operations
│   │   └── CertificateGenerator.cs            # Self-signed cert generation
│   ├── Components/
│   │   ├── App.razor                          # HTML shell
│   │   ├── Routes.razor                       # Router config
│   │   ├── _Imports.razor                     # Shared imports
│   │   ├── Layout/
│   │   │   └── MainLayout.razor              # Header + footer layout
│   │   ├── Pages/
│   │   │   └── Home.razor                     # Main file browser page
│   │   ├── DirectoryTree.razor               # Left panel: recursive tree
│   │   └── FileList.razor                     # Right panel: file list
│   └── wwwroot/
│       ├── app.css                            # Styles (light/dark themes)
│       ├── images/                            # Logo assets
│       └── js/
│           └── theme.js                       # Theme toggle logic
└── tests/FileSharingServer.Tests/
    ├── Auth/
    │   └── BasicAuthMiddlewareTests.cs        # 12 auth tests
    └── Services/
        ├── FileServiceTests.cs                # 19 file service tests
        └── CertificateGeneratorTests.cs       # 10 certificate tests
```

## Configuration

All settings are in `src/FileSharingServer/appsettings.yaml`:

```yaml
Server:
  Port: 8080                          # HTTP port
  RootDirectory: "D:\\files"          # Directory to serve

Auth:
  Users:
    - Username: admin
      PasswordHash: "<hex>"           # SHA256(username + password + "2026")
    - Username: shared
      PasswordHash: "<hex>"

Https:
  Enabled: true                       # Enable/disable HTTPS
  Port: 8443                          # HTTPS port
  Domain: "localhost"                 # Certificate domain
  CertDirectory: "Certificates-SelfSigned"  # Certificate output directory
```

### Generating a Password Hash

```bash
# Example: username=admin, password=MyPassword123
echo -n 'adminMyPassword1232026' | sha256sum
```

## Quick Start

```bash
# Clone the repository
git clone <repo-url>
cd file-sharing-server

# Build
dotnet build

# Run tests
dotnet test

# Start the server
dotnet run --project src/FileSharingServer
```

The server will:
1. Auto-generate self-signed certificates (if HTTPS is enabled and certs don't exist)
2. Listen on HTTP `http://0.0.0.0:8080`
3. Listen on HTTPS `https://0.0.0.0:8443`
4. Prompt for credentials via browser auth popup on first visit

## Authentication

Uses **HTTP Basic Authentication** — the browser shows a native credential dialog before any page content loads. This is the same mechanism as Apache's `AuthType Basic`.

- **Stateless**: Every request is validated (no cookies or sessions)
- **Password format**: `SHA256(username + password + "2026")` stored as hex in YAML
- **Multi-user**: Configure multiple users in the YAML file

## HTTPS Certificates

On first run (when `Https.Enabled: true`), the server auto-generates in the `CertDirectory`:

| File | Description |
|------|-------------|
| `ca.crt` | Self-signed CA root certificate |
| `ca.key` | CA private key |
| `ca.pub` | CA public key |
| `server.crt` | Server certificate (signed by CA) |
| `server.key` | Server private key |
| `server.pub` | Server public key |

Existing certificates are **never overwritten**. To regenerate, delete the certificate directory.

## Security

- **Path traversal protection**: All file paths are validated against the root directory
- **Hidden file filtering**: Windows Hidden attribute + dot-prefixed entries are excluded
- **Basic Auth over HTTPS**: Credentials are encrypted in transit via TLS
- **No write operations**: Read-only — no upload, delete, or modify capabilities

## Tech Stack

- ASP.NET Core 10.0 / Blazor Server
- [NetEscapades.Configuration.Yaml](https://github.com/andrewlock/NetEscapades.Configuration) for YAML config
- xUnit for unit testing (41 tests)

## License

MIT
