using System.Net;
using CupriCut.Configuration;
using CupriCut.Gui;
using CupriCut.Hosting;
using CupriCut.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CupriCut;

public static class Program
{
    public static int Main(string[] args)
    {
        // When running as a Windows Service the working directory is C:\Windows\System32, so
        // resolve config, compositions, fonts and logs relative to the exe.
        var contentRoot = GetContentRoot();
        var isService = WindowsServiceHelpers.IsWindowsService();

        // The window is the default face; -c is the headless one. Both host the MCP server, so this
        // decides whether a window opens, nothing else. A service has no desktop to open one on,
        // and a container has no display, so both imply console whatever the arguments say.
        var console = isService
            || args.Any(a => a is "-c" or "--console" or "--no-gui")
            || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

        if (!isService)
        {
            McpSharpIcon.ApplyConsoleWindowIcon();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "cupricut-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "CupriCut.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"CupriCut.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "CupriCut.Local.json"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "CUPRICUT_")
                .AddCommandLine([.. args.Where(a => a is not ("-c" or "--console" or "--no-gui"))]);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<CutOptions>(builder.Configuration.GetSection(CutOptions.SectionName));
            builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

            builder.Services.AddSingleton<CupriCutService>();
            builder.Services.AddSingleton<VideoEncoder>();
            builder.Services.AddSingleton<StudioModel>();
            builder.Services.AddSingleton<StudioController>(sp => new StudioController(
                sp.GetRequiredService<CupriCutService>(),
                sp.GetRequiredService<StudioModel>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<StudioController>()));

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var cut = app.Services.GetRequiredService<CupriCutService>();
            LogStartup(
                "CupriCut",
                $"http://{server.Host}:{server.Port}{server.Path}",
                "HTTP",
                isService ? "WindowsService" : "Console",
                contentRoot,
                $"Compositions: {(cut.CompositionRoots.Count == 0 ? "(none found)" : string.Join(", ", cut.CompositionRoots))}",
                $"Output: {cut.OutputRoot}",
                $"Video: {(cut.Options.EnableVideo ? cut.Options.FfmpegPath : "disabled")}",
                $"Frames: {(cut.Options.MaxFrames > 0 ? cut.Options.MaxFrames.ToString("N0") + " max" : "no limit")}, {cut.Options.MaxPixels:N0} pixels/frame",
                $"Face: {(console ? "console (-c)" : "studio window + server")}");

            app.UseMiddleware<McpPasswordMiddleware>();

            app.MapFavicon();
            app.MapGet("/healthz", () => new
            {
                status = "ok",
                server = "CupriCut",
                path = server.Path,
                video = cut.Options.EnableVideo,
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            if (console)
            {
                app.Run();
                return 0;
            }

            return RunWithWindow(app, contentRoot);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Start the MCP server, then give the main thread to the window.
    ///
    /// <para>That order matters: a desktop window loop owns the thread it is started on, so the
    /// server has to be running before the window takes it. When the window closes, the server is
    /// stopped and the process ends — the window IS the session in this mode.</para>
    ///
    /// <para>A window that cannot open — no display, no GL, a locked session — falls back to
    /// running headless with a message, rather than taking the server down with it. Someone who
    /// launched this to serve an agent still gets a served agent.</para>
    /// </summary>
    private static int RunWithWindow(WebApplication app, string contentRoot)
    {
        app.StartAsync().GetAwaiter().GetResult();

        var controller = app.Services.GetRequiredService<StudioController>();
        var studio = new StudioApp(app.Services.GetRequiredService<StudioModel>())
        {
            FontSources = [.. StudioFonts(app.Services.GetRequiredService<CupriCutService>())],
        };

        try
        {
            CupriFace.Shell.DesktopHost.Run(studio, document =>
            {
                controller.Attach(document);
                // The scrub bar writes straight to the model, so there is no change event to hook;
                // the controller notices the value moved and renders. Cheap, and it also picks up a
                // project an agent saved while the window was open.
                document.OnRebuilt(_ => controller.Tick());
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The studio window could not open; continuing headless. Use -c to skip the window entirely");
            Log.Information("CupriCut is serving MCP headlessly. Press Ctrl+C to stop");
            app.WaitForShutdown();
            return 0;
        }
        finally
        {
            controller.Dispose();
        }

        Log.Information("Studio window closed; stopping the server");
        app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>The faces the renderer registers, so the window's own text and a rendered frame's
    /// text come from the same files.</summary>
    private static IEnumerable<CupriFace.Resources.CupriSource> StudioFonts(CupriCutService cut)
    {
        foreach (var directory in CutOptions.DefaultFontDirectories
                     .Concat(cut.Options.FontDirectories)
                     .Select(d => Path.IsPathRooted(d) ? d : Path.Combine(cut.ContentRoot, d))
                     .Distinct()
                     .Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.ttf").Order(StringComparer.Ordinal))
                yield return CupriFace.Resources.CupriSource.File(file);
        }
    }

    private static void LogStartup(string serviceName, string endpoint, string transport, string mode, string contentRoot, params string[] details)
    {
        var startupLog = Log.ForContext("SourceContext", serviceName + ".Startup");
        startupLog.Information("{ServiceName} startup", serviceName);
        startupLog.Information("  Endpoint: {Endpoint}", endpoint);
        startupLog.Information("  Transport: {Transport}", transport);
        startupLog.Information("  Mode: {Mode}", mode);
        foreach (var detail in details)
        {
            startupLog.Information("  {Detail}", detail);
        }
        startupLog.Information("  Content root: {ContentRoot}", contentRoot);
    }

    private static string GetContentRoot() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ResolveConfigFile(string contentRoot, string fileName)
    {
        if (File.Exists(Path.Combine(contentRoot, fileName)))
        {
            return fileName;
        }

        try
        {
            var match = Directory.EnumerateFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

            return match is null ? fileName : Path.GetFileName(match);
        }
        catch (DirectoryNotFoundException)
        {
            return fileName;
        }
    }
}
