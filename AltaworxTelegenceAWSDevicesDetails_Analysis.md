# AltaworxTelegenceAWSDevicesDetails Lambda Analysis

This document provides detailed answers to all queries regarding the AltaworxTelegenceAWSDevicesDetails lambda function.

## 1. Why is SQL retry done first, and what issue does it prevent?

**Answer:** SQL retry is implemented using Polly retry policies to handle transient SQL errors and connection issues. The retry mechanism is done first to prevent:

- **Transient SQL Connection Failures**: Network hiccups, temporary database unavailability
- **SQL Timeout Exceptions**: Database busy conditions, long-running queries
- **Connection Pool Exhaustion**: High concurrency scenarios

**Implementation Details:**
```csharp
private static RetryPolicy GetSqlRetryPolicy(KeySysLambdaContext context)
{
    var sqlTransientRetryPolicy = Policy
        .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
        .Or<TimeoutException>()
        .WaitAndRetry(MaxRetries, // 5 attempts
            retryAttempt => TimeSpan.FromSeconds(RetryDelaySeconds), // 5 seconds delay
            (exception, timeSpan, retryCount, sqlContext) => LogInfo(context, "STATUS",
                $"Encountered transient SQL error - delaying for {timeSpan.TotalMilliseconds}ms, then making retry {retryCount}. Exception: {exception?.Message}"));
    return sqlTransientRetryPolicy;
}
```

**Configuration:**
- **MaxRetries**: 5 attempts
- **RetryDelaySeconds**: 5 seconds between attempts

## 2. Are device and BAN staging tables cleared at the start?

**Answer:** Yes, staging tables are cleared at the start of processing:

### Device and Usage Staging Tables:
- **Stored Procedure**: `usp_Telegence_Truncate_DeviceAndUsageStaging`
- **When Cleared**: At the beginning of device processing for each service provider
- **Tables Affected**: `TelegenceDeviceStaging`, `TelegenceDeviceUsageStaging`

### BAN Status Staging Table:
- **Stored Procedure**: `usp_Telegence_Truncate_BillingAccountNumberStatusStaging`
- **When Cleared**: At the beginning of processing for each service provider
- **Table Affected**: `TelegenceDeviceBillingNumberAccountStatusStaging`

### Device Detail Feature Staging:
- **Manual Truncation**: `TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]`
- **When Cleared**: Before processing device detail groups

## 3. Aren't staging tables already cleared after the previous run?

**Answer:** No, staging tables are intentionally cleared at the start of each run, not after completion. This approach ensures:

- **Data Consistency**: Fresh start for each processing cycle
- **Error Recovery**: If previous run failed, stale data is removed
- **Debugging**: Data remains available for troubleshooting until next run
- **Rollback Capability**: Allows for data recovery if needed

The clearing happens in the initialization phase of each service provider processing cycle.

## 4. Which staging table stores BAN, FAN, and Number statuses?

**Answer:** Different staging tables store different types of statuses:

### BAN (Billing Account Number) Status:
- **Table**: `TelegenceDeviceBillingNumberAccountStatusStaging`
- **Columns**: `BillingAccountNumber`, `Status`, `ServiceProviderId`
- **Data Source**: Retrieved from Telegence API via BAN detail endpoint

### FAN (Foundation Account Number) Status:
- **Table**: `TelegenceDeviceStaging`
- **Column**: `FoundationAccountNumber`
- **Additional Info**: FAN filtering is applied based on service provider settings

### Number (Subscriber Number) Status:
- **Table**: `TelegenceDeviceStaging`
- **Column**: `SubscriberNumberStatus`
- **Additional Tables**: 
  - `TelegenceDeviceDetailStaging` (detailed subscriber information)
  - `TelegenceDeviceDetailIdsToProcess` (processing queue)

## 5. Are BAN list statuses read from BillingAccountNumberStatusStaging or elsewhere?

**Answer:** BAN list statuses are read from **both sources** depending on the processing phase:

