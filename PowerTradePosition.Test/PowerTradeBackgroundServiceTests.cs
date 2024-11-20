using Axpo;
using Microsoft.Extensions.Configuration;
using Moq;
using Serilog;
using Xunit.Sdk;

namespace PowerTradePosition.Text;

public class PowerTradeBackgroundServiceTests
{
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger> _mockLogger;
    private readonly PowerTradeBackgroundService _service;

    public PowerTradeBackgroundServiceTests()
    {
        // Mock the configuration
        _mockConfiguration = new Mock<IConfiguration>();
        var sectionMock = new Mock<IConfigurationSection>();
        sectionMock.Setup(s => s.Value).Returns("1");
        _mockConfiguration.Setup(config => config.GetSection("PowerTradeConfiguration:IntervalMinutes")).Returns(sectionMock.Object);

        sectionMock = new Mock<IConfigurationSection>();
        sectionMock.Setup(s => s.Value).Returns("./");
        _mockConfiguration.Setup(config => config.GetSection("PowerTradeConfiguration:OutputFolderPath")).Returns(sectionMock.Object);

        sectionMock = new Mock<IConfigurationSection>();
        sectionMock.Setup(s => s.Value).Returns("Europe/Madrid");
        _mockConfiguration.Setup(config => config.GetSection("PowerTradeConfiguration:TimeZone")).Returns(sectionMock.Object);

        // Mock the logger
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(logger => logger.ForContext<PowerTradeBackgroundService>()).Returns(_mockLogger.Object);

        // Create an instance of PowerService
        IPowerService powerService = new PowerService();

        // Create an instance of the service to be tested
        _service = new PowerTradeBackgroundService(_mockConfiguration.Object, _mockLogger.Object, powerService);
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunExtract()
    {
        // Arrange
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        // Act
        var executeTask = _service.StartAsync(cancellationToken);
        await Task.Delay(5000, cancellationToken); // Wait for some time to ensure the background task is started


        // Assert
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Running first extract"))), Times.Once);
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Run extract"))), Times.Once);
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Aggregate positions per hour"))), Times.Once);
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Extract generated successfully:"))), Times.Once);
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Writing CSV"))), Times.Once);
        
        //Clean
        Clean();
    }


    [Fact]
    public async Task ExecuteAsync_Should_RunExtract_OnFailure()
    {
        // Arrange
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        // Injecting a faulty PowerService by inheriting and overriding the method
        var faultyPowerService = new FaultyPowerService();
        var serviceWithFaultyPowerService = new PowerTradeBackgroundService(_mockConfiguration.Object, _mockLogger.Object, faultyPowerService);

        // Act
        var executeTask = serviceWithFaultyPowerService.StartAsync(cancellationToken);
        await Task.Delay(1000, cancellationToken); // Wait for some time to ensure the background task is started
        await cancellationTokenSource.CancelAsync(); // Cancel the task

        // Assert
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Running first extract"))), Times.Once);
        _mockLogger.Verify(logger => logger.Information(It.Is<string>(s => s.Contains("Run extract"))), Times.Once);
        _mockLogger.Verify(logger => logger.Error(It.Is<Exception>(ex => ex.Message.Contains("Test exception")), It.Is<string>(s => s.Contains("Error while running extract"))), Times.AtLeastOnce);
    }

    public static void Clean()
    {
        if (!Directory.Exists("./")) return;
        var files = Directory.GetFiles("./", "*" + "csv");
        foreach (var file in files)
        {
            File.Delete(file);
        }
    }
}



public class FaultyPowerService : IPowerService
{
    IEnumerable<PowerTrade> IPowerService.GetTrades(DateTime date)
    {
        throw new NotImplementedException();
    }

    Task<IEnumerable<PowerTrade>> IPowerService.GetTradesAsync(DateTime date)
    {
        throw new Exception("Test exception");
    }
}