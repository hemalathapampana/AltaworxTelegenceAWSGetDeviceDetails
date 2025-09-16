# AltaworxTelegenceAWSGetDeviceDetails Lambda Function - Detailed Analysis

## Overview
The **AltaworxTelegenceAWSGetDeviceDetails** Lambda function is responsible for retrieving detailed device information from the Telegence API and staging it for further processing. This Lambda operates in a batch processing model, handling device details in configurable batch sizes with proper error handling and retry mechanisms.

## Lambda Triggers

### What Triggers the AltaworxTelegenceAWSGetDeviceDetails Lambda?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda is triggered by **SQS messages** from the `TelegenceDeviceDetailQueueURL`. The Lambda can be triggered in two modes:

1. **Initialization Mode** (`InitializeProcessing = true`):
   - Triggered manually or by another Lambda/scheduler to start the daily device detail processing
   - Calls the stored procedure `usp_Telegence_Devices_GetDetailFilter` to prepare device IDs for processing
   - Creates multiple SQS messages for batch processing

2. **Processing Mode** (`InitializeProcessing = false`):
   - Self-triggered by the Lambda itself after initialization
   - Each message contains a `GroupNumber` parameter to identify which batch of devices to process
   - Messages are sent with a 5-second delay (`DelaySeconds = 5`)

### Message Structure
```json
{
  "MessageAttributes": {
    "InitializeProcessing": {"StringValue": "true/false"},
    "GroupNumber": {"StringValue": "0-N"}
  },
  "MessageBody": "Requesting next {BatchSize} records to process"
}
```

## SQL Retry Mechanism

### Why is SQL Retry Done First?

SQL retry is implemented first in the **AltaworxTelegenceAWSGetDeviceDetails** Lambda to handle **transient SQL connection issues** that commonly occur in cloud environments. The retry mechanism prevents the following issues:

1. **Temporary Connection Failures**: Network timeouts or temporary database unavailability
2. **SQL Server Transient Errors**: Deadlocks, connection pool exhaustion, or temporary resource constraints
3. **Data Consistency**: Ensures critical operations like reading device IDs and updating staging tables complete successfully

### Retry Configuration
```csharp
private const int MaxRetries = 5;
private const int RetryDelaySeconds = 5;

var sqlRetryPolicy = Policy
    .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
    .Or<TimeoutException>()
    .WaitAndRetry(MaxRetries,
        retryAttempt => TimeSpan.FromSeconds(RetryDelaySeconds));
```

The retry policy in **AltaworxTelegenceAWSGetDeviceDetails** specifically handles:
- SQL Server transient exceptions (detected by `SqlServerTransientExceptionDetector`)
- Timeout exceptions
- 5 retry attempts with 5-second delays between attempts

## Staging Tables Management

### Are Device and BAN Staging Tables Cleared at Start?

**Yes, but only specific staging tables are cleared by the AltaworxTelegenceAWSGetDeviceDetails Lambda:**

1. **TelegenceDeviceMobilityFeature_Staging**: This table is **TRUNCATED** at the start of initialization in the `RemovePreviouslyStagedFeatures()` method
   ```csharp
   string cmdText = "TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]";
   ```

2. **TelegenceDeviceDetailStaging**: This table is **NOT** cleared at start. Instead, processed records are removed incrementally as they are successfully staged

3. **BAN Staging Tables**: The **AltaworxTelegenceAWSGetDeviceDetails** Lambda does not directly manage BAN staging tables - this is handled by other Lambdas in the system

### Aren't Staging Tables Already Cleared After Previous Run?

**Not necessarily.** The staging table clearing strategy in **AltaworxTelegenceAWSGetDeviceDetails** is designed to handle:

1. **Incomplete Previous Runs**: If the previous Lambda execution failed or timed out, staging data might remain
2. **Feature Accumulation**: The `TelegenceDeviceMobilityFeature_Staging` table accumulates device features and needs to be cleared before each full processing cycle
3. **Incremental Processing**: The `TelegenceDeviceDetailStaging` table uses incremental updates rather than full clearing

