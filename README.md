# Epic ERP Print Service

A small Windows service that lets the Epic ERP React frontend print PDFs **silently** on the user's PC,
without the browser's print dialog.

- Runs in the background as a Windows service (`EpicPrintService`) and **starts automatically at boot**. It restarts itself if it crashes.
- Exposes an HTTP API on **loopback only**, `http://127.0.0.1:9555`, so other machines can't reach it.
- Every call needs the API key in the `X-Api-Key` header.
- **CORS allows localhost origins only** (any port). Requests from any other web origin are rejected with `403`.
- Renders PDFs with Google's **PDFium** engine directly onto the printer's device context, so output is sharp and no external PDF reader is needed.

There is nothing to configure. All settings are fixed in the code ([`ServiceConstants.cs`](src/PrintService/ServiceConstants.cs)):

| Setting | Value |
|---|---|
| Port | `9555` (loopback: `127.0.0.1` and `[::1]`) |
| API key (`X-Api-Key`) | `epichealthcare@2026printserver` |
| Allowed browser origins | `http(s)://localhost`, `127.0.0.1`, `[::1]`, any port |
| Max PDF size | 50 MB |
| Printer for `/print/pdf` | OS default printer of the service account |
| Printer for `/print-compatibility-test` | `report_print` |

| | |
|---|---|
| Runtime | .NET 10 (self-contained; target PCs need **no** .NET install) |
| OS | Windows 10 / 11, Windows Server 2016+ (x64) |
| Service account | LocalSystem |
| Install folder | `C:\Program Files\EpicErp\PrintService\` |
| Logs | Event Viewer → Windows Logs → Application, source **Epic Print Service** |

To change any of these values, edit `ServiceConstants.cs`, rebuild with `publish.ps1` and reinstall.

---

## Contents

- [Install](#install)
- [Update](#update)
- [Uninstall](#uninstall)
- [Default printer](#default-printer)
- [Report printer (`report_print`)](#report-printer-report_print)
- [Security model](#security-model)
- [API reference](#api-reference)
- [Calling it from the React app](#calling-it-from-the-react-app)
- [Troubleshooting](#troubleshooting)
- [Development](#development)

---

## Install

### 1. Build the package (on a dev machine with the .NET 10 SDK)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

This produces `dist\EpicPrintService\` and `dist\EpicPrintService-win-x64.zip`:

```
EpicPrintService\
  app\            self-contained binaries
  install.ps1
  uninstall.ps1
```

### 2. Install on the target PC

Copy the folder (or unzip it) to the PC, open **PowerShell as Administrator** in that folder, and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The installer:

1. copies the binaries to `C:\Program Files\EpicErp\PrintService\`
2. registers the `EpicPrintService` service with **Automatic** start-up and restart-on-failure (5 s, 5 s, 30 s)
3. starts the service and checks that `GET /api/v1/printers` answers

Optional parameters: `-InstallDir <path>` installs somewhere other than Program Files, and `-SourceDir <path>`
takes the binaries from another folder. If you run `install.ps1` straight from the repository (with no `app\`
folder next to it), it runs `dotnet publish` itself, which needs the .NET 10 SDK.

Verify:

```powershell
Get-Service EpicPrintService          # Status: Running, StartType: Automatic
curl.exe -H "X-Api-Key: epichealthcare@2026printserver" http://127.0.0.1:9555/api/v1/printers
```

## Update

Run `install.ps1` from the new package again. It stops the service, replaces the binaries and starts it.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

This stops and deletes the service, then removes the install folder and the Event Log source.

---

## Default printer

`POST /api/v1/print/pdf` always prints on the **OS default printer of the service account**. The service runs as
**LocalSystem**, which has its *own* default printer, separate from the logged-in user's.
`GET /api/v1/printers` reports which printer that is (`defaultPrinter`).

- Locally attached printers (USB, or network printers added via *Settings → Printers*) are installed per machine and visible to the service.
- Printers **mapped per user** (e.g. `\\server\printer` connected from a user session or through a logon script) are **not** visible to LocalSystem. Install them for all users, or add them as a local TCP/IP port printer.
- To change which printer LocalSystem uses, run this in an elevated PowerShell, then restart the service:

  ```powershell
  # Sets the default printer for LocalSystem (S-1-5-18)
  $printer = 'HP LaserJet Pro M402-M403 n-dne PCL 6'
  New-Item -Path 'Registry::HKEY_USERS\S-1-5-18\Software\Microsoft\Windows NT\CurrentVersion\Windows' -Force | Out-Null
  Set-ItemProperty -Path 'Registry::HKEY_USERS\S-1-5-18\Software\Microsoft\Windows NT\CurrentVersion\Windows' -Name Device -Value "$printer,winspool,Ne00:"
  Restart-Service EpicPrintService
  ```

## Report printer (`report_print`)

`POST /api/v1/print-compatibility-test` ignores the default printer and always prints on a printer named exactly
**`report_print`**. On each PC, rename the printer meant for compatibility test reports:

*Settings → Bluetooth & devices → Printers & scanners → (printer) → Printer properties → General tab → name: `report_print`*

or, in an elevated PowerShell:

```powershell
Rename-Printer -Name 'HP LaserJet Pro M402-M403 n-dne PCL 6' -NewName 'report_print'
```

The service picks up the new name immediately, with no restart needed. Like any printer the service uses, it must be
installed on the machine rather than mapped per user (see [Default printer](#default-printer)). If no printer has
that name, the API returns `503`. Check with `GET /api/v1/printers`.

---

## Security model

| Layer | What it does |
|---|---|
| **Loopback binding** | Kestrel listens only on `127.0.0.1` and `[::1]`. The API is unreachable from the network, so no firewall rule is needed. |
| **Origin guard** | If a request has an `Origin` header that isn't localhost, it's rejected with `403` **before** anything runs. CORS on its own only stops the browser from *reading* a response; this stops a malicious web page from *triggering* a print. |
| **CORS** | Allowed origins: `http(s)://localhost`, `127.0.0.1` and `[::1]` on any port. Allowed methods: `GET`, `POST`. Allowed request headers: `X-Api-Key`, `Content-Type`. No credentials/cookies. |
| **API key** | Every request (except CORS preflight) needs `X-Api-Key: epichealthcare@2026printserver`, compared in constant time. Missing or wrong key → `401`. |
| **Input limits** | 50 MB size limit, content-type check, `%PDF-` signature check, `copies` 1–99, job name trimmed to 128 chars. |

