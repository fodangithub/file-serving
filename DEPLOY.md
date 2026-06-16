# Production Deployment Guide

This guide covers deploying **fodan-file-serving** to a production environment.

## Prerequisites

- .NET 10.0 SDK (for building) or .NET 10.0 Runtime (for running)
- A server with a public IP or domain name
- (Optional) A reverse proxy (Nginx / Caddy / IIS) for TLS termination

## 1. Build the Application

```bash
dotnet publish src/FileSharingServer/FileSharingServer.csproj \
    -c Release \
    -o ./publish
```

The `./publish` directory contains a self-contained deployable output.

## 2. Configure for Production

Edit `appsettings.yaml` in the publish directory:

```yaml
Server:
  Port: 8080
  RootDirectory: "/data/files"       # Your file directory

Auth:
  Users:
    - Username: admin
      PasswordHash: "<your-hash>"    # Generate with: echo -n 'user+pass+2026' | sha256sum
    - Username: readonly
      PasswordHash: "<your-hash>"

Https:
  Enabled: true
  Port: 8443
  Domain: "files.example.com"        # Your actual domain
  CertDirectory: "Certificates-SelfSigned"
```

### Password Hash Generation

```bash
# Linux/macOS
echo -n 'adminYourStrongPassword2026' | sha256sum

# PowerShell
[BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes("adminYourStrongPassword2026")
    )
).Replace("-","").ToLower()
```

## 3. HTTPS Certificate Options

### Option A: Self-Signed (Internal / LAN Use)

The server auto-generates self-signed certificates on first startup. To trust them:

1. Copy `ca.crt` from the `Certificates-SelfSigned` directory
2. Import it into the client's trusted root certificate store:
   - **Windows**: `certmgr.msc` → Trusted Root Certification Authorities → Import
   - **macOS**: Keychain Access → System → Import → set to "Always Trust"
   - **Linux**: Copy to `/usr/local/share/ca-certificates/` and run `update-ca-certificates`
   - **Browser**: Settings → Privacy → Certificates → Import CA

### Option B: Let's Encrypt (Public Domain)

For public-facing deployments, use a real certificate:

1. Obtain a certificate via [Certbot](https://certbot.eff.org/) or [acme.sh](https://github.com/acmesh-official/acme.sh):
   ```bash
   certbot certonly --standalone -d files.example.com
   ```
2. Convert to the format the server expects:
   ```bash
   cp /etc/letsencrypt/live/files.example.com/fullchain.pem Certificates-SelfSigned/server.crt
   cp /etc/letsencrypt/live/files.example.com/privkey.pem Certificates-SelfSigned/server.key
   ```
3. The server will use these certificates (it won't overwrite existing files).
4. Set up auto-renewal via certbot's cron/systemd timer.

### Option C: Reverse Proxy TLS Termination

Use Nginx or Caddy as a reverse proxy and let it handle TLS:

**Nginx example:**

```nginx
server {
    listen 443 ssl http2;
    server_name files.example.com;

    ssl_certificate     /etc/letsencrypt/live/files.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/files.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

When using a reverse proxy, you can disable HTTPS in the app:

```yaml
Https:
  Enabled: false
```

> **Important**: When using a reverse proxy, Blazor Server's SignalR WebSocket connection requires the `Upgrade` and `Connection` headers to be forwarded (shown in the Nginx config above).

## 4. Running as a Service

### Linux (systemd)

Create `/etc/systemd/system/file-serving.service`:

```ini
[Unit]
Description=fodan-file-serving
After=network.target

[Service]
Type=simple
User=www-data
WorkingDirectory=/opt/file-serving
ExecStart=/usr/bin/dotnet /opt/file-serving/FileSharingServer.dll
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable file-serving
sudo systemctl start file-serving
sudo systemctl status file-serving
```

### Windows (Windows Service)

Use [NSSM](https://nssm.cc/) to install as a Windows service:

```powershell
nssm install file-serving "C:\Program Files\dotnet\dotnet.exe" "C:\file-serving\FileSharingServer.dll"
nssm set file-serving AppDirectory "C:\file-serving"
nssm start file-serving
```

### Docker

Create a `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ .
EXPOSE 8080 8443
VOLUME ["/data/files"]
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "FileSharingServer.dll"]
```

Build and run:

```bash
docker build -t fodan-file-serving .
docker run -d \
    --name file-serving \
    -p 8080:8080 \
    -p 8443:8443 \
    -v /path/to/files:/data/files \
    -v ./Certificates-SelfSigned:/app/Certificates-SelfSigned \
    --restart unless-stopped \
    fodan-file-serving
```

## 5. Firewall Configuration

Open the required ports:

```bash
# Linux (ufw)
sudo ufw allow 8080/tcp    # HTTP
sudo ufw allow 8443/tcp    # HTTPS

# Or with iptables
sudo iptables -A INPUT -p tcp --dport 8080 -j ACCEPT
sudo iptables -A INPUT -p tcp --dport 8443 -j ACCEPT
```

```powershell
# Windows Firewall
New-NetFirewallRule -DisplayName "File Server HTTP" -Direction Inbound -Port 8080 -Protocol TCP -Action Allow
New-NetFirewallRule -DisplayName "File Server HTTPS" -Direction Inbound -Port 8443 -Protocol TCP -Action Allow
```

## 6. Security Checklist

- [ ] Use strong, unique passwords for all configured users
- [ ] Enable HTTPS (`Https.Enabled: true`)
- [ ] Use a real TLS certificate for public-facing deployments (not self-signed)
- [ ] Place behind a reverse proxy for production (Nginx/Caddy)
- [ ] Restrict `RootDirectory` to only the intended file directory
- [ ] Run the service under a non-root / limited-privilege user
- [ ] Set up log rotation and monitoring
- [ ] Keep .NET Runtime updated for security patches
- [ ] If using a reverse proxy, forward `X-Forwarded-For` and `X-Forwarded-Proto` headers
- [ ] Back up `appsettings.yaml` and certificate files securely

## 7. Troubleshooting

| Issue | Cause | Solution |
|-------|-------|---------|
| Browser shows cert warning | Self-signed cert not trusted | Import `ca.crt` into trust store |
| 401 on every request | Wrong password hash | Regenerate hash: `echo -n 'user+pass+2026' \| sha256sum` |
| Right panel empty | Path issue in `RootDirectory` | Verify the directory exists and is accessible |
| WebSocket connection fails | Proxy not forwarding upgrade headers | Add `Upgrade` and `Connection` proxy headers |
| Port already in use | Another service on 8080/8443 | Change ports in `appsettings.yaml` |