### From Staging Table:
```csharp
private Dictionary<string, string> GetBanListStatusesStaging(string centralDbConnectionString)
{
    // Reads from TelegenceDeviceBillingNumberAccountStatusStaging
    using (var sqlCommand = new SqlCommand(
        "SELECT BillingAccountNumber, Status, ServiceProviderId FROM TelegenceDeviceBillingNumberAccountStatusStaging where BillingAccountNumber IS NOT NULL", 
        connection))
}
```

### From Telegence API:
- **During initialization**: BAN statuses are fetched from Telegence API using `GetBanStatusAsync()`
- **API Endpoint**: Uses `TelegenceBanDetailGetURL` template
- **Then Saved**: Results are bulk copied to `TelegenceDeviceBillingNumberAccountStatusStaging`

### Processing Flow:
1. **Initialization**: Fetch BAN statuses from API → Save to staging
2. **Main Processing**: Read BAN statuses from staging table
3. **Device Processing**: Use staged BAN statuses for device validation

## 6. Which Telegence API endpoint is called in GetTelegenceDevicesAsync?

**Answer:** The `GetTelegenceDevicesAsync` method calls the **Telegence Devices List API endpoint**:

### Endpoint Configuration:
- **Environment Variable**: `TelegenceDevicesGetURL`
- **Usage**: `TelegenceCommon.GetTelegenceDevicesAsync(context, syncState, ProxyUrl, telegenceDeviceList, TelegenceDevicesGetURL, BatchSize)`

### API Details:
- **Method**: GET
- **Headers**: 
  - `app-id`: Client ID
  - `app-secret`: Client Secret
  - `current-page`: Page number
  - `page-size`: Batch size (default 250)
- **Authentication**: Uses Telegence API authentication
- **Proxy Support**: Configurable proxy server support

### Response Processing:
- Returns paginated list of Telegence devices
- Includes pagination headers (`page-total`, `refresh-timestamp`)
- Processes devices in batches to manage memory and API limits

## 7. What is the page size/limit for Telegence API calls?

**Answer:** The page size for Telegence API calls is configurable:

### Default Configuration:
- **Default Batch Size**: 250 devices per page
- **Environment Variable**: `BatchSize`
- **Fallback**: `DEFAULT_BATCH_SIZE = 250`

### Implementation:
```csharp
private int BatchSize = Convert.ToInt32(Environment.GetEnvironmentVariable("BatchSize")); // 250
private int DEFAULT_BATCH_SIZE = 250;

// Validation logic
var batchSize = int.Parse(context.ClientContext.Environment["BatchSize"]);
BatchSize = batchSize > 0 ? batchSize : DEFAULT_BATCH_SIZE;
```

### API Headers:
- **Header Name**: `page-size`
- **Usage**: Added to both proxy and direct API calls
- **Applies To**: Device list API calls, not individual device detail calls

## 8. How does the system know all API pages are processed?

**Answer:** The system uses multiple indicators to determine when all pages are processed:

### Primary Indicators:
1. **`IsLastCycle` Flag**: Set by API response analysis
2. **`HasMoreData` Flag**: Indicates if more pages exist
3. **Page Total Comparison**: `syncState.CurrentPage < pageTotal`

### API Response Headers:
```csharp
// From API response headers
if (int.TryParse(headers[CommonConstants.PAGE_TOTAL].ToString(), out int pageTotal))
{
    syncState.HasMoreData = syncState.CurrentPage < pageTotal;
}
syncState.IsLastCycle = !syncState.HasMoreData;
```

### Safeguards:
- **MaxCyclesToProcess**: Prevents infinite loops (configurable limit)
- **Remaining Time Check**: Stops processing when Lambda timeout approaches
- **Retry Counter**: Limits retry attempts for failed pages

### Processing Logic:
```csharp
while (cycleCounter <= MaxCyclesToProcess)
{
    if (!syncState.IsLastCycle && remainingTime > REMAINING_TIME_CUT_OFF)
    {
        syncState = await GetTelegenceDevicesFromAPI(context, syncState, telegenceDeviceList);
        syncState.CurrentPage++;
        cycleCounter++;
    }
    else break;
}
```

## 9. What parameters are used in GetTelegenceDeviceBySubscriberNumber?

**Answer:** The `GetTelegenceDeviceBySubscriberNumber` method uses the following parameters:

