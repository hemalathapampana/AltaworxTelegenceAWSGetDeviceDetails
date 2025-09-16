# AltaworxTelegenceAWSGetDeviceUsage Lambda - Comprehensive Analysis

## Overview
The AltaworxTelegenceAWSGetDeviceUsage Lambda is a comprehensive device usage processing system that handles Telegence device data synchronization, FTP file downloads, API interactions, and database operations for AT&T Telegence providers.

## 1. Lambda Triggers

### Primary Triggers
- **SQS Queue Messages**: The Lambda is primarily triggered by SQS messages from:
  - `TelegenceDeviceUsageQueueURL` (ExportDeviceUsageQueueURL)
  - `TelegenceDeviceNotificationQueueURL` (DeviceNotificationQueueURL)
- **CloudWatch Events**: Can be triggered by scheduled CloudWatch events for automated processing
- **Manual Invocation**: Can be invoked manually without SQS events, which triggers the daily device usage processing for all Telegence providers

### Trigger Flow
- **With SQS Event**: Processes individual messages with specific service provider and operation parameters
- **Without SQS Event**: Initiates daily processing by queuing all AT&T Telegence providers

## 2. SQL Retry Logic

### Implementation Details
The AltaworxTelegenceAWSGetDeviceUsage Lambda implements robust SQL retry logic through the `GetSqlRetryPolicy()` method:

```csharp
private static RetryPolicy GetSqlRetryPolicy(KeySysLambdaContext context)
{
    var sqlTransientRetryPolicy = Policy
        .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
        .Or<TimeoutException>()
        .WaitAndRetry(MaxRetries, // 3 attempts
            retryAttempt => TimeSpan.FromSeconds(RetryDelaySeconds), // 5 seconds delay
            (exception, timeSpan, retryCount, sqlContext) => LogInfo(context, "STATUS",
                $"Encountered transient SQL error - delaying for {timeSpan.TotalMilliseconds}ms, then making retry {retryCount}. Exception: {exception?.Message}"));
    return sqlTransientRetryPolicy;
}
```

### Configuration
- **Max Retries**: 3 attempts
- **Retry Delay**: 5 seconds between attempts
- **Exception Types**: Handles `SqlException` (transient errors) and `TimeoutException`

### Issues Prevented
- **Transient SQL Server Errors**: Network blips, temporary connection issues, deadlocks
- **Timeout Exceptions**: Long-running queries that exceed timeout limits
- **Connection Pool Exhaustion**: Temporary unavailability of database connections
- **Database Failover**: Brief interruptions during SQL Server failover scenarios

### Usage Areas
SQL retry is applied to critical database operations:
- `UpdateTelegenceFinalUsageFromStaging()`
- `UpdateTelegenceMubuUsageFromStaging()`
- `UpdateMobilityMubuUsageFromTelegence()`
- `UpdateLateMubuUsageFromTelegence()`
- `UpdateTelegenceKafkaUsage()`

## 3. Staging Tables Management

### Initialization Process
The AltaworxTelegenceAWSGetDeviceUsage Lambda clears staging tables at the start through the `InitializeSync()` method:

```csharp
private static void InitializeSync(KeySysLambdaContext context, string dbConnectionString)
{
    // Truncates all usage staging tables
    using (var Cmd = new SqlCommand("usp_Telegence_Truncate_UsageStaging", Conn))
    {
        Cmd.CommandType = CommandType.StoredProcedure;
        Cmd.CommandTimeout = 800;
        Conn.Open();
        Cmd.ExecuteNonQuery();
    }
    
    // Inserts sync tracking record
    using (var cmd = new SqlCommand("insert into [dbo].[TelegenceDeviceUsageLastSyncDate](LastSyncDate, QueueCount) SELECT GETDATE(), 0", conn))
    {
        cmd.ExecuteNonQuery();
    }
}
```

### Staging Tables Used
- **TelegenceAllUsageStaging**: Stores Premier report usage data
- **TelegenceDeviceUsageMubuStaging**: Stores MUBU report data (voice and usage)
- **TelegenceDeviceFinalUsageStaging**: Stores Final usage report data
- **TelegenceDeviceUsageIdsToProcess**: Queue management table

### Clearing Logic
- **At Start**: `usp_Telegence_Truncate_UsageStaging` stored procedure clears all staging tables
- **During Processing**: `TruncateTableByTableName()` method clears specific staging tables after data processing
- **Queue Clearing**: `ClearQueue()` method truncates the `TelegenceDeviceUsageIdsToProcess` table

