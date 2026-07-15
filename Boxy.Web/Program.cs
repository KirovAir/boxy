using System.Security.Claims;
using Boxy.Data;
using Boxy.Web.Extensions;
using Boxy.Web.Logging;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureLogging();

await builder.RunWithLoggingAsync(async b =>
{
    // ── Data ──────────────────────────────────────────────────────────
    b.Services.AddDbContext<AppDbContext>();

    // ── Auth ──────────────────────────────────────────────────────────
    b.Services.AddSingleton<UserService>();
    b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/login";
            o.LogoutPath = "/logout";
            o.AccessDeniedPath = "/login";
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Events.OnValidatePrincipal = async ctx =>
            {
                // A signed-in principal must map to an active account. This rejects pre-multi-user
                // cookies (no user-id claim) and ends sessions for deleted or disabled users on their
                // next request. It also refreshes the cookie when the account's role changed since
                // sign-in (e.g. promoted to admin), so the admin area appears without a manual
                // re-login. Only fires when an auth cookie is present, so anonymous traffic is unaffected.
                var id = ctx.Principal?.GetUserId() ?? 0;
                Boxy.Data.Entities.User? user = null;
                if (id != 0)
                {
                    var factory = ctx.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
                    await using var db = await factory.CreateDbContextAsync();
                    user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
                }

                if (user is null || !user.IsActive)
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                if (ctx.Principal!.FindFirstValue(ClaimTypes.Role) != user.Role.ToString())
                {
                    ctx.ReplacePrincipal(UserService.CreatePrincipal(user));
                    ctx.ShouldRenew = true;
                }
            };
        });
    b.Services.AddAuthorization();

    // Persist Data Protection keys in the DB so the admin login cookie survives container
    // recreates and travels with the backed-up database.
    b.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>();

    // ── Uploads: keep them fast on high-latency links ─────────────────
    // HTTP/2 flow-control windows default to ~128KB, so upload throughput is pinned near window/RTT
    // (about 1 MB/s on a fast link with real latency) no matter the chunk size or parallelism - every
    // HTTP/2 stream shares the one connection window. Raise the windows so a big chunked upload can
    // fill a high-bandwidth pipe. (Only bites when the client reaches Kestrel over HTTP/2; a buffering
    // reverse proxy in front governs its own client-side window.)
    b.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.Http2.InitialConnectionWindowSize = 16 * 1024 * 1024; // 16 MB in flight per connection
        o.Limits.Http2.InitialStreamWindowSize = 8 * 1024 * 1024; // 8 MB per stream
    });

    // ── MVC ───────────────────────────────────────────────────────────
    b.Services.AddControllersWithViews();

    // Allow large uploads: the per-action [DisableRequestSizeLimit] lifts Kestrel's cap;
    // this lifts the multipart form limit to match.
    b.Services.Configure<FormOptions>(o =>
    {
        o.MultipartBodyLengthLimit = long.MaxValue;
        o.ValueLengthLimit = int.MaxValue;
    });

    // ── Media services ────────────────────────────────────────────────
    b.Services.AddSingleton<ShareUnlock>();
    // Blob storage: provider-selected backend for finished content. Working files always stay local.
    var storageSettings = b.Configuration.GetSection(StorageSettings.SectionName).Get<StorageSettings>() ?? new StorageSettings();
    b.Services.AddSingleton(storageSettings);
    switch (storageSettings.Provider.Trim().ToLowerInvariant())
    {
        case "s3":
            b.Services.AddSingleton(storageSettings.S3);
            b.Services.AddSingleton<IBlobStore, S3BlobStore>();
            break;
        case "azure":
            b.Services.AddSingleton(storageSettings.Azure);
            b.Services.AddSingleton<IBlobStore, AzureBlobStore>();
            break;
        default:
            b.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();
            break;
    }

    // FFmpeg: binary paths and timeouts are deployment-only (bound once from the "Ffmpeg" section - an
    // HTTP-editable executable path would be an RCE primitive). The video-quality knobs are resolved at
    // transcode time (admin edits in-app, else the same section), so changes apply without a restart.
    b.Services.Configure<FfmpegSettings>(b.Configuration.GetSection(FfmpegSettings.SectionName));
    b.Services.AddSingleton<VideoSettingsProvider>();
    b.Services.AddSingleton<FfmpegCapabilities>();
    b.Services.AddSingleton<MediaProcessor>();
    b.Services.AddSingleton<FileMetadataExtractor>();
    b.Services.AddSingleton<MediaProcessingQueue>();
    // Live conversion progress the status poll reads: in-memory, written by the worker as it encodes.
    b.Services.AddSingleton<ConversionProgress>();
    // Webhook client: short timeout and never auto-follow redirects, so a redirect can't send the POST
    // to an address the SSRF check already validated away from.
    b.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    b.Services.AddSingleton<QuotaService>();
    b.Services.AddScoped<IngestionService>();
    b.Services.AddScoped<ChunkedUploadService>();
    // Singleton: it holds the in-flight assemblies, which outlive the requests that started them.
    b.Services.AddSingleton<UploadFinalizer>();
    b.Services.AddScoped<PartialRenderer>();

    // Email: settings resolve at send-time (admin edits in-app, else the "Email" config section), so
    // changes apply without a restart. Password is encrypted at rest via Data Protection.
    b.Services.AddSingleton<EmailSettingsProvider>();
    b.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
    b.Services.AddSingleton<EmailRenderer>();
    b.Services.AddSingleton<EmailComposer>();

    b.Services.AddHostedService<MediaProcessingWorker>();
    b.Services.AddHostedService<TempCleanupService>();
    b.Services.AddHostedService<RetentionSweepService>();
    b.Services.AddHostedService<NotificationWorker>();

    var app = b.Build();

    // ── DB init ───────────────────────────────────────────────────────
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Initialize(logger);

        // Seed the initial admin from config on first run and adopt any pre-multi-user content.
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        await users.SeedAsync(config);
        // Abandoned chunked-upload parts are swept by TempCleanupService (on start + periodically).

        // Create the bucket/container up front (and fail fast on a misconfigured remote backend).
        if (scope.ServiceProvider.GetRequiredService<IBlobStore>() is RemoteBlobStoreBase remoteStore)
        {
            await remoteStore.EnsureReadyAsync();
        }

        // Ask ffmpeg what this machine can actually do, once, before the worker takes its first item: can
        // it tone-map HDR, and can it really encode on the GPU. Both are properties of the deployment, and
        // finding out per-video would mean finding out the hard way, one failed transcode at a time.
        await scope.ServiceProvider.GetRequiredService<FfmpegCapabilities>().DetectAsync();
    }

    // ── Middleware ────────────────────────────────────────────────────
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/error");
    }

    // Render a friendly page (not a blank screen) for 404s etc. - e.g. a missing upload box or video.
    app.UseStatusCodePagesWithReExecute("/status/{0}");

    // Never let HTML be served stale (browser or the Cloudflare tunnel): always revalidate, so a
    // deploy's new markup - and its fresh asp-append-version asset URLs - are picked up immediately.
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var response = context.Response;
            if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                && string.IsNullOrEmpty(response.Headers.CacheControl))
            {
                response.Headers.CacheControl = "no-cache, must-revalidate";
            }

            return Task.CompletedTask;
        });

        await next();
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            // asp-append-version stamps a content hash as ?v=… on every asset URL, so a versioned
            // request can be cached forever - the URL changes whenever the file does. Unversioned
            // hits (rare) must revalidate.
            ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Query.ContainsKey("v")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
        }
    });
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
});