## Staging Table Details

### Which Staging Table Stores BAN, FAN, and Number Statuses?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda works with the following staging tables:

1. **TelegenceDeviceDetailStaging**: Stores comprehensive device details including:
   - SubscriberNumber (phone numbers)
   - Device characteristics (IMEI, ICCID, device make/model)
   - Service information (activation dates, billing cycles)
   - **Note**: This table does not store BAN/FAN statuses directly

2. **TelegenceDeviceMobilityFeature_Staging**: Stores device offering codes and features
   - SubscriberNumber
   - OfferingCode (truncated to 50 characters if longer)

**BAN and FAN statuses are NOT managed by the AltaworxTelegenceAWSGetDeviceDetails Lambda.** These are typically handled by other specialized Lambdas in the Telegence sync system.

### Are BAN List Statuses Read from BillingAccountNumberStatusStaging?

**No**, the **AltaworxTelegenceAWSGetDeviceDetails** Lambda does not read from or interact with `BillingAccountNumberStatusStaging`. This Lambda focuses specifically on device details, not billing account statuses.

## Telegence API Integration

### Which Telegence API Endpoint is Called in GetTelegenceDevicesAsync?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda calls the Telegence API through the `TelegenceAPIClient.GetDeviceDetails()` method, but it does **NOT** use `GetTelegenceDevicesAsync`. Instead, it uses:

**Device Detail Endpoint**: The endpoint is constructed using:
```csharp
string deviceDetailUrl = string.Format(TelegenceDeviceDetailGetURL, subscriberNumber);
var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
```

Where `TelegenceDeviceDetailGetURL` is an environment variable containing a URL template with a placeholder for the subscriber number.

### What Parameters are Used in GetTelegenceDeviceBySubscriberNumber?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda doesn't directly call `GetTelegenceDeviceBySubscriberNumber`, but the `TelegenceCommon` class provides this method with the following parameters:

```csharp
public static async Task<string> GetTelegenceDeviceBySubscriberNumber(
    KeySysLambdaContext context,
    TelegenceAuthentication telegenceAuthentication,
    bool isProduction,
    string subscriberNo,
    string endpoint,
    string proxyUrl)
```

Parameters:
- **subscriberNo**: The phone number/subscriber identifier
- **endpoint**: The API endpoint template
- **isProduction**: Boolean flag for production vs sandbox environment
- **proxyUrl**: Optional proxy server URL
- **telegenceAuthentication**: Contains ClientId, ClientSecret, and URLs

### What is the Page Size/Limit for Telegence API Calls?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda does **NOT** use pagination as it processes individual device details one by one. However, it processes devices in batches determined by:

```csharp
private int BatchSize = Convert.ToInt32(Environment.GetEnvironmentVariable("BatchSize"));
```

The `BatchSize` environment variable controls:
- How many device IDs are retrieved from `TelegenceDeviceDetailIdsToProcess` per Lambda execution
- The number of individual API calls made per batch
- **Each device requires a separate API call** - there is no bulk device detail endpoint

### How Does the System Know All API Pages are Processed?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda doesn't use traditional pagination. Instead, it uses a **group-based processing model**:

1. **Group Creation**: The stored procedure `usp_Telegence_Devices_GetDetailFilter` assigns devices to groups based on `BatchSize`
2. **Group Counting**: The Lambda queries for the maximum group number:
   ```csharp
   SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE IsDeleted = 0
   ```
3. **Sequential Processing**: The Lambda processes groups 0 through the maximum group number
4. **Completion Detection**: When no more records exist for a group, processing for that group is complete
5. **Re-queuing**: The Lambda re-queues itself for the next group until all groups are processed

## Device Validation and Error Handling

### What Happens to Devices That Fail Validation?

In the **AltaworxTelegenceAWSGetDeviceDetails** Lambda, devices that fail validation are handled as follows:

1. **API Call Failures**: If the Telegence API call fails or returns invalid data:
   ```csharp
   catch (Exception e)
   {
       LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");
       LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
       sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
       continue;
   }
   ```