### Previous Run Cleanup
Yes, staging tables are cleared after the previous run completion. The Lambda follows this pattern:
1. Process data from staging tables
2. Move data to permanent tables via stored procedures
3. Clear staging tables using `TruncateTableByTableName()`
4. Next run starts with clean staging tables

## 4. BAN, FAN, and Number Status Storage

### Storage Location
Based on the code analysis, BAN, FAN, and Number statuses are **NOT** stored in dedicated staging tables. Instead:

- **BAN Status**: Retrieved directly from Telegence API via `GetBanStatusAsync()` method
- **FAN (Foundation Account Number)**: Used as processing parameter, stored in queue management
- **Number Status**: Retrieved through `GetTelegenceDeviceBySubscriberNumber()` API calls

### BAN Status Retrieval
```csharp
public static async Task<string> GetBanStatusAsync(KeySysLambdaContext context, TelegenceAuthentication telegenceAuth, string proxyUrl, string ban, string telegenceBanDetailGetURL)
{
    string banDetailUrl = telegenceBanDetailGetURL.Replace("{ban}", ban);
    // Makes API call to Telegence to get BAN status
    return GetBillingAccountStatus(responseBody);
}
```

### Status Flow
- **BAN List**: Retrieved from `usp_Telegence_Get_BillingAccountsByProviderId` stored procedure
- **BAN Status**: Fetched real-time from Telegence API, not stored in staging
- **Device Status**: Retrieved via API calls during processing

## 5. Telegence API Integration

### API Endpoints Called

#### 1. GetTelegenceDevicesAsync Endpoint
- **Purpose**: Retrieves paginated device lists from Telegence
- **Endpoint Pattern**: Uses `deviceDetailEndpoint` parameter passed to the method
- **Method**: GET request with pagination headers

#### 2. GetTelegenceDeviceBySubscriberNumber Parameters
```csharp
public static async Task<string> GetTelegenceDeviceBySubscriberNumber(
    KeySysLambdaContext context, 
    TelegenceAuthentication telegenceAuthentication,
    bool isProduction, 
    string subscriberNo,     // Subscriber number to query
    string endpoint,         // API endpoint URL
    string proxyUrl)         // Optional proxy URL
```

**Parameters Used:**
- `subscriberNo`: The specific subscriber/device number to query
- `endpoint`: Base API endpoint URL
- `isProduction`: Determines sandbox vs production URL
- Authentication headers: `app-id` and `app-secret`

## 6. Pagination Configuration

### Page Size and Limits
```csharp
// Page size is passed as parameter to GetTelegenceDevicesAsync
headerContent.Add(CommonConstants.CURRENT_PAGE, syncState.CurrentPage.ToString());
headerContent.Add(CommonConstants.PAGE_SIZE, pageSize);
```

- **Page Size**: Configurable parameter passed to `GetTelegenceDevicesAsync()` method
- **Current Page**: Tracked in `syncState.CurrentPage`
- **Default Behavior**: No hardcoded page size limit found in the code

### Pagination Detection
The system determines all pages are processed by:

```csharp
if (int.TryParse(headers[CommonConstants.PAGE_TOTAL].ToString(), out int pageTotal))
{
    syncState.HasMoreData = syncState.CurrentPage < pageTotal;
}
syncState.IsLastCycle = !syncState.HasMoreData;
```

**Detection Mechanism:**
- **PAGE_TOTAL Header**: API returns total page count in response headers
- **HasMoreData Flag**: Set to true when `CurrentPage < PageTotal`
- **IsLastCycle Flag**: Set to true when no more data to process
- **REFRESH_TIMESTAMP**: Used to track data freshness

### Authentication
- **Headers**: `app-id` and `app-secret` from `TelegenceAuthentication` object
- **URL Selection**: Production vs Sandbox based on `isProduction` flag
- **Proxy Support**: Optional proxy URL for network routing

## 7. Device Validation and Failure Handling

### Validation Process
The AltaworxTelegenceAWSGetDeviceUsage Lambda doesn't implement explicit device validation logic. Instead, it handles failures through:

### Failure Handling Mechanisms