### Method Signature:
```csharp
public static async Task<string> GetTelegenceDeviceBySubscriberNumber(
    KeySysLambdaContext context, 
    TelegenceAuthentication telegenceAuthentication,
    bool isProduction, 
    string subscriberNo, 
    string endpoint, 
    string proxyUrl)
```

### Parameters:
1. **context**: Lambda execution context for logging
2. **telegenceAuthentication**: Contains API credentials and URLs
3. **isProduction**: Determines sandbox vs production URL
4. **subscriberNo**: The subscriber number to query
5. **endpoint**: API endpoint template (e.g., `/device/{subscriberNumber}`)
6. **proxyUrl**: Optional proxy server URL

### API Call Details:
- **Final URL**: `{baseUrl}{endpoint}{subscriberNo}`
- **Headers**: 
  - `Accept`: `application/json`
  - `app-id`: Client ID
  - `app-secret`: Client Secret
- **Method**: GET
- **Retry Policy**: Uses Polly retry with `NUMBER_OF_TELEGENCE_RETRIES`

### Usage in Lambda:
```csharp
string resultAPI = await TelegenceCommon.GetTelegenceDeviceBySubscriberNumber(
    context, 
    telegenceAuthenticationInfo, 
    context.IsProduction,
    telegenceDevice.SubscriberNumber, 
    TelegenceDeviceDetailGetURL, 
    ProxyUrl);
```

## 10. What happens to devices that fail validation?

**Answer:** Devices that fail validation are handled through several mechanisms:

### Removal from Processing Queue:
```csharp
// For devices that fail API calls or validation
LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
```

### RemoveDeviceFromQueue Implementation:
```csharp
private static void RemoveDeviceFromQueue(string connectionString, int groupNumber, string subscriberNumber)
{
    using (var cmd = new SqlCommand(
        @"UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
          SET [IsDeleted] = 1
          WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber", con))
    {
        // Mark device as deleted/processed
    }
}
```

### Validation Failure Scenarios:
1. **API Call Failures**: Network errors, API timeouts
2. **Empty/Null Response**: API returns no data for subscriber
3. **JSON Parsing Errors**: Malformed API responses
4. **Missing Required Fields**: Incomplete device data

### Error Handling:
- **Logging**: All failures are logged with exception details
- **Continuation**: Processing continues with next device
- **Retry Logic**: Transient failures are retried via Polly policies
- **Queue Management**: Failed devices are marked as processed to prevent reprocessing

## 11. What is the retry setup for Polly (attempts, delay)?

**Answer:** The system uses multiple Polly retry configurations:

### SQL Retry Policy:
```csharp
private const int MaxRetries = 5;
private const int RetryDelaySeconds = 5;

var sqlTransientRetryPolicy = Policy
    .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
    .Or<TimeoutException>()
    .WaitAndRetry(MaxRetries, // 5 attempts
        retryAttempt => TimeSpan.FromSeconds(RetryDelaySeconds), // 5 seconds
        (exception, timeSpan, retryCount, sqlContext) => /* logging */);
```

### Telegence API Retry Policy:
```csharp
// For device details API calls
var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries); // 5 retries

// Uses CommonConstants.NUMBER_OF_TELEGENCE_RETRIES (typically 3-5)
var response = await RetryPolicyHelper.PollyRetryHttpRequestAsync(_logger, CommonConstants.NUMBER_OF_TELEGENCE_RETRIES)
```

### Lambda-Level Retry:
```csharp
// For incomplete processing due to timeout
if (syncState.RetryNumber <= CommonConstants.NUMBER_OF_TELEGENCE_LAMBDA_RETRIES)
{
    await SendMessageToGetDevicesQueueAsync(context, syncState, queueURL, delaySeconds);
}
```

### Retry Configurations:
- **SQL Retries**: 5 attempts, 5-second delay
- **API Retries**: 3-5 attempts (configured via constants)
- **Lambda Retries**: Configurable via `NUMBER_OF_TELEGENCE_LAMBDA_RETRIES`
- **Queue Delays**: 5-60 seconds between message processing

## 12. How is re-enqueuing handled for incomplete or timed-out device lists?

**Answer:** Re-enqueuing is handled through SQS message attributes and state management:

