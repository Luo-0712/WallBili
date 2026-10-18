# Third-party notices

WallBili application code is MIT licensed; see `LICENSE`.

The Windows application package and its MCP bridge include:

| Component | Version | License / source |
|---|---|---|
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.13 | MIT, copyright .NET Foundation and Contributors; [upstream](https://github.com/dotnet/efcore), full text in `licenses/Microsoft.Data.Sqlite-LICENSE.txt` |
| SQLitePCLRaw core, provider.e_sqlite3, bundle_e_sqlite3 and lib.e_sqlite3 | 2.1.13 | NuGet packages declare Apache-2.0; [upstream](https://github.com/ericsink/SQLitePCL.raw), full text in `licenses/SQLitePCLRaw-LICENSE.txt` |
| SQLite native engine inside e_sqlite3 | Supplied by SQLitePCLRaw.lib.e_sqlite3 2.1.13 | SQLite is [public domain](https://www.sqlite.org/copyright.html); build wrapper/package follows the notice above |
| QRCoder | 1.8.1 | MIT; [upstream](https://github.com/codebude/QRCoder), full text in `licenses/QRCoder-LICENSE.txt` |
| ModelContextProtocol.AspNetCore / ModelContextProtocol / ModelContextProtocol.Core | 2.2.0 | Apache-2.0, © Model Context Protocol a Series of LF Projects, LLC; [upstream](https://github.com/modelcontextprotocol/csharp-sdk), the Apache-2.0 license text is also included in `licenses/SQLitePCLRaw-LICENSE.txt` |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT, © Microsoft Corporation; [upstream](https://github.com/dotnet/extensions), [license](https://github.com/dotnet/extensions/blob/main/LICENSE.TXT) |
| Microsoft.Extensions.Logging.Abstractions / Microsoft.Extensions.DependencyInjection.Abstractions (MCP bridge) | 10.0.10 | MIT, © .NET Foundation and Contributors; [upstream](https://github.com/dotnet/runtime), MIT terms in `licenses/Microsoft.Data.Sqlite-LICENSE.txt` |

The account and MCP credential files use Windows DPAPI (`System.Security.Cryptography.ProtectedData`, MIT, copyright .NET Foundation and Contributors), provided by the .NET Windows Desktop runtime and bundled only in self-contained packages.

License texts were obtained from the corresponding upstream repositories. Exact NuGet dependency versions and content hashes are checked in as `packages.lock.json` files. No upstream library source was modified.

Framework-dependent packages require independently installed .NET 10 Desktop Runtime and ASP.NET Core Runtime 10; they do not redistribute .NET. Self-contained packages include the selected Windows x64 .NET and ASP.NET Core runtime assemblies, whose upstream licenses and notices are published by [dotnet/runtime](https://github.com/dotnet/runtime), [dotnet/wpf](https://github.com/dotnet/wpf) and [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore).

All packages require an independently installed mpv and do not redistribute mpv or FFmpeg. mpv distribution licensing depends on its build; use the notices and source offer accompanying the chosen distributor's binaries. FFmpeg is used for developer/media verification only.

The test runner xUnit is restored only for development/tests and is not part of the Windows application package. No Biu source implementation is included.
