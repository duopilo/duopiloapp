[English](cloudflared.md) | **Deutsch**

# Cloudflare Tunnel für Kopilot einrichten

Ziel: `https://kopilot.flo210500.com` zeigt über einen **Named Tunnel** auf Kopilot (`http://127.0.0.1:5155`) auf dem Sim-PC. cloudflared läuft als **Windows-Dienst**. Am Router und an der Windows-Firewall wird **kein Port geöffnet**, cloudflared baut die Verbindung von innen nach außen auf.

```
Claude (Cloud) ──https──▶ Cloudflare ──Tunnel──▶ cloudflared (Dienst) ──▶ http://127.0.0.1:5155 ──▶ Kopilot ──▶ MSFS
```

Die Anleitung folgt der offiziellen Cloudflare-Doku für [lokal verwaltete Tunnel als Windows-Dienst](https://developers.cloudflare.com/tunnel/advanced/local-management/as-a-service/windows/).

## Voraussetzungen

- Die Domain **flo210500.com** liegt mit ihrem DNS bei Cloudflare. Im Cloudflare-Dashboard steht bei der Domain der Status *Aktiv*.
- Kopilot läuft im HTTP-Modus mit `PublicUrl = https://kopilot.flo210500.com` (siehe [README](../README.de.md), „Remote-Betrieb“).
- Eine **Eingabeaufforderung als Administrator**. Alle Befehle unten sind für `cmd.exe`.

## 1. cloudflared installieren

1. Ordner anlegen:
   ```bat
   mkdir C:\Cloudflared\bin
   ```
2. Auf der [Cloudflare-Downloadseite](https://developers.cloudflare.com/tunnel/downloads/) die Windows-64-Bit-Version herunterladen, nach `C:\Cloudflared\bin\` legen und in `cloudflared.exe` umbenennen.
3. Prüfen:
   ```bat
   cd /d C:\Cloudflared\bin
   cloudflared.exe --version
   ```

> Mit `winget install --id Cloudflare.cloudflared` geht es auch. Dann liegt die EXE woanders. Rufe sie in den Schritten 2 bis 7 ohne `cd` direkt als `cloudflared` auf und passe den EXE-Pfad im `ImagePath` in Schritt 8 an (`where cloudflared` zeigt ihn).

## 2. Bei Cloudflare anmelden

```bat
cloudflared.exe tunnel login
```

Es öffnet sich der Browser. Dort **flo210500.com** auswählen und bestätigen. Das Zertifikat landet in `%USERPROFILE%\.cloudflared\cert.pem`.

## 3. Tunnel anlegen

```bat
cloudflared.exe tunnel create kopilot
```

Die Ausgabe enthält die **Tunnel-ID** (eine UUID). Schreib sie dir auf, unten steht sie als `<TUNNEL-ID>`. Die Zugangsdatei liegt danach in `%USERPROFILE%\.cloudflared\<TUNNEL-ID>.json`.

## 4. Subdomain auf den Tunnel zeigen lassen

```bat
cloudflared.exe tunnel route dns kopilot kopilot.flo210500.com
```

Das legt bei Cloudflare den DNS-Eintrag `kopilot` als CNAME auf den Tunnel an.

## 5. Dateien für den Dienst bereitstellen

Der Dienst läuft als *LocalSystem* und sucht seine Dateien im Systemprofil, nicht in deinem Benutzerordner:

```bat
mkdir C:\Windows\System32\config\systemprofile\.cloudflared
copy %USERPROFILE%\.cloudflared\cert.pem C:\Windows\System32\config\systemprofile\.cloudflared\
copy %USERPROFILE%\.cloudflared\<TUNNEL-ID>.json C:\Windows\System32\config\systemprofile\.cloudflared\
```

## 6. config.yml anlegen

Den Editor **als Administrator** öffnen und als `C:\Windows\System32\config\systemprofile\.cloudflared\config.yml` speichern:

```yaml
tunnel: <TUNNEL-ID>
credentials-file: C:\Windows\System32\config\systemprofile\.cloudflared\<TUNNEL-ID>.json
logfile: C:\Cloudflared\cloudflared.log

ingress:
  - hostname: kopilot.flo210500.com
    service: http://127.0.0.1:5155
  - service: http_status:404
```

Wichtig:
- **Kein `httpHostHeader` setzen.** Kopilot braucht den öffentlichen Hostnamen, um Tunnel-Aufrufe von lokalen Aufrufen zu unterscheiden.
- Die letzte Regel (`http_status:404`) fängt alle anderen Hostnamen ab.

Die Regeln prüfen:

```bat
cloudflared.exe tunnel --config C:\Windows\System32\config\systemprofile\.cloudflared\config.yml ingress validate
```

## 7. Probelauf im Vordergrund

Kopilot muss laufen. Dann den Tunnel testweise im Fenster starten:

```bat
cloudflared.exe tunnel --config C:\Windows\System32\config\systemprofile\.cloudflared\config.yml run
```

In einem **zweiten** Fenster prüfen:

```bat
curl https://kopilot.flo210500.com/.well-known/oauth-protected-resource
curl -i https://kopilot.flo210500.com/mcp
curl -i https://kopilot.flo210500.com/state
```

Erwartet:
1. JSON mit `"resource":"https://kopilot.flo210500.com/mcp"`
2. `401 Unauthorized` mit `WWW-Authenticate: Bearer resource_metadata="…"`
3. `404 Not Found`, denn die Prüfseiten sind über den Tunnel gesperrt.

Danach den Probelauf mit `Strg+C` beenden.

## 8. Als Windows-Dienst einrichten

```bat
cd /d C:\Cloudflared\bin
cloudflared.exe service install
reg add HKLM\SYSTEM\CurrentControlSet\Services\Cloudflared /v ImagePath /t REG_EXPAND_SZ /d "C:\Cloudflared\bin\cloudflared.exe --config=C:\Windows\System32\config\systemprofile\.cloudflared\config.yml tunnel run" /f
sc stop cloudflared
sc start cloudflared
sc query cloudflared
```

Bei `sc query` muss `STATE : 4 RUNNING` stehen. Der Dienst startet ab jetzt automatisch mit Windows. Ein Fehler bei `sc stop` ist normal, wenn der Dienst noch nicht lief.

> Den `ImagePath` kannst du alternativ im Registrierungs-Editor unter `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Cloudflared` setzen. Achte darauf, dass keine zusätzlichen Leerzeichen hineinrutschen.

Nach Änderungen an `config.yml`:

```bat
sc stop cloudflared
sc start cloudflared
```

Danach die drei `curl`-Tests aus Schritt 7 wiederholen.

## Optional: Nur Claude an /mcp lassen (Cloudflare-WAF)

Claudes Server greifen aus dem Bereich `160.79.104.0/21` zu ([Anthropic IP-Adressen](https://platform.claude.com/docs/en/api/ip-addresses)). Mit einer WAF-Regel kannst du alles andere blocken. Ausgenommen bleibt nur die Freigabeseite `/authorize`, die du selbst im Browser öffnest.

Cloudflare-Dashboard → flo210500.com → **Security → WAF → Custom rules → Create rule**, Ausdruck bearbeiten:

```
(http.host eq "kopilot.flo210500.com" and not ip.src in {160.79.104.0/21} and http.request.uri.path ne "/authorize")
```

Aktion: **Block**.

Folge: Über die öffentliche URL kommen dann nur noch Claudes Server durch. Claude Code auf dem Sim-PC nutzt stattdessen die lokale Adresse `http://127.0.0.1:5155/mcp` mit dem Bearer-Token. Ändert Anthropic seinen IP-Bereich, musst du die Regel anpassen.

## Fehlersuche

| Symptom | Ursache |
|---|---|
| Cloudflare-Fehler **1033** | Der Tunnel läuft nicht. Prüfen mit `sc query cloudflared` und im Log `C:\Cloudflared\cloudflared.log`. |
| **502 Bad Gateway** | Der Tunnel läuft, aber Kopilot nicht, oder Kopilot nicht im HTTP-Modus bzw. auf einem anderen Port. |
| **400 Bad Request** (leere Seite) | Kopilots Host-Filter kennt den Hostnamen nicht. `PublicUrl` in `appsettings.Local.json` muss genau `https://kopilot.flo210500.com` sein. |
| `/.well-known/…` liefert **404** | OAuth ist in Kopilot nicht aktiv. Das Kopilot-Log sagt beim Start, warum (z. B. Passwort zu kurz). |
| Dienst startet nicht | `ImagePath` prüfen (Pfad zur EXE, keine Extra-Leerzeichen), dann `config.yml` mit `ingress validate` testen. |
