# AltaworxTelegenceAWSGetDeviceDetails Lambda Analysis

## Overview
The `AltaworxTelegenceAWSGetDeviceDetails` Lambda function is responsible for processing device details from the Telegence API in batches. It processes SQS messages to either initialize device detail processing or process specific groups of subscriber numbers.

---

## 1. SQS Message Triggers for Device Details Processing

### Initial Trigger
The first SQS message is triggered when:
- An SQS message arrives with `MessageAttributes["InitializeProcessing"] = true`
- This triggers the `StartDailyDeviceDetailProcessingAsync()` method

### Processing Flow
1. **Initial Message**: Contains `InitializeProcessing = true`
2. **Subsequent Messages**: Contain `InitializeProcessing = false` and `GroupNumber = [specific group]`

### Message Attributes
```csharp
MessageAttributes = {
    {"InitializeProcessing", new MessageAttributeValue {DataType = "String", StringValue = boolean.ToString()}},
    {"GroupNumber", new MessageAttributeValue {DataType = "String", StringValue = groupNumber.ToString()}}
}
```

### Queue Configuration
- **Queue URL**: Environment variable `TelegenceDeviceDetailQueueURL`
- **Delay**: 5 seconds delay for each queued message
- **Region**: USEast1

---

## 2. Batch Size Limits for Subscribers

### Default Configuration
- **Source**: Environment variable `BatchSize`
- **Fallback**: Context client environment `BatchSize`
- **Usage**: Used in stored procedure `usp_Telegence_Devices_GetDetailFilter`

### Implementation
```csharp
private int BatchSize = Convert.ToInt32(Environment.GetEnvironmentVariable("BatchSize"));
```

### Database Query Limit
```sql
SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] 
FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) 
WHERE GroupNumber = @GroupNumber AND [IsDeleted] = 0
```

**Note**: The actual batch size value is configured externally via environment variables and is not hardcoded in the Lambda function.

---

## 3. Exact API Endpoint Called in GetDeviceDetails

### Endpoint Configuration
- **Base URL**: Environment variable `TelegenceDeviceDetailGetURL`
- **Format**: String format pattern expecting subscriber number placeholder

### API Call Implementation
```csharp
string deviceDetailUrl = string.Format(TelegenceDeviceDetailGetURL, subscriberNumber);
var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
```

### TelegenceAPIClient.GetDeviceDetails Method
- **HTTP Method**: GET
- **Headers**: 
  - `Accept`: `application/json`
  - `app-id`: Client ID from authentication
  - `app-secret`: Client secret from authentication
- **Authentication**: Uses Telegence API authentication (app-id/app-secret headers)
- **Base URL**: Production or Sandbox URL based on environment

### Complete Endpoint Construction
```csharp
var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
var requestUri = new Uri($"{baseUri}{deviceDetailsEndpoint}");
```

---

## 4. Failed Subscriber Handling

### Retry Strategy
- **Automatic Retries**: Yes, using Polly retry policies
- **API Retries**: `MaxRetries = 5` (configurable per call)
- **SQL Retries**: `MaxRetries = 5` with `RetryDelaySeconds = 5`

### Failure Handling Process
1. **API Call Failure**: 
   - Logged as exception
   - Subscriber removed from queue via `RemoveDeviceFromQueue()`
   - Processing continues with next subscriber

2. **Invalid/Empty Response**:
   - Subscriber removed from queue via `RemoveDeviceFromQueue()`
   - Processing continues with next subscriber

### Error Logging
```csharp
catch (Exception e)
{
    LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");
    LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
    sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
    continue;
}
```

### Behavior Summary
- **Failed subscribers**: Automatically removed from processing queue
- **Retries**: Handled automatically by Polly policies
- **Logging**: All failures are logged with full exception details
- **Skipping**: Failed subscribers are skipped, processing continues

---

## 5. Tables for Error/Unprocessed Subscriber Capture

### Primary Processing Table
- **Table**: `TelegenceDeviceDetailIdsToProcess`
- **Purpose**: Queue of subscribers to process
- **Key Fields**:
  - `SubscriberNumber`
  - `ServiceProviderId`
  - `GroupNumber`
  - `IsDeleted` (used to mark processed/failed items)

### Staging Tables
- **Table**: `TelegenceDeviceDetailStaging`
- **Purpose**: Temporary storage for successfully retrieved device details
- **Populated by**: `TelegenceDeviceDetailSyncTable` DataTable

- **Table**: `TelegenceDeviceMobilityFeature_Staging`
- **Purpose**: Temporary storage for device offering codes/features
- **Populated by**: `TelegenceDeviceFeatureSyncTable` DataTable

### Error Handling in Database
```sql
-- Mark failed/processed subscribers as deleted
UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
SET [IsDeleted] = 1
WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber
```

### Cleanup Process
- Successfully processed subscribers are marked as `IsDeleted = 1`
- Failed subscribers are also marked as `IsDeleted = 1` to prevent reprocessing
- Staging table `TelegenceDeviceMobilityFeature_Staging` is truncated at start of processing

---

## 6. Polly Retry Configuration for SQL/API Failures

### SQL Retry Policy
```csharp
private static RetryPolicy GetSqlRetryPolicy(KeySysLambdaContext context)
{
    var sqlTransientRetryPolicy = Policy
        .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
        .Or<TimeoutException>()
        .WaitAndRetry(MaxRetries,
            retryAttempt => TimeSpan.FromSeconds(RetryDelaySeconds),
            (exception, timeSpan, retryCount, sqlContext) => LogInfo(context, "STATUS",
                $"Encountered transient SQL error - delaying for {timeSpan.TotalMilliseconds}ms, then making retry {retryCount}. Exception: {exception?.Message}"));
    return sqlTransientRetryPolicy;
}
```

### SQL Retry Configuration
- **Max Retries**: 5
- **Retry Delay**: 5 seconds
- **Exception Types**: 
  - `SqlException` (with transient error detection)
  - `TimeoutException`
- **Strategy**: Wait and retry with fixed delay

### API Retry Policy (Telegence API Client)
```csharp
var response = await RetryPolicyHelper.PollyRetryHttpRequestAsync(_logger, retryNumber).ExecuteAsync(async () =>
{
    var request = _httpRequestFactory.BuildRequestMessage(_authentication, new HttpMethod(CommonConstants.METHOD_GET), requestUri,
        new Dictionary<string, string> { 
            { CommonConstants.ACCEPT, CommonConstants.APPLICATION_JSON }, 
            { CommonConstants.APP_ID, _authentication.ClientId }, 
            { CommonConstants.APP_SECRET, _authentication.ClientSecret } 
        });
    return await _httpClientFactory.GetClient().SendAsync(request);
});
```

### API Retry Configuration
- **Max Retries**: Configurable (default via `CommonConstants.NUMBER_OF_TELEGENCE_RETRIES`)
- **Retry Helper**: `RetryPolicyHelper.PollyRetryHttpRequestAsync`
- **Default Retries for GetDeviceDetails**: 5 (`MaxRetries` constant)
- **Strategy**: Polly retry policy with exponential backoff (implementation in RetryPolicyHelper)

### Constants Used
```csharp
private const int MaxRetries = 5;
private const int RetryDelaySeconds = 5;
```

---

## Summary

The `AltaworxTelegenceAWSGetDeviceDetails` Lambda function implements a robust batch processing system with comprehensive error handling and retry mechanisms. It processes subscribers in configurable batch sizes, handles failures gracefully by removing failed items from the queue, and uses Polly retry policies for both SQL and API operations. The system maintains clear separation between processing queues and staging tables, ensuring data integrity throughout the process.