### Re-enqueuing Triggers:
1. **Timeout Conditions**: When `RemainingTime < REMAINING_TIME_CUT_OFF`
2. **Incomplete Processing**: When not all devices in batch are processed
3. **API Failures**: When Telegence API calls fail temporarily

### SQS Message Structure:
```csharp
var request = new SendMessageRequest
{
    DelaySeconds = delaySeconds, // 5-60 seconds
    MessageAttributes = new Dictionary<string, MessageAttributeValue>
    {
        {"InitializeProcessing", new MessageAttributeValue {DataType = "String", StringValue = false.ToString()}},
        {"GroupNumber", new MessageAttributeValue {DataType = "String", StringValue = groupNumber.ToString()}},
        {"RetryNumber", new MessageAttributeValue {DataType = "String", StringValue = syncState.RetryNumber.ToString()}}
    },
    MessageBody = $"Requesting next {BatchSize} records to process",
    QueueUrl = TelegenceDeviceDetailQueueURL
};
```

### State Persistence:
- **GroupNumber**: Tracks which batch of devices to process
- **RetryNumber**: Prevents infinite retry loops
- **InitializeProcessing**: Distinguishes between initial and retry processing

### Re-enqueuing Logic:
```csharp
// Check remaining time before processing each device
var remainingTime = context.Context.RemainingTime.TotalSeconds;
if (remainingTime > CommonConstants.REMAINING_TIME_CUT_OFF)
{
    // Process device
}
else
{
    LogInfo(context, CommonConstants.INFO, $"Remaining run time ({remainingTime} seconds) is not enough to continue.");
    // Re-enqueue for later processing
    await SendProcessMessageToQueueAsync(context, groupNumber);
    break;
}
```

## 13. How do the stored procedures work in the flow?

**Answer:** Multiple stored procedures orchestrate the device detail processing flow:

### Initial Setup Procedures:

#### `usp_Telegence_Devices_GetDetailFilter`
- **Purpose**: Prepares device IDs for detail processing
- **Parameters**: `@BatchSize`
- **Function**: Creates batched groups in `TelegenceDeviceDetailIdsToProcess`
- **Called**: At initialization (`StartDailyDeviceDetailProcessingAsync`)

#### `usp_Telegence_Truncate_DeviceAndUsageStaging`
- **Purpose**: Clears staging tables for fresh processing
- **Tables Cleared**: Device and usage staging tables
- **Called**: At service provider initialization

#### `usp_Telegence_Truncate_BillingAccountNumberStatusStaging`
- **Purpose**: Clears BAN status staging table
- **Called**: Before BAN status processing

### Processing Procedures:

#### Device Group Management:
```csharp
// Get maximum group number for processing
"SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE IsDeleted = 0"

// Get devices to process by group
"SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE GroupNumber = @GroupNumber AND [IsDeleted] = 0"
```

#### Device Removal Procedures:
```csharp
// Mark processed devices as deleted
"UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess] SET [IsDeleted] = 1 WHERE GroupNumber = @GroupNumber AND SubscriberNumber IN (SELECT SubscriberNumber FROM [dbo].[TelegenceDeviceDetailStaging])"
```

### Data Flow:
1. **Setup**: `usp_Telegence_Devices_GetDetailFilter` creates processing groups
2. **Processing**: Lambda processes each group, calling Telegence API
3. **Storage**: Results bulk copied to `TelegenceDeviceDetailStaging`
4. **Cleanup**: Processed devices marked as deleted
5. **Features**: Device features stored in `TelegenceDeviceMobilityFeature_Staging`

## 14. What details are captured in the summary logs?

**Answer:** The lambda captures comprehensive logging details throughout processing:

### Processing Status Logs:
```csharp
LogInfo(keysysContext, "STATUS", $"Beginning to process {sqsEvent.Records.Count} records...");
LogInfo(keysysContext, "STATUS", $"Processed {sqsEvent.Records.Count} records.");
```

### Message Attribute Logs:
- **MessageId**: SQS message identifier
- **EventSource**: Source of the SQS event
- **Body**: Message body content
- **InitializeProcessing**: Whether this is initialization or continuation
- **GroupNumber**: Current processing group
- **BatchSize**: Number of devices per batch

