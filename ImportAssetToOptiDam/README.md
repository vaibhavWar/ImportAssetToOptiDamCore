# ImportAssetToOptiDam

Console utility that bulk-imports assets listed in an Excel spreadsheet into the
Optimizely CMP DAM. Reads each row, uploads the file, registers it as an asset,
patches its metadata (title / description / alt text / tags) and sets any
custom DAM fields that match the spreadsheet headers.

## Prerequisites

- .NET SDK **8.0** or later
- An Optimizely CMP application with a Client ID and Client Secret

## Configuration

All configuration lives in `appsettings.json`, including the `OptimizelyCmp:ClientId`
and `OptimizelyCmp:ClientSecret` fields. **Please read the security note below
before relying on this for anything beyond local development.**

### Setting the credentials (primary path)

Open `ImportAssetToOptiDam/appsettings.json` and replace the placeholder values:

```jsonc
"OptimizelyCmp": {
  "BaseUrl":      "https://api.cmp.optimizely.com",
  "TokenUrl":     "https://accounts.cmp.optimizely.com/o/oauth2/v1/token",
  "ApiVersion":   "v3",
  "ClientId":     "REPLACE_WITH_CLIENT_ID",     // ← your value
  "ClientSecret": "REPLACE_WITH_CLIENT_SECRET", // ← your value
  "TokenRefreshSkewSeconds": 60,
  "FieldsPageSize": 100
}
```

The app refuses to start while either field still contains the placeholder
string, is empty, or has leading/trailing whitespace — you will see a clear
error at startup rather than a later `401 Unauthorized`.

### Security note — important

Storing credentials in `appsettings.json` puts them in plain text wherever the
file lives: your source-control repository, build artifacts, CI logs, deployed
copies on disk, machine backups. **Treat any secret that is ever committed to
source control as compromised and rotate it.** For anything production-adjacent,
prefer one of the override paths below.

### Overrides (take precedence over appsettings.json)

Values set here override the values in `appsettings.json` without touching the file.

**User Secrets** (local development — stored outside the repo in your user profile):

```bash
cd ImportAssetToOptiDam
dotnet user-secrets init
dotnet user-secrets set "OptimizelyCmp:ClientId"     "<your-client-id>"
dotnet user-secrets set "OptimizelyCmp:ClientSecret" "<your-client-secret>"
```

**Environment variables** (CI/CD and servers — the double underscore is the
.NET convention for nested config keys):

```bash
export OptimizelyCmp__ClientId="..."
export OptimizelyCmp__ClientSecret="..."
```

In production, pull from a secret store (Azure Key Vault, AWS Secrets Manager,
HashiCorp Vault) and inject as environment variables.

### Verifying the credentials are loaded

On startup the application logs, for each credential key, **which provider
supplied it** and whether the value is present — but never the value itself.
Look for lines like:

```
Config source: OptimizelyCmp:ClientId = set (len=36) (from JsonConfigurationProvider for 'appsettings.json')
Config source: OptimizelyCmp:ClientSecret = set (len=40) (from EnvironmentVariablesConfigurationProvider)
```

If a value is `missing`, `empty`, or `has whitespace`, the startup validator
will refuse to boot the app and tell you exactly which key is the problem.

### Troubleshooting: "401 Unauthorized" after startup

The token endpoint rejected the credentials. Check, in order:

1. **Is `OptimizelyCmp:TokenUrl` pointing at the right environment?** The
   exception message includes the exact URL that was called — compare against
   the one in Optimizely's admin portal.
2. **Has the secret been rotated?** CMP will stop accepting the old value
   after a rotation even if everything else is unchanged.
3. **Did an env-var override sneak in?** Check `env | grep OptimizelyCmp`;
   an override outranks `appsettings.json`.

## Folder layout at runtime

```
<output-dir>/
├── ImportAssetToOptiDam.exe (or `dotnet ImportAssetToOptiDam.dll`)
├── appsettings.json
├── ExcelSheet/
│   └── ImportAssetToOptiDam.xlsx    ← configurable via Import:ImportFileName
├── Images/
│   └── <files referenced by the OldFileName column>
├── Output/
│   └── UploadReport-YYYYMMDD.xlsx   ← post-import report (see below)
└── Logs/
    └── import-YYYYMMDD.log          ← Serilog rolling file sink
```