2. **Invalid Response Data**: If the API returns null or invalid device details:
   ```csharp
   if (deviceDetail != null && !string.IsNullOrWhiteSpace(deviceDetail.SubscriberNumber))
   {
       // Process valid device
   }
   else
   {
       LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
       sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
   }
   ```

3. **Device Removal**: Failed devices are marked as deleted in the processing queue:
   ```csharp
   UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
   SET [IsDeleted] = 1
   WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber
   ```

### What is the Retry Setup for Polly?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda uses Polly for retry policies in multiple contexts:

#### SQL Retry Policy
```csharp
private const int MaxRetries = 5;
private const int RetryDelaySeconds = 5;

var sqlTransientRetryPolicy = Policy
    .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
    .Or<TimeoutException>()
    .WaitAndRetry(MaxRetries,
        retryAttempt => TimeSpan.FromSeconds(RetryDelaySeconds));
```

#### API Retry Policy (via TelegenceAPIClient)
The `TelegenceAPIClient.GetDeviceDetails()` method implements its own retry mechanism:
- **MaxRetries**: 5 attempts (passed as parameter)
- **Retry Logic**: Handled by `RetryPolicyHelper.PollyRetryHttpRequestAsync`
- **Transient Error Detection**: Automatic retry on HTTP timeouts and transient failures

### How is Re-enqueuing Handled for Incomplete or Timed-out Device Lists?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda implements sophisticated re-queuing logic:

1. **Time-based Processing Control**:
   ```csharp
   var remainingTime = context.Context.RemainingTime.TotalSeconds;
   if (remainingTime > CommonConstants.REMAINING_TIME_CUT_OFF)
   {
       // Process device
   }
   else
   {
       LogInfo(context, CommonConstants.INFO, $"Remaining run time ({remainingTime} seconds) is not enough to continue.");
       break;
   }
   ```

2. **Automatic Re-queuing**: After processing each batch (successful or partial), the Lambda re-queues itself:
   ```csharp
   await SendProcessMessageToQueueAsync(context, groupNumber);
   ```

3. **Group-based Continuation**: Unprocessed devices remain in the `TelegenceDeviceDetailIdsToProcess` table with `IsDeleted = 0`, ensuring they will be picked up in subsequent executions

4. **Graceful Timeout Handling**: If the Lambda approaches its timeout limit, it stops processing new devices but still re-queues for continuation

## Stored Procedures in the Flow

### How Do the Stored Procedures Work in the Flow?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda uses the following stored procedures:

#### 1. usp_Telegence_Devices_GetDetailFilter
- **Purpose**: Initializes the device processing queue
- **Called**: During initialization phase (`InitializeProcessing = true`)
- **Parameters**: `@BatchSize` - determines how devices are grouped
- **Function**: 
  - Identifies devices that need detail updates
  - Creates groups in `TelegenceDeviceDetailIdsToProcess` table
  - Assigns `GroupNumber` based on batch size

#### 2. usp_Telegence_Get_AuthenticationByProviderId (via TelegenceCommon)
- **Purpose**: Retrieves Telegence API authentication credentials
- **Parameters**: `@providerId` - service provider identifier
- **Returns**: ClientId, ClientSecret, URLs, and configuration settings

#### 3. usp_Telegence_Get_BillingAccountsByProviderId (via TelegenceCommon)
- **Purpose**: Retrieves billing account mappings (used by other components)
- **Parameters**: `@providerId` - service provider identifier

## Data Flow and Processing

### Detailed Step-by-Step Flow

1. **Lambda Invocation**:
   - **AltaworxTelegenceAWSGetDeviceDetails** receives SQS event
   - Extracts `InitializeProcessing` and `GroupNumber` from message attributes

2. **Initialization Phase** (`InitializeProcessing = true`):
   - Calls `usp_Telegence_Devices_GetDetailFilter` with `BatchSize` parameter
   - Queries for maximum group number from `TelegenceDeviceDetailIdsToProcess`
   - Clears `TelegenceDeviceMobilityFeature_Staging` table
   - Enqueues processing messages for each group (0 to max group number)