#### 1. File Download Failures
```csharp
private void AddToDataRow(DataTable table, UsageFile usageFile, string errorDetail, int serviceProviderId, string reportType)
{
    var dr = table.NewRow();
    dr[1] = usageFile.FilePath;
    dr[2] = "FAILED";
    dr[3] = errorDetail;
    dr[4] = reportType;
    dr[5] = usageFile.WriteTime;
    dr[6] = serviceProviderId;
    dr[7] = "Telegence AWS Get Device Usage Lambda";
}
```

#### 2. Failed Device Processing
**What happens to devices that fail validation:**
- **File Download Failures**: Stored in `TelegenceSFTPFileDownloadStatus` table with "FAILED" status
- **Retry Mechanism**: Failed downloads are tracked and retried in subsequent runs
- **Queue Management**: Failed items are re-queued with `downloadFailedIds` parameter
- **Error Logging**: Detailed error information logged for troubleshooting

#### 3. Retry Process Flow
- **Failure Detection**: Exceptions caught during file processing
- **Error Storage**: Failed items stored in `fileDownloadAgainDt` DataTable
- **Bulk Insert**: Failed records bulk inserted into `TelegenceSFTPFileDownloadStatus`
- **Re-queuing**: Failed IDs sent back to SQS for retry processing

### Recovery Mechanisms
- `ProcessDownloadFileAgain()`: Handles retry of previously failed downloads
- `ProcessDownloadFileNextInstance()`: Processes next batch of files with failure tracking
- `GetFileNamesDownloadFailed()`: Retrieves list of previously failed downloads for retry

## 8. Polly Retry Configuration

### Retry Setup for Telegence API Calls
The AltaworxTelegenceAWSGetDeviceUsage Lambda uses Polly retry policies through the `RetryPolicyHelper` class:

```csharp
var responseMessage = await Amop.Core.Helpers.RetryPolicyHelper.PollyRetryHttpRequestAsync(
    context.logger, 
    CommonConstants.NUMBER_OF_TELEGENCE_RETRIES
).ExecuteAsync(async () =>
{
    using (var client = new HttpClient())
    {
        // HTTP request logic
        return await client.GetAsync(telegenceDevicesGetUrl);
    }
});
```

### Configuration Details
- **Retry Attempts**: Controlled by `CommonConstants.NUMBER_OF_TELEGENCE_RETRIES`
- **Retry Types**:
  - `PollyRetryHttpRequestAsync()`: For direct HTTP requests
  - `PollyRetryForProxyRequestAsync()`: For proxy-routed requests
- **Delay Strategy**: Implemented in the `RetryPolicyHelper` class (not visible in current code)

### SQL Retry Configuration
- **Attempts**: 3 retries (`MaxRetries = 3`)
- **Delay**: 5 seconds between attempts (`RetryDelaySeconds = 5`)
- **Strategy**: Fixed delay with logging

## 9. Re-enqueuing for Incomplete/Timed-out Device Lists

### Re-enqueuing Mechanisms

#### Download Failures:
```csharp
await SendMessageToQueueDownloadAgainAsync(context, serviceProviderId, isFromCloudwatchEvent, REPORT_TYPE_MUBU, downloadFailedIdsString, 0);
```

#### Next Instance Processing:
```csharp
await SendMessageToQueueNextDownloadAsync(context, serviceProviderId, isFromCloudwatchEvent, REPORT_TYPE_MUBU, fileNamesNextDownloadString, writeTimesNextDownloadString, fileDownLoadFailedIds);
```

#### Cleanup Retry:
```csharp
await SendNotificationMessageToQueueAsync(context, currentServiceProviderId);
```

### Re-enqueuing Parameters
- **DelayBetweenRetries**: `SQSMaxDelaySeconds` (900 seconds)
- **MaxRetries**: `DeviceCleanupMaxRetries` (environment variable)
- **Queue URLs**: Uses `DeviceNotificationQueueURL` and `ExportDeviceUsageQueueURL`

### Timeout Handling
- **HTTP Client Timeout**: `CommonConstants.HTTP_CLIENT_REQUEST_TIMEOUT_IN_MINUTES`
- **SQL Command Timeout**: 800 seconds for most operations, 240 seconds for some
- **SQS Delay**: Maximum 900 seconds between retries

## 10. Stored Procedures Flow

### Core Stored Procedures Used