## Upload report

After every run that produces at least one successful upload, the importer writes
`Output/UploadReport-YYYYMMDD.xlsx` containing one row per uploaded asset:

| New FileName | Public DAM URL | Private DAM URL |
| --- | --- | --- |

The CMP API returns a single canonical `url` field per asset. Per the docs:

> *"This is the URL to access the asset. If the asset is private, the URL
> includes a token that expires after a short time."*

So there is one URL per asset, and it lands in either the public column or the
private column based on the asset's `is_public` setting:

- This importer sets `is_public: true` when patching metadata, so under normal
  operation the **Public DAM URL** column is populated and **Private DAM URL**
  is blank.
- If a future change keeps assets private (`is_public: false`), the same field
  will populate the **Private DAM URL** column instead.

The output filename supports a single `{date:format}` placeholder that's
expanded against today's local date:

| Configured `OutputFileName`            | Resolved at runtime               |
| -------------------------------------- | --------------------------------- |
| `UploadReport-{date:yyyyMMdd}.xlsx`    | `UploadReport-20260425.xlsx`      |
| `Report.xlsx`                          | `Report.xlsx` (no substitution)   |

If a file with the resolved name already exists, the writer appends ` (2)`,
` (3)`, … before the extension so consecutive runs do not overwrite earlier
reports.

## Spreadsheet format

The first row is the header row. Recognised header names (case- and
whitespace-insensitive):

| Header          | Purpose                                                 |
| --------------- | ------------------------------------------------------- |
| `SourceLink`    | Originating URL — informational                         |
| `OldFileName`   | File name on disk under `Images/` — **required**        |
| `NewFileName`   | Title given to the asset in DAM                         |
| `ParentFolder`  | DAM folder GUID, checked shallowest → deepest           |
| `Subfolder`     | Optional sub-folder GUID                                |
| `Subfolder2`    | Optional                                                |
| `Subfolder3`    | Optional — the deepest valid GUID wins                  |
| `Description`   | Patched onto the asset                                  |
| `AltText`       | Patched onto the asset (alt text for images)            |

**Any other header** is matched by name against the DAM's configured custom
fields. If a matching field exists and is active, the cell value is applied;
for choice fields, a comma-separated list of choice names is supported, and the
literal value `ALL` expands to every choice.

## Running

```bash
dotnet run --project ImportAssetToOptiDam
```

The process runs the import once and exits with:

- `0` — success
- `1` — one or more rows failed (check the log)
- `2` — cancelled before completion

## What changed vs the original

- Target framework upgraded from `.NET Framework 4.8.1` (legacy csproj) to
  `.NET 8` (SDK-style).
- OAuth token is cached per its `expires_in` and refreshed under a lock
  instead of held forever.
- All HTTP goes through `IHttpClientFactory` typed clients with
  `Microsoft.Extensions.Http.Resilience` (retries, circuit breaker, timeouts);
  no more `new HttpClient()` per request and no more `.Result`/`.Wait()`.
- JSON payloads are built from strongly-typed records with `System.Text.Json`,
  eliminating the `StringBuilder`-based hand-rolled JSON and the escaping bugs
  that came with it.
- `DAMTokenBase` inheritance replaced with a single injectable
  `IOptimizelyDamClient` (composition over inheritance).
- Secrets removed from `appsettings.json`; resolved via User Secrets /
  environment variables at startup with `ValidateDataAnnotations().ValidateOnStart()`.
- `ExcelFileReader.ReadExcelFile` split into an `IAssetImportReader` (pure parse)
  and an `AssetImporter` (orchestrator). Rows are yielded as strongly-typed
  `AssetImportRow` values; column meaning comes from headers, not positional
  magic numbers.
- Hard-coded fallback folder GUID removed — the importer now fails fast if no
  folder can be resolved and no `DefaultFolderId` is configured.
- `Console.ReadKey()` removed; the tool runs unattended under a `BackgroundService`
  so it works under CI/CD, Docker, and Task Scheduler.
- `CancellationToken` threaded through every async call path; Ctrl+C triggers a
  clean shutdown.
- Structured Serilog logging with per-row scopes, rolling daily files, and
  configuration-driven sinks.
- `EPPlus` dependency removed (dual-licensed, and not needed for read-only parsing).
