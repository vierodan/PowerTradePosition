
using System.Globalization;
using Axpo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Polly;


namespace PowerTradePosition;
public class PowerTradeBackgroundService(IConfiguration configuration, PowerService powerService) : BackgroundService
{
    private readonly int _interval = Convert.ToInt32(configuration
        .GetSection("PowerTradeConfiguration")
        .GetSection("IntervalMinutes").Value ?? "15");
    
    private readonly string _folderPath = configuration
        .GetSection("PowerTradeConfiguration")
        .GetSection("OutputFolderPath").Value ?? "~/avr/PowerTradeReports";
    
    private readonly string _timezone = configuration
        .GetSection("PowerTradeConfiguration")
        .GetSection("TimeZone").Value ?? "Europe/Madrid";

    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run first extract immediately
        await RunExtractWithRetry(_folderPath, _timezone, stoppingToken);

        // Schedule subsequent extracts
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(_interval), stoppingToken);
            await RunExtractWithRetry(_folderPath, _timezone, stoppingToken);
        }
    }

    private async Task RunExtractWithRetry(string folderPath, string timezone, CancellationToken stoppingToken)
    {
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(5), (exception, timeSpan, retryCount, context) =>
            {
                Console.WriteLine($"Attempt {retryCount} failed: {exception.Message}");
            });

        await retryPolicy.ExecuteAsync(async () =>
        {
            await RunExtract(folderPath, timezone, stoppingToken);
        });
    }

    private async Task RunExtract(string folderPath, string timezone, CancellationToken stoppingToken)
    {
        try
        {
            var referenceDate = DateTime.UtcNow.Date.AddDays(1); // Fetch trades for the next day
            var trades = await powerService.GetTradesAsync(referenceDate);

            // Aggregate positions per hour
            var aggregatedPositions = AggregateTrades(trades, timezone);

            // Write to CSV
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
            var filename = $"PowerPosition_{referenceDate:yyyyMMdd}_{timestamp}.csv";
            var filePath = Path.Combine(folderPath, filename);
            WriteToCsv(filePath, aggregatedPositions);

            Console.WriteLine($"Extract generated successfully: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while running extract: {ex.Message}");
            throw; // Rethrow the exception to be handled by the retry mechanism
        }
    }

    private List<(DateTime DateTime, double Volume)> AggregateTrades(IEnumerable<PowerTrade> trades, string timezone)
    {
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);

        return trades
            .SelectMany(t => t.Periods.Select(p => new
            {
                DateTime = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(t.Date.Date.AddHours(p.Period - 1), DateTimeKind.Unspecified), 
                    timeZoneInfo),
                p.Volume
            }))
            .GroupBy(x => x.DateTime)
            .OrderBy(g => g.Key) // Ensure the data is ordered by time
            .Select(g => (g.Key, g.Sum(x => x.Volume)))
            .ToList();
    }


    private void WriteToCsv(string filePath, List<(DateTime DateTime, double Volume)> aggregatedPositions)
    {
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("Datetime;Volume");
        foreach (var position in aggregatedPositions)
        {
            writer.WriteLine($"{position.DateTime:yyyy-MM-ddTHH:mm:ssZ};{position.Volume.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
