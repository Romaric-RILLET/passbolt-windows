# Local Passbolt server

Passbolt CE server on `https://passbolt.test` (Docker), to reproduce issues with the desktop application.
Emails are captured by Mailpit on http://localhost:8025.

## One-time host setup

```powershell
.\tools\dev-server\Setup-Host.ps1   # PowerShell 7, self-elevates
```

It generates the `passbolt.test` TLS certificate in `certs\` (git ignored), trusts it, maps `passbolt.test`
to `127.0.0.1` in the hosts file and exempts the installed passbolt packages from the UWP loopback isolation
(UWP applications cannot reach `127.0.0.1` otherwise). Run it again after installing a new package identity.

## Start / stop

```powershell
docker compose -f tools\dev-server\docker-compose.yml up -d
docker compose -f tools\dev-server\docker-compose.yml down        # keep data
docker compose -f tools\dev-server\docker-compose.yml down -v     # reset everything
```

## Create a user

```powershell
docker compose -f tools\dev-server\docker-compose.yml exec passbolt su -m -c "/usr/share/php/passbolt/bin/cake passbolt register_user -u admin@passbolt.test -f Admin -l Dev -r admin" -s /bin/sh www-data
```

Open the printed setup link in a browser with the passbolt extension, complete the setup, then download the
account kit (Manage account > Desktop app setup) and import it in the desktop application.
