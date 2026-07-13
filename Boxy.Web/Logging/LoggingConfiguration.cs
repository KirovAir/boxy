using System.Reflection;
using Serilog;
using Serilog.Events;

namespace Boxy.Web.Logging;

public static class LoggingConfiguration
{
    public static WebApplicationBuilder ConfigureLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Boxy";
        var environment = builder.Environment.EnvironmentName;

        var logConfig = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.WithProperty("Application", appName)
            .Enrich.WithProperty("Environment", environment)
            .WriteTo.Console();

        var seqUrl = builder.Configuration["Seq:ServerUrl"];
        var seqApiKey = builder.Configuration["Seq:ApiKey"];
        if (!string.IsNullOrEmpty(seqUrl))
        {
            logConfig = logConfig.WriteTo.Seq(seqUrl, apiKey: seqApiKey);
        }

        Log.Logger = logConfig.CreateLogger();
        builder.Logging.AddSerilog(Log.Logger, true);

        Log.Information("Initialized logger for {Application} ({Environment})", appName, environment);

        return builder;
    }

    public static async Task RunWithLoggingAsync(this WebApplicationBuilder builder,
        Func<WebApplicationBuilder, Task> configure)
    {
        try
        {
            await configure(builder);
        }
        catch (Exception ex) when (ex is not HostAbortedException)
        {
            Log.Fatal(ex, "Application startup failed");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
