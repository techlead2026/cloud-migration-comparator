using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Initialize and Inject Redis into Dependency Injection Container
// Falls back to localhost if running outside Docker environment
var redisConnectionString = Environment.GetEnvironmentVariable("RedisConnection") ?? "localhost:6379";
var redisMultiplexer = ConnectionMultiplexer.Connect(redisConnectionString);
builder.Services.AddSingleton<IConnectionMultiplexer>(redisMultiplexer);

// Register HttpClient factory
builder.Services.AddHttpClient();

var app = builder.Build();

// Metadata welcome route
app.MapGet("/", () => Results.Ok(new { engine = "Cloud Migration Comparator Engine", version = "1.0.0", activeModule = "Object Storage with Distributed Caching (Phase 1)" }));

// =========================================================================
// MODULE 1: OBJECT STORAGE RESILIENCY INTERFACE (With Redis Caching)
// =========================================================================
app.MapPost("/api/v1/compare/storage", async (
    [FromBody] StorageWorkloadRequest request,
    HttpClient httpClient,
    IConnectionMultiplexer redisMux) =>
{
    if (request.DataSizeGb <= 0)
    {
        return Results.BadRequest("Data storage specification footprint must be greater than 0 GB.");
    }

    // Connect to the Redis database instance
    var cache = redisMux.GetDatabase();
    string cacheKey = "pricing:azure:blob:hot-lrs";

    // 2. AWS S3 Standard Cost Models (Baseline Estimates)
    double awsS3StorageRatePerGb = 0.023;
    double awsS3WriteRatePer10k = 0.05;
    double awsS3ReadRatePer10k = 0.004;

    double awsStorageCost = request.DataSizeGb * awsS3StorageRatePerGb;
    double awsWriteCost = (request.WriteOpsCount / 10000.0) * awsS3WriteRatePer10k;
    double awsReadCost = (request.ReadOpsCount / 10000.0) * awsS3ReadRatePer10k;
    double totalAwsMonthlyCost = awsStorageCost + awsWriteCost + awsReadCost;

    // 3. Azure Blob Pricing Execution (Cache-Aside Pattern)
    AzureRates azureRates;

    // STEP A: Query Local Redis Memory Cache
    string? cachedRatesJson = await cache.StringGetAsync(cacheKey);

    if (!string.IsNullOrEmpty(cachedRatesJson))
    {
        // Cache Hit: Instantly parse the serialized memory object
        azureRates = JsonSerializer.Deserialize<AzureRates>(cachedRatesJson)!;
        Console.WriteLine("🚀 [Cache Hit] Pulling storage pricing data straight out of Redis memory!");
    }
    else
    {
        // Cache Miss: Fallback to the slow, live public internet API call
        Console.WriteLine("⚠️ [Cache Miss] Redis memory is empty. Fetching live rates from Microsoft API...");

        // Default backup fallback rates if external network drops out entirely
        azureRates = new AzureRates(0.023, 0.054, 0.004);

        try
        {
            string azureApiUrl = "https://azure.com eq 'Storage' and armRegionName eq 'eastus' and productName eq 'Blob Storage' and skuName eq 'Hot LRS' and priceType eq 'Consumption'";
            var apiResponse = await httpClient.GetFromJsonAsync<AzureRetailPriceResponse>(azureApiUrl);

            if (apiResponse?.Items != null)
            {
                var capacityMeter = apiResponse.Items.FirstOrDefault(x => x.MeterName.Contains("Data Stored") || x.MeterName.Contains("Capacity"));
                var writeMeter = apiResponse.Items.FirstOrDefault(x => x.MeterName.Contains("Write Operations") || x.MeterName.Contains("List and Create"));
                var readMeter = apiResponse.Items.FirstOrDefault(x => x.MeterName.Contains("Read Operations"));

                double parsedStorage = capacityMeter != null ? capacityMeter.RetailPrice : 0.023;
                double parsedWrite = writeMeter != null ? writeMeter.RetailPrice : 0.054;
                double parsedRead = readMeter != null ? readMeter.RetailPrice : 0.004;

                azureRates = new AzureRates(parsedStorage, parsedWrite, parsedRead);

                // STEP B: Async Write-Back to Redis memory with an explicit 12-Hour Time-To-Live (TTL)
                string serializedRates = JsonSerializer.Serialize(azureRates);
                await cache.StringSetAsync(cacheKey, serializedRates, TimeSpan.FromHours(12));
                Console.WriteLine("💾 [Cache Populate] Successfully backed up rates to Redis with a 12-hour TTL expiration.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"API Pricing Latency: {ex.Message}. Bypassing to default seed standard.");
        }
    }

    // Execute the final comparative calculation math strings
    double azureStorageCost = request.DataSizeGb * azureRates.StorageRate;
    double azureWriteCost = (request.WriteOpsCount / 10000.0) * azureRates.WriteRate;
    double azureReadCost = (request.ReadOpsCount / 10000.0) * azureRates.ReadRate;
    double totalAzureMonthlyCost = azureStorageCost + azureWriteCost + azureReadCost;

    double netMonthlySavings = totalAwsMonthlyCost - totalAzureMonthlyCost;

    var finalReport = new StorageComparisonReport(
        request.DataSizeGb,
        new CloudCostBreakdown("AWS S3 Standard", awsStorageCost, awsWriteCost, awsReadCost, totalAwsMonthlyCost),
        new CloudCostBreakdown("Azure Blob (Hot LRS)", azureStorageCost, azureWriteCost, azureReadCost, totalAzureMonthlyCost),
        netMonthlySavings
    );

    return Results.Ok(finalReport);
});

app.Run();

// Core architectural contract structures data schemas
public record StorageWorkloadRequest(double DataSizeGb, long WriteOpsCount, long ReadOpsCount);
public record CloudCostBreakdown(string ServiceName, double CapacityMonthlyCost, double WriteOpsMonthlyCost, double ReadOpsMonthlyCost, double TotalEstimatedMonthlyCost);
public record StorageComparisonReport(double TotalDataAllocatedGb, CloudCostBreakdown AwsMetrics, CloudCostBreakdown AzureMetrics, double ProjectedNetSavings);
public record AzureRates(double StorageRate, double WriteRate, double ReadRate);
public class AzureRetailPriceResponse { [JsonPropertyName("Items")] public List<AzurePriceItem>? Items { get; set; } }
public class AzurePriceItem { [JsonPropertyName("retailPrice")] public double RetailPrice { get; set; } [JsonPropertyName("meterName")] public string MeterName { get; set; } = string.Empty; }
