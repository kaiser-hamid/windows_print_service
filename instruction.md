Add a raw label printing endpoint to the Epic Print Service, next to /api/v1/print-compatibility-test.
Follow the existing conventions (PrintEndpoints.cs, ServiceConstants.cs, RFC 7807 problems,
X-Api-Key + localhost origin guard, CORS unchanged).

## Endpoint
POST /api/v1/print-sample-label

Prints sample barcode labels on the GoDEX label printer (38 x 25 mm stock). The body is raw printer
data (GoDEX EZPL, plain ASCII text with CRLF line endings), possibly several labels concatenated into
one job. The service must pass it to the printer BYTE FOR BYTE:
- do not decode it to a string and re-encode it, do not trim, do not normalise line endings, do not
  strip or add a BOM, do not append anything.
- send it with the Win32 spooler as a RAW job: OpenPrinter → StartDocPrinter (DOC_INFO_1 with
  pDatatype = "RAW", pDocName = jobName) → StartPagePrinter → WritePrinter (check that the bytes
  written equal the body length) → EndPagePrinter → EndDocPrinter → ClosePrinter. No PDFium/GDI rendering.
- jobs are processed one at a time, like the PDF endpoints (share the same queue/lock).

## Printer
Always the printer named exactly "label_print" (no printer/paper parameter in the request).
Add it to ServiceConstants next to the report printer:
    public const string SampleLabelPrinter = "label_print";
    public const long MaxRawSizeBytes = 1L * 1024 * 1024;   // 1 MB

## Request
Headers:
  X-Api-Key: epichealthcare@2026printserver
  Content-Type: text/plain (any charset parameter, e.g. "text/plain; charset=utf-8")
                or application/octet-stream
Query:
  jobName  optional, name in the Windows print queue, default "Sample Labels", trimmed, max 128 chars
No "copies" parameter: every label print is logged by the ERP backend, so copies must not bypass it.

Example:
  curl -X POST "http://127.0.0.1:9555/api/v1/print-sample-label?jobName=Sample%20labels%20S2610310001" \
       -H "X-Api-Key: epichealthcare@2026printserver" \
       -H "Content-Type: text/plain; charset=utf-8" \
       --data-binary "@label.ezpl"

## Response
200 OK, once the job is handed to the spooler (spooled, not physically printed):
  { "printer": "label_print", "jobName": "Sample labels S2610310001", "bytes": 412 }

## Errors (application/problem+json, { type, title, status, detail, traceId })
  400 "Empty body"            body is 0 bytes or only whitespace
  401                         X-Api-Key missing or wrong (existing middleware)
  403                         non-localhost Origin (existing middleware)
  413 "Label data too large"  body over 1 MB (check Content-Length first, then while reading)
  415 "Unsupported content type"  not text/plain or application/octet-stream
  503 "Printer unavailable"   no printer named "label_print" is installed, it is offline, or the
                              spooler rejected the job (OpenPrinter/StartDocPrinter/WritePrinter failed,
                              or fewer bytes were written than sent); detail says which
  500 "Print failed"          unexpected error (log it to the Event Log)

## Also
- Tests in tests/PrintService.Tests: content types (415), empty body (400), size limit (413), missing
  printer (503), and that the bytes handed to the raw printer abstraction equal the request body exactly
  (CRLF preserved, nothing added). Put the Win32 calls behind an interface (e.g. IRawPrinter) so it can be faked.
- PrintService.http: add a sample request.
- README: settings table row ("Printer for /print-sample-label | label_print"), an API reference section
  for the endpoint, and a "Label printer (label_print)" setup section: install the GoDEX driver (or
  "Generic / Text Only") on the machine, not per user, and rename the queue with
  Rename-Printer -Name '<GoDEX name>' -NewName 'label_print'. Note that RAW jobs bypass the driver's
  rendering, so label size and darkness come from the EZPL commands and the printer's own setup.