3. **Processing Phase** (`InitializeProcessing = false`):
   - Retrieves up to `BatchSize` subscriber numbers from `TelegenceDeviceDetailIdsToProcess` for the specified group
   - Gets Telegence authentication credentials for the service provider
   - Creates `TelegenceAPIClient` with authentication and proxy settings

4. **Device Detail Processing**:
   - For each subscriber number in the batch:
     - Checks remaining Lambda execution time
     - Calls Telegence API `GetDeviceDetails` with retry logic
     - If successful, processes device characteristics using `TelegenceServicegGetCharacteristicHelper`
     - Adds device details to `TelegenceDeviceDetailSyncTable`
     - Extracts offering codes and adds to `TelegenceDeviceFeatureSyncTable`
     - If failed, removes device from processing queue

5. **Staging Data Load**:
   - Bulk copies `TelegenceDeviceDetailSyncTable` to `TelegenceDeviceDetailStaging`
   - Bulk copies `TelegenceDeviceFeatureSyncTable` to `TelegenceDeviceMobilityFeature_Staging`
   - Removes successfully processed devices from `TelegenceDeviceDetailIdsToProcess`

6. **Re-queuing**:
   - Sends new SQS message for the same group number to continue processing remaining devices
   - Process repeats until no more devices exist for the group

## Summary Logging and Monitoring

### What Details are Captured in Summary Logs?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda captures comprehensive logging:

#### Initialization Logs
- Processing start/end status
- Number of SQS records being processed
- Group count determination
- Batch size configuration

#### Processing Logs
- Individual subscriber number processing
- API call success/failure details
- Device validation results
- Bulk copy operations
- Exception details with full stack traces

#### Performance Logs
- Remaining execution time monitoring
- Batch processing metrics
- SQL operation timing
- API call response times

#### Error Logs
- SQL connection failures and retries
- API call failures with response details
- Device validation failures
- Timeout warnings

## Reference Items Usage

### How are Reference Items Used in the Flow?

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda utilizes several reference components:

#### Functions
- **TelegenceCommon.GetTelegenceAuthenticationInformation**: Retrieves API credentials
- **TelegenceAPIClient.GetDeviceDetails**: Makes authenticated API calls
- **TelegenceServicegGetCharacteristicHelper**: Parses device characteristics

#### Queues
- **TelegenceDeviceDetailQueueURL**: SQS queue for batch processing coordination
- Self-queuing mechanism for continuous processing

#### Stored Procedures
- **usp_Telegence_Devices_GetDetailFilter**: Device queue initialization
- **usp_Telegence_Get_AuthenticationByProviderId**: Authentication retrieval

#### Tables
- **TelegenceDeviceDetailIdsToProcess**: Processing queue management
- **TelegenceDeviceDetailStaging**: Final device detail storage
- **TelegenceDeviceMobilityFeature_Staging**: Device feature storage

## Environment Configuration

### Key Environment Variables
- **TelegenceDeviceDetailGetURL**: API endpoint template for device details
- **TelegenceDeviceDetailQueueURL**: SQS queue URL for processing coordination
- **ProxyUrl**: Optional proxy server for API calls
- **BatchSize**: Number of devices to process per Lambda execution

## Error Handling and Resilience

The **AltaworxTelegenceAWSGetDeviceDetails** Lambda implements multiple layers of resilience:

1. **SQL Retry Policy**: Handles database connection issues
2. **API Retry Policy**: Handles Telegence API transient failures
3. **Timeout Management**: Graceful handling of Lambda execution limits
4. **Device-level Error Isolation**: Failed devices don't stop batch processing
5. **Automatic Re-queuing**: Ensures processing continuation across Lambda invocations
6. **Comprehensive Logging**: Enables effective troubleshooting and monitoring

This design ensures robust, scalable processing of device details while maintaining data consistency and providing clear visibility into the processing flow.