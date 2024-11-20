using Axpo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;

namespace PowerTradePosition;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        
        // Serilog
        var loggerOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{MachineName}] [{EnvironmentUserName}] [{ThreadId}] [{ThreadName}] [{CorrelationId}] [{Level}] [{SourceContext}] [{EventId}] {Message}{NewLine}{Exception}";

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Destructure.AsScalar<JObject>()
            .Destructure.AsScalar<JArray>()
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentUserName()
            .Enrich.WithThreadId()
            .Enrich.WithThreadName()
            .Enrich.WithCorrelationId();
        
        if (Convert.ToBoolean(configuration.GetSection("CustomLogging").GetSection("Console:Enabled").Value))
        {
            var level = Convert.ToInt32(configuration
                .GetSection("CustomLogging")
                .GetSection("Console:LogLevel").Value ?? "3");
            
            var consoleLevel = CalculateLogLevel(level);

            loggerConfiguration.WriteTo.Console(
                restrictedToMinimumLevel: consoleLevel,
                outputTemplate: loggerOutputTemplate);
        }
        
        if (Convert.ToBoolean(configuration.GetSection("CustomLogging").GetSection("Seq:Enabled").Value))
        {
            var url = configuration
                .GetSection("CustomLogging")
                .GetSection("Seq:ServerUrl").Value ?? "http://localhost:5341";
            
            var level = Convert.ToInt32(configuration
                .GetSection("CustomLogging")
                .GetSection("Seq:LogLevel").Value ?? "3");
            
            var seqLevel = CalculateLogLevel(level);
            
            loggerConfiguration.WriteTo.Seq(
                serverUrl: url,
                restrictedToMinimumLevel: seqLevel,
                eventBodyLimitBytes: 1048576); // (1048576=1MB), default is 262144=256KB 
        }
        
        if (Convert.ToBoolean(configuration.GetSection("CustomLogging").GetSection("File:Enabled").Value))
        {
            var pathFormat = configuration
                .GetSection("CustomLogging")
                .GetSection("File:PathFormat").Value ?? "logs/application.log";
            
            var level = Convert.ToInt32(configuration
                .GetSection("CustomLogging")
                .GetSection("File:LogLevel").Value ?? "3");
            
            var fileLevel = CalculateLogLevel(level);
            
            loggerConfiguration.WriteTo.File(
                path: pathFormat,
                outputTemplate: loggerOutputTemplate,
                restrictedToMinimumLevel: fileLevel,
                rollingInterval: RollingInterval.Day);
        }
        
        ILogger logger = loggerConfiguration.CreateLogger();

        try
        {
            logger.Information("Application starting...");
            var host = CreateHostBuilder(args, configuration, logger).Build();
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "Unexpected error starting application.");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
        
    }

    private static IHostBuilder CreateHostBuilder(string[] args, IConfiguration configuration, ILogger logger) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddHostedService<PowerTradeBackgroundService>();
                services.AddSingleton<IPowerService, PowerService>();
                services.AddSingleton(configuration);
                services.AddSingleton(logger);
            });

    private static LogEventLevel CalculateLogLevel(int level)
    {
        var consoleLevel = level switch
        {
            0 => LogEventLevel.Verbose,
            1 => LogEventLevel.Debug,
            2 => LogEventLevel.Information,
            3 => LogEventLevel.Warning,
            4 => LogEventLevel.Error,
            5 => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
        
        return consoleLevel;
    }
}