#### 1. Authentication and Configuration
- `usp_Telegence_Get_AuthenticationByProviderId`: Retrieves Telegence API credentials
- `usp_Telegence_Get_BillingAccountsByProviderId`: Gets billing account numbers

#### 2. Data Processing Procedures
- `usp_Telegence_Truncate_UsageStaging`: Clears all staging tables at initialization
- `usp_Telegence_Update_DeviceFinalUsage_FromStaging`: Processes final usage data from staging
- `usp_Telegence_Update_DeviceMubuUsage_FromStaging`: Processes MUBU usage data from staging
- `usp_Telegence_DeviceSync`: Synchronizes device data with mobility systems
- `usp_MobilityDeviceUsage_UpdateLateRecords`: Updates late MUBU usage records

#### 3. Utility Procedures
- `usp_BillingPeriodIsPending`: Checks if billing period is open for updates
- `usp_Telegence_Zero_Usage_For_New_Billing_Cycle`: Zeros out usage for new billing cycles
- `TELEGENCE_UPDATE_DEVICE_KAFKA_USAGE`: Updates Kafka-sourced usage data

### Stored Procedure Execution Flow

#### Standard Processing Flow
1. **Initialization**: `usp_Telegence_Truncate_UsageStaging` - Clear staging tables
2. **Data Loading**: Bulk copy operations to staging tables
3. **Data Processing**: Execute update procedures to move data to permanent tables
4. **Cleanup**: Truncate staging tables after successful processing

#### MUBU Processing Flow
```csharp
// Step 1: Update MUBU usage from staging
sqlRetryPolicy.Execute(() => UpdateTelegenceMubuUsageFromStaging(context, serviceProviderId));

// Step 2: Update mobility data
sqlRetryPolicy.Execute(() => UpdateMobilityMubuUsageFromTelegence(context, serviceProviderId));

// Step 3: Update late records
sqlRetryPolicy.Execute(() => UpdateLateMubuUsageFromTelegence(context, serviceProviderId));
```

### Error Handling in Stored Procedures
- **SQL Retry Policy**: All stored procedure calls wrapped in retry policy
- **Timeout Configuration**: Extended timeouts (800 seconds) for long-running operations
- **Transaction Management**: Uses transaction scopes for data consistency

## 11. Summary Logging Details

### Logging Framework
The AltaworxTelegenceAWSGetDeviceUsage Lambda uses comprehensive logging through the `LogInfo()` method:

```csharp
LogInfo(context, "STATUS", "Usage SQL Bulk Copy Start");
LogInfo(context, "SUB", $"ProcessDailyUsage({serviceProviderId},{fan},{reportType})");
LogInfo(context, "EXCEPTION", ex.Message + " " + ex.StackTrace);
```

### Log Categories and Details Captured

#### 1. Process Flow Logging
- **SUB**: Method entry/exit with parameters
- **STATUS**: Operation status and progress updates
- **INFO**: Informational messages about file counts, processing details

#### 2. Error and Exception Logging
- **EXCEPTION**: Full exception details with stack traces
- **ERROR**: Error conditions and failure details
- **WARN**: Warning conditions and non-critical issues

#### 3. Integration Logging
- **API Calls**: Telegence API request/response logging
- **Database Operations**: SQL operation status and timing
- **File Operations**: FTP download status and file processing

### Summary Log Details Captured

#### Service Provider Processing
```csharp
LogInfo(context, "ServiceProviderId", serviceProviderId);
LogInfo(context, "FAN", fan);
LogInfo(context, "ReportType", reportType);
```

#### File Processing Summary
```csharp
LogInfo(context, "INFO", $"The voice file has {newestVoiceFileList.Count} files need download.");
LogInfo(context, "INFO", $"The data file has {newestDataFileList.Count} files need download.");
```

#### SQL Retry Summary
```csharp
LogInfo(context, "STATUS", $"Encountered transient SQL error - delaying for {timeSpan.TotalMilliseconds}ms, then making retry {retryCount}. Exception: {exception?.Message}");
```

#### API Integration Summary
```csharp
AwsFunctionBase.LogInfo(context, LogTypeConstant.Info, $"GetTelegenceDevicesAsync: {proxyUrl}, {syncState.CurrentPage}");
AwsFunctionBase.LogInfo(context, "GetTelegenceDevicesAsync::TelegenceAPIClientId", telegenceAuth.ClientId);
```

## 12. Reference Items Usage

### Functions and Methods

