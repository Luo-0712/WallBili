# Third-party notices

WallBili application code is MIT licensed; see `LICENSE`.

The Windows framework-dependent debug package includes:

| Component | Version | License / source |
|---|---|---|
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.13 | MIT, copyright .NET Foundation and Contributors; [upstream](https://github.com/dotnet/efcore), full text in `licenses/Microsoft.Data.Sqlite-LICENSE.txt` |
| SQLitePCLRaw core, provider.e_sqlite3, bundle_e_sqlite3 and lib.e_sqlite3 | 2.1.13 | NuGet packages declare Apache-2.0; [upstream](https://github.com/ericsink/SQLitePCL.raw), full text in `licenses/SQLitePCLRaw-LICENSE.txt` |
| SQLite native engine inside e_sqlite3 | Supplied by SQLitePCLRaw.lib.e_sqlite3 2.1.13 | SQLite is [public domain](https://www.sqlite.org/copyright.html); build wrapper/package follows the notice above |

License texts were obtained from the corresponding upstream repositories. Exact NuGet dependency versions and content hashes are checked in as `packages.lock.json` files. No upstream library source was modified.

The package requires an independently installed .NET 10 Desktop Runtime and mpv. It does not redistribute .NET, mpv or FFmpeg. mpv distribution licensing depends on its build; use the notices and source offer accompanying the chosen distributor's binaries. FFmpeg is used for developer/media verification only.

The test runner xUnit is restored only for development/tests and is not part of the Windows application package. No Biu source implementation is included.