> The key is compiled into the service and shipped in the React bundle, so treat it as a lock against other
> websites and casual misuse, not as a secret. Anything running on the PC can read it.

---

## API reference

Base URL: `http://127.0.0.1:9555` (or `http://localhost:9555`)

All requests need the header `X-Api-Key: epichealthcare@2026printserver`. Errors are returned as
[RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807) (`application/problem+json`):

```json
{ "type": "...", "title": "Invalid PDF", "status": 400, "detail": "The request body is not a PDF file (missing %PDF- header).", "traceId": "..." }
```

Common errors for every endpoint:

| Status | When |
|---|---|
| `401` | `X-Api-Key` missing or wrong |
| `403` | Request came from a browser origin that isn't localhost |

### `GET /api/v1/printers`

Lists the printers installed for the service account, and says which one jobs will go to.

```bash
curl -H "X-Api-Key: epichealthcare@2026printserver" http://127.0.0.1:9555/api/v1/printers
```

`200 OK`

```json
{
  "defaultPrinter": "HP LaserJet Pro M402-M403 n-dne PCL 6",
  "printers": [
    { "name": "HP LaserJet Pro M402-M403 n-dne PCL 6", "isDefault": true,  "status": "Idle",    "isOffline": false, "isNetwork": false },
    { "name": "Godex G500",                            "isDefault": false, "status": "Offline", "isOffline": true,  "isNetwork": false }
  ]
}
```

| Field | Description |
|---|---|
| `defaultPrinter` | The printer `POST /api/v1/print/pdf` will use, or `null` if there is no default printer |
| `name` | Exact printer name |
| `isDefault` | `true` for the printer above |
| `status` | `Idle`, `Printing`, `Warmup`, `Stopped`, `Offline`, `Other`, `Unknown`, or `null` if Windows couldn't report it |
| `isOffline` | `true` if Windows reports the printer offline (including *Use Printer Offline*) |
| `isNetwork` | `true` for network-connected printer queues |

### `POST /api/v1/print/pdf`

Prints a PDF on the default printer. The request **body is the raw PDF file** (binary, not multipart, not base64).

| | |
|---|---|
| Header `Content-Type` | `application/pdf` (or `application/octet-stream`) |
| Query `copies` | optional, 1–99, default `1`. Copies are collated. |
| Query `jobName` | optional, name shown in the Windows print queue, default `ERP Document` (max 128 chars) |

```bash
curl -X POST "http://127.0.0.1:9555/api/v1/print/pdf?copies=2&jobName=Invoice%20INV-1001" \
     -H "X-Api-Key: epichealthcare@2026printserver" \
     -H "Content-Type: application/pdf" \
     --data-binary "@invoice.pdf"
```

`200 OK`: returned once the job has been handed to the Windows spooler.

