# ImportAssetToOptiDam

Console utility that bulk-imports assets listed in an Excel spreadsheet into the
Optimizely CMP DAM.

It:

- Reads each row from Excel.
- Uploads the corresponding file.
- Registers it as an asset.
- Updates metadata such as title, description, alt text, and tags.
- Applies custom DAM fields based on column headers.
- Generates an output Excel report with published DAM URLs.

## Features

- Bulk upload assets using Excel.
- Metadata-driven import for tags, attributes, and SEO fields.
- Supports custom DAM fields by matching spreadsheet column headers to DAM field names.
- Generates an output report with publish URLs.
- Does not require direct CMP UI access for the import run.

## Prerequisites

- .NET SDK 8.0 or later.
- Optimizely CMP app credentials: Client ID and Client Secret.

## Configuration

All configuration is defined in `appsettings.json`.

### Setting Credentials

For local development, replace the placeholder values in `appsettings.json`:

```json
{
  "OptimizelyCmp": {
    "BaseUrl": "https://api.cmp.optimizely.com",
    "TokenUrl": "https://accounts.cmp.optimizely.com/o/oauth2/v1/token",
    "ApiVersion": "v3",
    "ClientId": "REPLACE_WITH_CLIENT_ID",
    "ClientSecret": "REPLACE_WITH_CLIENT_SECRET",
    "TokenRefreshSkewSeconds": 60,
    "FieldsPageSize": 100
  }
}
```

For safer local development, use .NET user secrets:

```bash
cd ImportAssetToOptiDam
dotnet user-secrets init
dotnet user-secrets set "OptimizelyCmp:ClientId" "<your-client-id>"
dotnet user-secrets set "OptimizelyCmp:ClientSecret" "<your-client-secret>"
```

For CI/CD or hosted environments, use environment variables:

```bash
export OptimizelyCmp__ClientId="your-client-id"
export OptimizelyCmp__ClientSecret="your-client-secret"
```

## Usage Example

```csharp
using ImportAssetToOptiDam.Configuration;
using ImportAssetToOptiDam.Services.Authentication;
using ImportAssetToOptiDam.Services.Dam;
using ImportAssetToOptiDam.Services.Excel;
using ImportAssetToOptiDam.Services.Import;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true);

builder.Services.AddSerilog();

builder.Services
    .AddOptions<OptimizelyCmpOptions>()
    .Bind(builder.Configuration.GetSection("OptimizelyCmp"))
    .ValidateOnStart();

builder.Services.AddSingleton<ITokenProvider, CachingTokenProvider>();
builder.Services.AddSingleton<IAssetImportReader, OpenXmlAssetImportReader>();
builder.Services.AddSingleton<IUploadReportWriter, XlsxUploadReportWriter>();
builder.Services.AddSingleton<AssetImporter>();

builder.Services.AddHostedService<ImportHostedService>();

await builder.Build().RunAsync();
```

## Sample appsettings.json

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information"
    }
  },
  "OptimizelyCmp": {
    "BaseUrl": "https://api.cmp.optimizely.com",
    "TokenUrl": "https://accounts.cmp.optimizely.com/o/oauth2/v1/token",
    "ApiVersion": "v3",
    "TokenRefreshSkewSeconds": 60,
    "FieldsPageSize": 100,
    "ClientId": "REPLACE_WITH_CLIENT_ID",
    "ClientSecret": "REPLACE_WITH_CLIENT_SECRET"
  },
  "Import": {
    "ImportFileName": "ImportAssetToOptiDam.xlsx",
    "ImportFolder": "ExcelSheet",
    "ImagesFolder": "Images",
    "SheetIndex": 0,
    "MaxDegreeOfParallelism": 1,
    "OutputFolder": "Output",
    "OutputFileName": "UploadReport-{date:yyyyMMdd}.xlsx"
  }
}
```

## Excel Template

The import workbook is located at `ExcelSheet/ImportAssetToOptiDam.xlsx`.

The first worksheet is read by default. The first row must contain headers.
Recognized headers are case-insensitive and can include spaces:

| Header | Purpose |
| --- | --- |
| `Source Folder Path` | Optional source reference path. |
| `OldFileName` | File name under the configured `Images` folder. Required. |
| `NewFileName` | Title to apply to the DAM asset. |
| `DAMFolderGuid` | DAM folder GUID. Used before `DAM Folder Path` when populated. |
| `DAM Folder Path` | DAM folder path, for example `Assets/TestFolderForDAMAutomation/VWTest`. |
| `Description` | Asset description. |
| `AltText` | Image alt text. |
| `Tags` | Comma-separated tags, for example `hero,web,banner`. |

Any additional header is treated as a custom DAM field. If an active DAM field
with the same name exists, the cell value is applied to that field. For choice
fields, provide comma-separated choice names. Use `ALL` to select every choice.

## Running

```bash
dotnet run --project ImportAssetToOptiDam
```

The app reads:

- `ExcelSheet/ImportAssetToOptiDam.xlsx`
- files from the configured `Images` folder
- CMP credentials from configuration, user secrets, or environment variables

## Output

After execution, the tool generates an Excel report in the configured
`Output` folder.

The report contains:

- New file name.
- Public DAM URL.
- Private DAM URL, when applicable.

## Important Note

This tool is currently beta.

- It is not production hardened.
- Test in lower environments such as DEV or UAT before production use.
- Keep CMP credentials out of source control and rotate any secret that has
  already been committed.
