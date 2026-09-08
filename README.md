# QR Bulk CLI

Generate QR codes **offline** from text, URLs, CSV/TSV, or a pipeline. Save individual
PNG/SVG images or a paginated PDF label sheet. Runs on Windows, Linux, and macOS;
no desktop UI, printer driver, or system fonts required.

```powershell
qr-bulk --text "https://example.com" --output qr.png
qr-bulk --input links.txt --output codes
qr-bulk --input items.csv --column url --label-column name --output labels.pdf
```

## Install

### No .NET installation needed

Download the archive for your operating system and CPU from
[Releases](https://github.com/morning4coffe-dev/qr-bulk-gen-cli/releases/latest)
and extract it. Windows uses `.zip`; Linux/macOS use `.tar.gz`. `x64` is for
Intel/AMD; `arm64` is for ARM, including Apple Silicon.

On Windows, run `.\qr-bulk.exe --help` from the extracted folder.
On Linux/macOS, run `./qr-bulk --help` (use `chmod +x qr-bulk` if necessary).
Add that folder to your `PATH` to use `qr-bulk` from anywhere.
Release assets include `SHA256SUMS` for checking downloads.

### As a .NET tool

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Download the `.nupkg` release asset into a local folder, then install from it:

```powershell
dotnet tool install --global QRBulkGen.Cli --version 1.0.0 --source (Resolve-Path .\downloads)
qr-bulk --help
```

The package is distributed with GitHub releases; publishing to NuGet.org is not
required. Alternatively, build and install directly from this repository:

```powershell
dotnet pack --configuration Release --output artifacts
dotnet tool install --global QRBulkGen.Cli --source (Resolve-Path .\artifacts)
```

## Everyday uses

### A URL, asset ID, Wi-Fi payload, or any other text

```powershell
qr-bulk --text "https://example.com/menu" --output menu.svg
qr-bulk --text "ASSET-0042" --output asset.png
qr-bulk --text "WIFI:T:WPA;S:GuestNetwork;P:example-only;;" --output wifi.png
```

Payloads are encoded as provided: no URL rewriting, column joining, trimming, or
diacritic removal. Unicode is preserved. QR codes are not encryption: anyone
who can see a code can read its payload. For sensitive payloads, use a file or
stdin rather than putting secrets in shell history.

### One code per line

```powershell
qr-bulk --input examples\links.txt --output codes
qr-bulk --input examples\links.txt --format svg --output vectors
```

Blank/whitespace-only lines are ignored; other whitespace is preserved.
Images are named `000001.png`, `000002.png`, etc. in input order, never from
payload contents. Duplicate payloads still produce separate records.
For a new directory whose name ends in an image extension, include a trailing
separator to distinguish it from a single file, e.g. `--output ".\batch.png\"`.

### CSV and TSV

```csv
id,name,url
001,Front desk,https://example.com/check-in
002,Workshop,https://example.com/workshop
```

```powershell
qr-bulk --input examples\items.csv --column url --output codes
qr-bulk --input examples\items.csv --column url --label-column name --output labels.pdf
qr-bulk --input examples\items.csv --template "https://example.com/assets/{id}" --label-template "{id} - {name}" --output assets.pdf
```

`.csv` uses commas and `.tsv` uses tabs. Override with `--delimiter ";"` or
`--delimiter "\t"`. Quoted delimiters, escaped quotes, and multiline fields work.
Headers are case-sensitive and must be unique and non-empty. A single-column CSV
needs no `--column`; with multiple columns, choose `--column` or `--template`.
Templates insert field values literally; use `{{` and `}}` for literal braces.

For files **without a header**, use `--no-header` and 1-based column numbers:

```powershell
qr-bulk --input data.csv --delimiter ";" --no-header --column 2 --output codes
qr-bulk --input data.csv --delimiter ";" --no-header --template "{2}; {3}; {4}" --output labels.pdf
```

Files/stdin default to strict UTF-8 (with or without a UTF-8 BOM). Other encodings
must be explicit, e.g. `--encoding windows-1250`. Invalid text, malformed CSV,
missing columns, and empty selected payloads fail instead of silently losing data.

### A whole file as one code

Use `--input-format text` for a vCard, calendar event, multiline text, or a payload
that contains newlines. The entire file is preserved, including its final newline:

```powershell
qr-bulk --input examples\contact.vcf --input-format text --output contact.png
```

### Pipelines

```powershell
Get-Content examples\links.txt | qr-bulk --input - --output codes
Get-Content examples\items.csv | qr-bulk --input - --input-format csv --column url --output labels.pdf
"https://example.com" | qr-bulk --input - --format svg --output - --quiet
```

`--input -` reads one payload per line unless another input format is specified.
`--output -` requires an explicit `--format`; PNG/SVG require exactly one payload,
while PDF can contain a batch. Only document/image bytes go to stdout; progress
and errors go to stderr. Use a binary-safe shell/redirection API for PNG/PDF
stdout, such as PowerShell 7.4+ or a POSIX shell. Older PowerShell versions can
corrupt redirected binary data; use `--output file.png` instead.

## Printable labels

```powershell
qr-bulk --input examples\items.csv --column url --label-column name --output labels.pdf --page-size Letter --rows 4 --columns 2 --margin 8 --gap 3
qr-bulk --text "https://example.com" --label "Scan to visit" --output poster.pdf --rows 1 --columns 1 --font-size 18
```

Defaults are A4, 2 columns x 5 rows, 6 mm outside margins, 2 mm gaps, and
10-point captions. `--label`, `--label-column`, and `--label-template` are PDF-only.
Captions wrap; layouts that cannot fit the QR and caption legibly are rejected
rather than clipped. Codes are vector graphics with a four-module quiet zone;
the minimum printed module size is 0.25 mm.

The bundled Noto Sans font covers Latin, Greek, and Cyrillic captions, not all
scripts or emoji. QR **payloads** support UTF-8 regardless of the caption font.
PDF is a grid layout, not a manufacturer-specific adhesive-label template:
check your printer alignment and print at actual size (100%).

## Output rules and scripting

- With `--text` or whole-file text, the default is `qr.png`. Batch images default
  to the `qr-codes` directory. PDF defaults to `qr-codes.pdf`.
- `.png`, `.svg`, and `.pdf` output extensions select the format. An image file
  accepts one payload; a directory accepts a batch. `--format` must agree with a
  recognized file extension. An existing directory or trailing path separator
  explicitly selects directory output.
- Nothing is overwritten unless `--force` is supplied. Input files and existing
  symlinks are not valid overwrite targets. A batch is fully rendered into
  temporary files before any final file is replaced; each final rename is atomic
  on the same filesystem. An I/O failure during final renames can still leave a
  partially committed batch.
- `--pixels-per-module` controls PNG/SVG resolution (default 10, range 1-32).
  `--error-correction L|M|Q|H` trades capacity for resilience (default M).
- `--quiet` suppresses success summaries, not errors. No arguments shows help;
  `--version` prints the version. The tool never prompts for input.

| Exit code | Meaning |
| --- | --- |
| `0` | Success, help, or version |
| `1` | File, permission, or other I/O failure |
| `2` | Invalid arguments, input data, output conflict, QR capacity, or layout |

Generation makes no network requests and sends no telemetry. Treat input files
and generated codes as data, not source code; do not commit real customer data,
credentials, or generated private labels to this repository.

## Development and releases

Use .NET 10 LTS:

```powershell
dotnet restore --locked-mode
dotnet test --configuration Release --no-restore
dotnet run --project src\QRBulkGen.Cli -- --help
```

CI runs on Windows, Linux, and macOS. Push a `v1.0.0`-style tag matching the
project version to run the release workflow, which produces self-contained
archives for x64/ARM64, the .NET tool package, and checksums. It needs only the
repository's GitHub Actions token, not a NuGet API key.

The CLI is a standalone repository with independent history and no GUI dependency.
See [LICENSE.md](LICENSE.md), [NOTICE](NOTICE), and
[third-party notices](THIRD-PARTY-NOTICES.md).
