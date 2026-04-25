# ImportAssetToOptiDam

Solution for bulk-importing assets listed in an Excel spreadsheet into the
Optimizely CMP DAM.

## Layout

```
.
├── ImportAssetToOptiDam.sln
├── Directory.Build.props           Shared MSBuild settings
├── .editorconfig                   Formatting + analyzer severities
├── .gitignore
├── azure-pipelines.yml             Azure DevOps CI
├── .github/workflows/ci.yml        GitHub Actions CI
│
├── ImportAssetToOptiDam/           Console application
│   ├── Program.cs                  Generic Host composition root
│   ├── Configuration/              Options pattern + data annotations
│   ├── Models/                     Request / response records
│   ├── Services/                   Authentication · Dam · Excel · Import
│   ├── Infrastructure/Http/        BearerTokenHandler
│   ├── appsettings.json            NO secrets
│   └── README.md                   How to run, set secrets, configure
│
└── tests/ImportAssetToOptiDam.Tests/
    ├── TestHelpers/                Stub handlers & fakes (no mocking lib)
    ├── Models/
    └── Services/                   Parallels the production tree
```

## Quick start

```bash
# Restore, build, test
dotnet restore
dotnet build --configuration Release
dotnet test

# Set secrets for local development
cd ImportAssetToOptiDam
dotnet user-secrets init
dotnet user-secrets set "OptimizelyCmp:ClientId"     "<your-client-id>"
dotnet user-secrets set "OptimizelyCmp:ClientSecret" "<your-client-secret>"

# Drop your Excel file into ExcelSheet/ and referenced images into Images/, then:
dotnet run
```

See [`ImportAssetToOptiDam/README.md`](ImportAssetToOptiDam/README.md) for the
full walkthrough and the spreadsheet format.

## Requirements

- .NET SDK 8.0+
- An Optimizely CMP application (Client ID + Client Secret)
