# Third-party notices

Boxy is distributed under GPL-3.0-or-later (see [LICENSE.md](LICENSE.md)). It uses — and, in its container
image, redistributes — the following third-party components under their own licenses.

## Runtime dependencies (NuGet)

| Component | License |
|---|---|
| .NET / ASP.NET Core runtime and EF Core (Microsoft) | MIT |
| Microsoft.AspNetCore.DataProtection.EntityFrameworkCore | MIT |
| MetadataExtractor | Apache-2.0 |
| MailKit / MimeKit | MIT |
| AWSSDK.S3 | Apache-2.0 |
| Azure.Storage.Blobs | MIT |
| Serilog.AspNetCore, Serilog.Sinks.Seq | Apache-2.0 |
| SQLitePCLRaw (`bundle_e_sqlite3`) | Apache-2.0 (bundled SQLite is public domain) |

## Bundled tools

- **FFmpeg** — used for probing, thumbnails, and transcoding. Installed into the container image from the
  Debian package (LGPL-2.1-or-later / GPL-2.0-or-later components); invoked as a separate process, with no
  FFmpeg source included in this repository. See <https://ffmpeg.org/legal.html>.

## Vendored front-end assets (`Boxy.Web/wwwroot/lib`)

- **Bootstrap** — MIT — <https://github.com/twbs/bootstrap/blob/main/LICENSE>
- **Bootstrap Icons** — MIT — <https://github.com/twbs/icons/blob/main/LICENSE>

Full license text for each component is available at the links above.