```json
{ "printer": "HP LaserJet Pro M402-M403 n-dne PCL 6", "jobName": "Invoice INV-1001", "pages": 3, "copies": 2 }
```

| Status | When |
|---|---|
| `400` | Empty body, not a PDF, corrupted or password-protected PDF, or `copies` out of range |
| `413` | PDF larger than 50 MB |
| `415` | `Content-Type` isn't `application/pdf` / `application/octet-stream` |
| `503` | No default printer, or the printer isn't available or rejected the job |
| `500` | Unexpected printing error (details in the Event Log) |

### `POST /api/v1/print-compatibility-test`

Prints a compatibility test report PDF, always on the printer named **`report_print`** (see
[Report printer](#report-printer-report_print)), whatever the default printer is.

Same request as `/api/v1/print/pdf`: the body is the raw PDF, `Content-Type: application/pdf`, with optional `copies` and
`jobName` (default `Compatibility Test Report`). There is no printer or paper-size parameter.

```bash
curl -X POST "http://127.0.0.1:9555/api/v1/print-compatibility-test?jobName=CT-2026-0042" \
     -H "X-Api-Key: epichealthcare@2026printserver" \
     -H "Content-Type: application/pdf" \
     --data-binary "@compatibility-report.pdf"
```

`200 OK`

```json
{ "printer": "report_print", "jobName": "CT-2026-0042", "pages": 2, "copies": 1 }
```

Errors are the same as `/api/v1/print/pdf`, except that `503` here means **no printer named `report_print` is installed**
(or it rejected the job).

**Exact size for letterhead.** The report is laid out for 215 × 292 mm pre-printed letterhead, so this endpoint prints every
page **1:1**: no scaling and no centring, with the page's top-left on the paper's top-left. Anything inside the printer's
unprintable edge (usually 3–5 mm) is clipped, so the report layout must keep its content inside that edge.

215 × 292 mm isn't a standard paper size, so set it up once on each `report_print` PC:

1. *Print management*: run `printui /s /t2`, open the **Forms** tab, tick *Create a new form*, name it `Letterhead 215x292`, set
   width **21.5 cm** and height **29.2 cm**, all margins 0, then *Save Form*.
2. In `report_print` → *Printing Preferences*, choose paper size `Letterhead 215x292` for the tray that holds the letterhead.

The service then matches the report's 215 × 292 mm pages to that form automatically. Without the form, the driver's default
paper is used (e.g. A4, which is 5 mm narrower), and the page lands on the wrong paper size.

### Printing behaviour (both print endpoints)

- **Paper size comes from the PDF**, so you don't send one. For each page the service picks the printer's paper with the same
  size (A4 page → A4, Letter → Letter, within ±1.3 mm). If the printer has no matching paper, it uses the printer's
  default paper. To control the paper, generate the PDF at that page size.
- `/print/pdf`: each page prints at **actual size**. It's only shrunk, keeping its proportions, if it doesn't fit the printer's printable area, and it's centred on the sheet.
- `/print-compatibility-test`: each page prints at **exact size** (1:1, aligned to the paper edge, never scaled). See above.
- Landscape pages are printed landscape. If the driver ignores orientation, the content is rotated to fit.
- Jobs are processed one at a time. Concurrent requests queue up.
- `200` means *spooled*, not *physically printed*. A printer that's out of paper will still accept the job.

---

## Calling it from the React app

The React app must be served from a **localhost** origin (e.g. `http://localhost:3000`).

```ts
// printService.ts
const PRINT_SERVICE = 'http://127.0.0.1:9555';
const PRINT_KEY = 'epichealthcare@2026printserver';

export async function listPrinters() {
  const res = await fetch(`${PRINT_SERVICE}/api/v1/printers`, {
    headers: { 'X-Api-Key': PRINT_KEY },
  });
  if (!res.ok) throw new Error((await res.json()).detail ?? res.statusText);
  return res.json(); // { defaultPrinter, printers: [...] }
}

export async function printPdf(pdf: Blob, opts: { copies?: number; jobName?: string } = {}) {
  const qs = new URLSearchParams();
  if (opts.copies) qs.set('copies', String(opts.copies));
  if (opts.jobName) qs.set('jobName', opts.jobName);

  const res = await fetch(`${PRINT_SERVICE}/api/v1/print/pdf?${qs}`, {
    method: 'POST',
    headers: { 'X-Api-Key': PRINT_KEY, 'Content-Type': 'application/pdf' },
    body: pdf,
  });
  if (!res.ok) throw new Error((await res.json()).detail ?? res.statusText);
  return res.json(); // { printer, jobName, pages, copies }
}

export async function printCompatibilityTestReport(pdf: Blob, opts: { copies?: number; jobName?: string } = {}) {
  const qs = new URLSearchParams();
  if (opts.copies) qs.set('copies', String(opts.copies));
  if (opts.jobName) qs.set('jobName', opts.jobName);

  const res = await fetch(`${PRINT_SERVICE}/api/v1/print-compatibility-test?${qs}`, {
    method: 'POST',
    headers: { 'X-Api-Key': PRINT_KEY, 'Content-Type': 'application/pdf' },
    body: pdf,
  });
  if (!res.ok) throw new Error((await res.json()).detail ?? res.statusText);
  return res.json(); // { printer: 'report_print', jobName, pages, copies }
}

// e.g. print a PDF fetched from the ERP backend on the default printer
const pdf = await (await fetch('/api/invoices/1001/pdf')).blob();
await printPdf(pdf, { jobName: 'Invoice 1001' });

// compatibility test report -> always the "report_print" printer
const report = await (await fetch('/api/compatibility-tests/42/report.pdf')).blob();
await printCompatibilityTestReport(report, { jobName: 'Compatibility Test 42' });
```

To detect whether the service is installed, call `listPrinters()`. A network error (`TypeError: Failed to fetch`)
means it isn't running on this PC.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `fetch` fails with a network error | The service isn't running. Check `Get-Service EpicPrintService` and try `Start-Service EpicPrintService`. |
| Browser console: *blocked by CORS policy* / API returns `403` | The page isn't served from localhost. Only localhost origins are allowed. |
| `401 Unauthorized` | Wrong or missing `X-Api-Key`. It must be exactly `epichealthcare@2026printserver`. |
| `503 No default printer` | LocalSystem has no default printer. See [Default printer](#default-printer). |
| `503` *Printer 'report_print' is not installed on this PC* | Rename the report printer to exactly `report_print`. See [Report printer](#report-printer-report_print). |
| Wrong paper size | The PDF's page size decides the paper. Generate the PDF at the size you want, and check that the printer offers that paper (Printing Preferences). |
| Printer missing from the list | It's mapped per user. Install it for all users / as a local port printer. |
| Prints on the wrong printer | LocalSystem's default differs from the user's. See [Default printer](#default-printer). |
| Job sits in the queue | Printer offline or out of paper. `200` only means the job was spooled. |
| Service won't start | Event Viewer → Application → source *Epic Print Service* / *.NET Runtime*. Common cause: port 9555 already in use (`Get-NetTCPConnection -LocalPort 9555`). |

Run it interactively to see logs in the console: stop the service, then run
`"C:\Program Files\EpicErp\PrintService\EpicErp.PrintService.exe"` from an elevated prompt. It runs as a normal console app when it isn't started by the Service Control Manager.

---

## Development

Requirements: .NET 10 SDK, Windows.

```powershell
dotnet build
dotnet test                          # security, API, and real PDFium rendering via "Microsoft Print to PDF"
dotnet run --project src/PrintService
```

`PrintService.http` has ready-made requests (VS Code REST Client / Rider / Visual Studio).

### Project layout

```
Print service/
├─ PrintService.slnx
├─ PrintService.http                  sample requests
├─ scripts/
│  ├─ publish.ps1                     builds dist\EpicPrintService (+ zip)
│  ├─ install.ps1                     install / update  (admin)
│  └─ uninstall.ps1                   (admin)
├─ src/PrintService/
│  ├─ Program.cs                      host, loopback Kestrel, middleware pipeline, endpoints
│  ├─ ServiceConstants.cs             port, API key, report printer name, size limit, service name
│  ├─ Security/                       OriginGuardMiddleware, LocalhostOriginPolicy, ApiKeyMiddleware
│  ├─ Printing/                       WindowsPrinterCatalog (printers + WMI status),
│  │                                  PdfiumPdfPrinter (PDFium → printer HDC), PdfiumNative (P/Invoke)
│  └─ Endpoints/                      PrinterEndpoints, PrintEndpoints
└─ tests/PrintService.Tests/          xUnit + WebApplicationFactory
```

Adding an endpoint: create a `MapXxxEndpoints` extension in `Endpoints/` and call it in `Program.cs`. It's
automatically covered by the origin guard, CORS and API-key middleware.

### Third-party components

- [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD-3-Clause), via the prebuilt binaries in
  [`bblanchon.PDFium.Win32`](https://github.com/bblanchon/pdfium-binaries) (MIT)
- `System.Drawing.Common`, `System.Management`, `Microsoft.Extensions.Hosting.WindowsServices` (MIT)
