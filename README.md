# Epic ERP Print Service

A small Windows service that lets the Epic ERP React frontend print PDFs **silently** on the user's PC,
without the browser's print dialog.

- Runs in the background as a Windows service (`EpicPrintService`) and **starts automatically at boot**. It restarts itself if it crashes.
- Exposes an HTTP API on **loopback only**, `http://127.0.0.1:9555`, so other machines can't reach it.
- Every call needs the API key in the `X-Api-Key` header.
- **CORS allows localhost origins only** (any port). Requests from any other web origin are rejected with `403`.
- Renders PDFs with Google's **PDFium** engine directly onto the printer's device context, so output is sharp and no external PDF reader is needed.
- Sends barcode label data (GoDEX EZPL) to the label printer **as a RAW job, byte for byte**.

There is nothing to configure. All settings are fixed in the code ([`ServiceConstants.cs`](src/PrintService/ServiceConstants.cs)):

| Setting | Value |
|---|---|
| Port | `9555` (loopback: `127.0.0.1` and `[::1]`) |
| API key (`X-Api-Key`) | `epichealthcare@2026printserver` |
| Allowed browser origins | `http(s)://localhost`, `127.0.0.1`, `[::1]`, any port |
| Max PDF size | 50 MB |
| Max raw print data size | 1 MB |
| Printer for `/print/pdf` and `/print/raw` | chosen by the caller in the `X-Printer-Name` header (required) |

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
- [Choosing the printer](#choosing-the-printer)
- [Label printers (RAW)](#label-printers-raw)
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

## Choosing the printer

Both print endpoints (`/api/v1/print/pdf` and `/api/v1/print/raw`) print on the printer the **caller names in the
`X-Printer-Name` header**. The header is required. The service no longer has a built-in or default printer.

- The name must match an **installed** printer exactly (case and surrounding spaces are ignored). Get the names from `GET /api/v1/printers`.
- A missing header, or a name that isn't installed, returns `400` before any job is spooled.
- Locally attached printers (USB, or network printers added via *Settings → Printers*) are installed per machine and visible to the service, which runs as **LocalSystem**.
- Printers **mapped per user** (e.g. `\\server\printer` connected from a user session or through a logon script) are **not** visible to LocalSystem and will not appear in the list. Install them for all users, or add them as a local TCP/IP port printer.

## Label printers (RAW)

`POST /api/v1/print/raw` sends the body to the named printer as a Windows **RAW** spooler job. It is meant for GoDEX label
printers (38 × 25 mm label stock in our case). On each PC:

1. Install the **GoDEX driver** for the model (e.g. *Godex G500*). If you don't have it, Windows' built-in
   **Generic / Text Only** driver works too, because the service never uses the driver to render anything.
   Install it on the machine (*Settings → Printers & scanners → Add device*, or the GoDEX installer run as
   administrator), **not per user**, so the service account can see it (see [Choosing the printer](#choosing-the-printer)).
2. Note the queue name (`Get-Printer | Where-Object DriverName -like '*Godex*'`) and send that name in `X-Printer-Name`.
   Renaming the queue to something stable (e.g. `Barcode_Print`) is optional but makes the client config the same on every PC:

   ```powershell
   Rename-Printer -Name 'Godex G500' -NewName 'Barcode_Print'
   ```

The new name takes effect immediately, with no restart needed. If Windows reports the printer offline, the API returns `503`.

**RAW jobs bypass the driver's rendering.** The data goes to the printer exactly as sent, so the driver's
*Printing Preferences* (paper size, darkness, speed, orientation) have **no effect**. Label size, darkness, speed,
gap/sensor and position all come from the EZPL commands in the data (`^Q`, `^W`, `^H`, `^S`, …) and from the printer's
own setup (calibration, front-panel / GoLabel settings). Calibrate the printer for the stock once.

---

## Security model

| Layer | What it does |
|---|---|
| **Loopback binding** | Kestrel listens only on `127.0.0.1` and `[::1]`. The API is unreachable from the network, so no firewall rule is needed. |
| **Origin guard** | If a request has an `Origin` header that isn't localhost, it's rejected with `403` **before** anything runs. CORS on its own only stops the browser from *reading* a response; this stops a malicious web page from *triggering* a print. |
| **CORS** | Allowed origins: `http(s)://localhost`, `127.0.0.1` and `[::1]` on any port. Allowed methods: `GET`, `POST`. Allowed request headers: `X-Api-Key`, `X-Printer-Name`, `Content-Type`. No credentials/cookies. |
| **API key** | Every request (except CORS preflight) needs `X-Api-Key: epichealthcare@2026printserver`, compared in constant time. Missing or wrong key → `401`. |
| **Input limits** | 50 MB PDF / 1 MB raw data size limits, content-type check, `%PDF-` signature check, `copies` 1–99, job name trimmed to 128 chars. |

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

Lists the printers installed for the service account. Use a `name` from this list in the `X-Printer-Name` header.

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
| `defaultPrinter` | The OS default printer of the service account, or `null` if there is none. Informational only: print requests always name their printer |
| `name` | Exact printer name |
| `isDefault` | `true` for the printer above |
| `status` | `Idle`, `Printing`, `Warmup`, `Stopped`, `Offline`, `Other`, `Unknown`, or `null` if Windows couldn't report it |
| `isOffline` | `true` if Windows reports the printer offline (including *Use Printer Offline*) |
| `isNetwork` | `true` for network-connected printer queues |

### `POST /api/v1/print/pdf`

Prints a PDF on the printer named in `X-Printer-Name`. The request **body is the raw PDF file** (binary, not multipart, not base64).

| | |
|---|---|
| Header `X-Printer-Name` | **required**, name of an installed printer (see [Choosing the printer](#choosing-the-printer)) |
| Header `Content-Type` | `application/pdf` (or `application/octet-stream`) |
| Query `copies` | optional, 1–99, default `1`. Copies are collated. |
| Query `jobName` | optional, name shown in the Windows print queue, default `ERP Document` (max 128 chars) |
| Query `exactSize` | optional, `true` prints every page 1:1 aligned to the paper edge (see below), default `false` |

```bash
curl -X POST "http://127.0.0.1:9555/api/v1/print/pdf?copies=2&jobName=Invoice%20INV-1001" \
     -H "X-Api-Key: epichealthcare@2026printserver" \
     -H "X-Printer-Name: HP LaserJet Pro M402-M403 n-dne PCL 6" \
     -H "Content-Type: application/pdf" \
     --data-binary "@invoice.pdf"
```

`200 OK`: returned once the job has been handed to the Windows spooler.

```json
{ "printer": "HP LaserJet Pro M402-M403 n-dne PCL 6", "jobName": "Invoice INV-1001", "pages": 3, "copies": 2 }
```

| Status | When |
|---|---|
| `400` | `X-Printer-Name` missing or not an installed printer, empty body, not a PDF, corrupted or password-protected PDF, or `copies` out of range |
| `413` | PDF larger than 50 MB |
| `415` | `Content-Type` isn't `application/pdf` / `application/octet-stream` |
| `503` | The printer is offline/unavailable or rejected the job |
| `500` | Unexpected printing error (details in the Event Log) |

**PDFs of different sizes.** Send one request per PDF. Paper size comes from each PDF page, so you don't send one, and a
single PDF with mixed page sizes works too:

- For each page the service picks the printer's paper with the same size (A4 page → A4, Letter → Letter, within ±1.3 mm).
  If the printer has no matching paper, it uses the printer's default paper. Add custom sizes as forms in Windows (see below).
- Each page prints at **actual size**. It's only shrunk, keeping its proportions, if it doesn't fit the printer's printable area, and it's centred on the sheet.
- Landscape pages are printed landscape. If the driver ignores orientation, the content is rotated to fit.
- Jobs are processed one at a time. Concurrent requests queue up.
- `200` means *spooled*, not *physically printed*. A printer that's out of paper will still accept the job.

**Exact size for letterhead (`exactSize=true`).** For pre-printed letterhead, every page prints **1:1**: no scaling and no
centring, with the page's top-left on the paper's top-left. Anything inside the printer's unprintable edge (usually 3–5 mm)
is clipped, so the layout must keep its content inside that edge.

A size like 215 × 292 mm isn't a standard paper, so set it up once on each PC that prints it:

1. *Print management*: run `printui /s /t2`, open the **Forms** tab, tick *Create a new form*, name it e.g. `Letterhead 215x292`, set
   width **21.5 cm** and height **29.2 cm**, all margins 0, then *Save Form*.
2. In the printer's *Printing Preferences*, choose that paper size for the tray that holds the letterhead.

The service then matches the PDF's 215 × 292 mm pages to that form automatically. Without the form, the driver's default
paper is used (e.g. A4, which is 5 mm narrower), and the page lands on the wrong paper size.

### `POST /api/v1/print/raw`

Prints raw printer data on the printer named in `X-Printer-Name`, e.g. GoDEX EZPL label data (plain ASCII text with CRLF
line endings; several labels can be concatenated into one job; see [Label printers](#label-printers-raw)). The service
sends the body to the printer as a Windows **RAW** spooler job, **byte for byte**. It isn't decoded or re-encoded, trimmed,
or line-ending normalised, no BOM is added or stripped, and nothing is appended. Nothing is rendered (no PDFium/GDI).

| | |
|---|---|
| Header `X-Printer-Name` | **required**, name of an installed printer |
| Header `Content-Type` | `text/plain` (any `charset` parameter, e.g. `text/plain; charset=utf-8`) or `application/octet-stream` |
| Query `jobName` | optional, name shown in the Windows print queue, default `Raw Print` (trimmed, max 128 chars) |

There is no paper or `copies` parameter. Every label print is logged by the ERP backend, so reprints must go through
it and can't be multiplied here. To print several labels, put them all in the body.

```bash
curl -X POST "http://127.0.0.1:9555/api/v1/print/raw?jobName=Sample%20labels%20S2610310001" \
     -H "X-Api-Key: epichealthcare@2026printserver" \
     -H "X-Printer-Name: Barcode_Print" \
     -H "Content-Type: text/plain; charset=utf-8" \
     --data-binary "@label.ezpl"
```

Use `--data-binary` (not `-d`/`--data`, which strip the line breaks).

`200 OK`: returned once the job has been handed to the Windows spooler (spooled, not physically printed).
`bytes` is the number of bytes sent, which is always the full body.

```json
{ "printer": "Barcode_Print", "jobName": "Sample labels S2610310001", "bytes": 412 }
```

| Status | Title | When |
|---|---|---|
| `400` | `Printer name required` / `Unknown printer` | `X-Printer-Name` is missing, or names a printer that isn't installed |
| `400` | `Empty body` | Body is 0 bytes or only whitespace |
| `413` | `Print data too large` | Body over 1 MB |
| `415` | `Unsupported content type` | `Content-Type` isn't `text/plain` / `application/octet-stream` |
| `503` | `Printer unavailable` | The printer is offline, or the spooler rejected the job (`OpenPrinter`, `StartDocPrinter` or `WritePrinter` failed, or not every byte was written). `detail` says which. A partly written job is cancelled. |
| `500` | `Print failed` | Unexpected error (details in the Event Log) |

Raw jobs share the queue with the PDF endpoint, so all jobs are processed one at a time.

> The former `POST /api/v1/print-compatibility-test` (fixed `report_print` printer) and `POST /api/v1/print-sample-label`
> (fixed `Barcode_Print` printer) routes have been removed. Use `/print/pdf?exactSize=true` and `/print/raw` with
> `X-Printer-Name` instead.

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

// printer: exact name of an installed printer (pick one from listPrinters()).
export async function printPdf(
  pdf: Blob,
  printer: string,
  opts: { copies?: number; jobName?: string; exactSize?: boolean } = {},
) {
  const qs = new URLSearchParams();
  if (opts.copies) qs.set('copies', String(opts.copies));
  if (opts.jobName) qs.set('jobName', opts.jobName);
  if (opts.exactSize) qs.set('exactSize', 'true');

  const res = await fetch(`${PRINT_SERVICE}/api/v1/print/pdf?${qs}`, {
    method: 'POST',
    headers: { 'X-Api-Key': PRINT_KEY, 'X-Printer-Name': printer, 'Content-Type': 'application/pdf' },
    body: pdf,
  });
  if (!res.ok) throw new Error((await res.json()).detail ?? res.statusText);
  return res.json(); // { printer, jobName, pages, copies }
}

// ezpl: the EZPL string (CRLF line endings) from the ERP backend, which logs every label print.
export async function printRaw(ezpl: string, printer: string, opts: { jobName?: string } = {}) {
  const qs = new URLSearchParams();
  if (opts.jobName) qs.set('jobName', opts.jobName);

  const res = await fetch(`${PRINT_SERVICE}/api/v1/print/raw?${qs}`, {
    method: 'POST',
    headers: { 'X-Api-Key': PRINT_KEY, 'X-Printer-Name': printer, 'Content-Type': 'text/plain; charset=utf-8' },
    body: ezpl, // sent as-is; the service doesn't change a byte
  });
  if (!res.ok) throw new Error((await res.json()).detail ?? res.statusText);
  return res.json(); // { printer, jobName, bytes }
}

// e.g. print a PDF fetched from the ERP backend on a chosen printer
const pdf = await (await fetch('/api/invoices/1001/pdf')).blob();
await printPdf(pdf, 'HP LaserJet Pro M402-M403 n-dne PCL 6', { jobName: 'Invoice 1001' });

// letterhead report, 1:1 without scaling
const report = await (await fetch('/api/compatibility-tests/42/report.pdf')).blob();
await printPdf(report, 'report_print', { jobName: 'Compatibility Test 42', exactSize: true });

// barcode labels on the GoDEX queue
await printRaw(ezpl, 'Barcode_Print', { jobName: 'Sample labels S2610310001' });
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
| `400 Printer name required` | The `X-Printer-Name` header is missing. Send the exact name of an installed printer. See [Choosing the printer](#choosing-the-printer). |
| `400 Unknown printer` | No installed printer has that name. Copy a `name` from `GET /api/v1/printers`. Per-user mapped printers are not visible to the service. |
| `503 Printer unavailable` | The named printer is offline or rejected the job. Check it is connected and not set to *Use Printer Offline*. |
| Labels print blank, shifted or the wrong size | RAW jobs ignore the driver settings. Fix the EZPL commands (`^Q`, `^W`, `^H`) or recalibrate the printer for the 38 × 25 mm stock. |
| Wrong paper size | The PDF's page size decides the paper. Generate the PDF at the size you want, and check that the printer offers that paper (Printing Preferences). |
| Printer missing from the list | It's mapped per user. Install it for all users / as a local port printer (see [Choosing the printer](#choosing-the-printer)). |
| Prints on the wrong printer | The client sent a different `X-Printer-Name` than intended. Check the header value. |
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
                                     # (the real RAW spooler test runs only if a v3 queue on NUL:/FILE: exists, e.g.
                                     #  Add-Printer -Name epic_raw_test -DriverName 'Generic / Text Only' -PortName 'NUL:')
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
│  ├─ ServiceConstants.cs             port, API key, size limits, service name
│  ├─ Security/                       OriginGuardMiddleware, LocalhostOriginPolicy, ApiKeyMiddleware
│  ├─ Printing/                       WindowsPrinterCatalog (printers + WMI status),
│  │                                  PdfiumPdfPrinter (PDFium → printer HDC), PdfiumNative (P/Invoke),
│  │                                  WinSpoolRawPrinter (RAW spooler jobs), PrintQueue (one job at a time)
│  └─ Endpoints/                      PrinterEndpoints, PrintEndpoints
└─ tests/PrintService.Tests/          xUnit + WebApplicationFactory
```

Adding an endpoint: create a `MapXxxEndpoints` extension in `Endpoints/` and call it in `Program.cs`. It's
automatically covered by the origin guard, CORS and API-key middleware.

### Third-party components

- [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD-3-Clause), via the prebuilt binaries in
  [`bblanchon.PDFium.Win32`](https://github.com/bblanchon/pdfium-binaries) (MIT)
- `System.Drawing.Common`, `System.Management`, `Microsoft.Extensions.Hosting.WindowsServices` (MIT)