### API Call Logs:
```csharp
LogInfo(context, CommonConstants.INFO, $"Get Detail for Subscriber Number : {subscriberNumber}");
LogInfo(context, CommonConstants.INFO, string.Format(LogCommonStrings.REQUEST_GET_DEVICE_DETAIL, deviceDetailUrl));
```

### Processing Statistics:
- **Group Count**: Total number of processing groups
- **Remaining Time**: Lambda execution time remaining
- **Retry Numbers**: Current retry attempt count
- **Batch Processing**: Number of devices processed per batch

### Error Logs:
- **SQL Exceptions**: Database connectivity and query issues
- **API Failures**: Telegence API call failures
- **Timeout Warnings**: When processing stops due to time limits
- **Validation Errors**: Device data validation failures

### Performance Logs:
- **Bulk Copy Operations**: Data insertion statistics
- **Queue Operations**: SQS message sending confirmations
- **Processing Duration**: Time taken for various operations

## 15. How are reference items (functions, queues, procedures) used in the flow?

**Answer:** Reference items are orchestrated through a complex workflow:

### Queue References:

#### **TelegenceDeviceDetailQueueURL**
- **Purpose**: Main processing queue for device details
- **Message Flow**: Initialization → Group Processing → Continuation
- **Attributes**: GroupNumber, InitializeProcessing, RetryNumber

#### **TelegenceDeviceUsageQueueURL**
- **Purpose**: Triggers device usage processing after details complete
- **Delay**: 5 seconds after detail processing completion

### Function References:

#### **Core Processing Functions**:
1. **`StartDailyDeviceDetailProcessingAsync`**: Initializes daily processing
2. **`ProcessDetailListAsync`**: Processes device batches
3. **`GetTelegenceDevicesAsync`**: Calls Telegence API
4. **`SendProcessMessageToQueueAsync`**: Manages queue continuation

#### **Utility Functions**:
1. **`GetGroupCount`**: Determines processing groups
2. **`RemoveDeviceFromQueue`**: Handles failed devices
3. **`GetDeviceOfferingCodes`**: Extracts device features
4. **`RemovePreviouslyStagedFeatures`**: Cleans feature staging

### Procedure References:

#### **Setup Procedures**:
- `usp_Telegence_Devices_GetDetailFilter`: Creates processing batches
- Truncation procedures: Clear staging tables

#### **Processing Procedures**:
- Dynamic SQL for group processing
- Bulk copy operations for staging data
- Device removal and cleanup operations

### Integration Flow:
```
1. Lambda Invoked → BaseFunctionHandler
2. Check InitializeProcessing → StartDailyDeviceDetailProcessingAsync
3. Call Setup Procedure → usp_Telegence_Devices_GetDetailFilter
4. Get Group Count → Create SQS Messages for Each Group
5. Process Groups → ProcessDetailListAsync
6. API Calls → GetDeviceDetails (Telegence API)
7. Store Results → Bulk Copy to Staging Tables
8. Queue Continuation → SendProcessMessageToQueueAsync
9. Completion → Trigger Usage and Detail Queues
```

### Error Handling Integration:
- **Polly Retry Policies**: Wrap API and SQL calls
- **Queue Re-enqueuing**: Handle timeouts and failures
- **State Management**: Track processing progress across invocations
- **Logging Integration**: Comprehensive error tracking and debugging

This orchestrated approach ensures reliable, scalable processing of Telegence device details with proper error handling and recovery mechanisms.

---

## Summary

The AltaworxTelegenceAWSDevicesDetails lambda is a sophisticated system that:

1. **Manages Device Processing**: Batches device detail retrieval from Telegence API
2. **Handles Failures Gracefully**: Uses Polly retry policies and queue re-enqueuing
3. **Maintains Data Integrity**: Clears and manages staging tables properly
4. **Scales Efficiently**: Processes devices in configurable batch sizes
5. **Provides Comprehensive Logging**: Tracks all operations for debugging and monitoring
6. **Integrates with Workflow**: Coordinates with other lambdas through SQS queues

The system demonstrates enterprise-grade patterns for API integration, error handling, and data processing in a serverless environment.