
using System.Globalization;
using Axpo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Polly;
using ILogger = Serilog.ILogger;


namespace PowerTradePosition;
public class PowerTradeBackgroundService(IConfiguration configuration, ILogger logger, IPowerService powerService) : BackgroundService
{
    private readonly ILogger _logger = logger.ForContext<PowerTradeBackgroundService>();

    private readonly int _interval = Convert.ToInt32(configuration
        .GetSection("PowerTradeConfiguration:IntervalMinutes").Value);

    private readonly string _folderPath = configuration
        .GetSection("PowerTradeConfiguration:OutputFolderPath").Value ?? CreateOutputFolder();

    private readonly string _timezone = configuration
        .GetSection("PowerTradeConfiguration:TimeZone").Value ?? "Europe/Madrid";


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run first extract immediately
        _logger.Information("Running first extract");
        await RunExtractWithRetry(stoppingToken);

        // Schedule subsequent extracts
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(_interval), stoppingToken);
            _logger.Information("Running next extract");
            await RunExtractWithRetry(stoppingToken);
        }
    }

    private static string CreateOutputFolder()
    {
        var rootPath = AppDomain.CurrentDomain.BaseDirectory;
        var folderName = "PowerTradeReports";
        var folderPath = Path.Combine(rootPath, folderName);
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        return folderPath;
    }

    private async Task RunExtractWithRetry(CancellationToken stoppingToken)
    {
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(5), (exception, timeSpan, retryCount, context) =>
            {
                _logger.Warning($"Attempt {retryCount} failed: {exception.Message}");
            });

        await retryPolicy.ExecuteAsync(async () =>
        {
            await RunExtract(stoppingToken);
        });
    }

    private async Task RunExtract(CancellationToken stoppingToken)
    {
        try
        {
            _logger.Information("Run extract");
            var referenceDate = DateTime.UtcNow.Date.AddDays(1); // Fetch trades for the next day
            var trades = await powerService.GetTradesAsync(referenceDate);

            // Aggregate positions per hour
            var aggregatedPositions = AggregateTrades(trades);

            // Write to CSV
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
            var filename = $"PowerPosition_{referenceDate:yyyyMMdd}_{timestamp}.csv";
            var filePath = Path.Combine(_folderPath, filename);
            WriteToCsv(filePath, aggregatedPositions);
            _logger.Information($"Extract generated successfully: {filePath}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error while running extract: {ex.Message}");
            throw; // Rethrow the exception to be handled by the retry mechanism
        }
    }

    private List<(DateTime DateTime, double Volume)> AggregateTrades(IEnumerable<PowerTrade> trades)
    {
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(_timezone);
        _logger.Information("Aggregate positions per hour");
        return trades
            .SelectMany(powerTrade =>
                powerTrade.Periods.Select(powerPeriod => new
                {
                    DateTime = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(powerTrade.Date.Date.AddHours(powerPeriod.Period - 1), DateTimeKind.Unspecified), timeZoneInfo),
                    powerPeriod.Volume
                }))
            .GroupBy(x => x.DateTime)
            .OrderBy(g => g.Key) // Ensure the data is ordered by time
            .Select(g => (g.Key, g.Sum(x => x.Volume)))
            .ToList();
    }


    private void WriteToCsv(string filePath, List<(DateTime DateTime, double Volume)> aggregatedPositions)
    {
        _logger.Information("Writing CSV");
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("Datetime;Volume");
        foreach (var position in aggregatedPositions)
        {
            writer.WriteLine($"{position.DateTime:yyyy-MM-ddTHH:mm:ssZ};{position.Volume.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