#### Core Lambda Functions
- `FunctionHandler()`: Main entry point for Lambda execution
- `ProcessEventAsync()`: Processes SQS events or initiates daily processing
- `ProcessEventRecordAsync()`: Handles individual SQS message processing
- `StartDailyDeviceUsageProcessingAsync()`: Initializes daily processing for all providers

#### Data Processing Functions
- `ProcessDailyUsage()`: Main usage data processing method
- `ProcessDownloadFileAgain()`: Retry failed file downloads
- `ProcessDownloadFileNextInstance()`: Process next batch of files
- `ProcessTelegenceMubuUsageDataSync()`: MUBU data synchronization

#### Utility Functions
- `GetSqlRetryPolicy()`: SQL retry policy configuration
- `InitializeSync()`: System initialization and staging table cleanup
- `ClearQueue()`: Queue management table cleanup

### Queues

#### Primary Queues
1. **TelegenceDeviceUsageQueueURL (ExportDeviceUsageQueueURL)**
   - **Purpose**: Main processing queue for device usage operations
   - **Usage**: Receives messages for file downloads, data processing, and retry operations

2. **TelegenceDeviceNotificationQueueURL (DeviceNotificationQueueURL)**
   - **Purpose**: Notification and cleanup operations
   - **Usage**: Handles service provider notifications and cleanup tasks

#### Queue Message Types
- **Service Provider Processing**: Messages with `ServiceProviderId` for specific provider processing
- **File Download Retry**: Messages with `DownloadFailedIds` for retry operations
- **Next Instance Processing**: Messages with `FileNamesNextDownload` and `WriteTimesNextDownload`
- **Sync Data Processing**: Messages with `TelegenceSyncDataStep` for staged processing

### Stored Procedures Reference

#### Authentication Procedures
- `usp_Telegence_Get_AuthenticationByProviderId`: Gets API credentials
- `usp_Telegence_Get_BillingAccountsByProviderId`: Gets billing accounts

#### Data Processing Procedures
- `usp_Telegence_Truncate_UsageStaging`: Staging table cleanup
- `usp_Telegence_Update_DeviceFinalUsage_FromStaging`: Final usage processing
- `usp_Telegence_Update_DeviceMubuUsage_FromStaging`: MUBU usage processing
- `usp_Telegence_DeviceSync`: Device synchronization
- `usp_MobilityDeviceUsage_UpdateLateRecords`: Late record updates

#### Utility Procedures
- `usp_BillingPeriodIsPending`: Billing period validation
- `usp_Telegence_Zero_Usage_For_New_Billing_Cycle`: Usage reset for new cycles
- `TELEGENCE_UPDATE_DEVICE_KAFKA_USAGE`: Kafka data processing

### Database Tables

#### Staging Tables
- `TelegenceAllUsageStaging`: Premier usage data staging
- `TelegenceDeviceUsageMubuStaging`: MUBU data staging
- `TelegenceDeviceFinalUsageStaging`: Final usage data staging

#### Management Tables
- `TelegenceDeviceUsageIdsToProcess`: Processing queue management
- `TelegenceDeviceUsageLastSyncDate`: Sync tracking
- `TelegenceSFTPFileDownloadStatus`: File download status tracking

### Environment Variables

#### Queue Configuration
- `TelegenceDeviceUsageQueueURL`: Main processing queue URL
- `TelegenceDeviceNotificationQueueURL`: Notification queue URL

#### Processing Configuration
- `DeviceCleanupMaxRetries`: Maximum retry attempts for cleanup
- `DaysToKeep`: Data retention period
- `FtpReportNotificationThresholdDays`: FTP notification threshold
- `CheckFilesMissedThresholdDays`: File check threshold
- `LimitAmountFilePerRunTimes`: File processing limit per run
- `PremiereReportDelayDays`: Premiere report processing delay
- `MUBURowsCountLimit`: MUBU processing row limit

#### Simulation Configuration
- `IsPremiereReportDelaySimulator`: Premiere delay simulation flag
- `DayEndBillingSimulator`: Billing simulation day configuration

---

## Conclusion

The AltaworxTelegenceAWSGetDeviceUsage Lambda is a sophisticated data processing system that handles complex workflows involving API integration, file processing, database operations, and error recovery. Its robust retry mechanisms, comprehensive logging, and staged processing approach ensure reliable handling of Telegence device usage data for AT&T providers.