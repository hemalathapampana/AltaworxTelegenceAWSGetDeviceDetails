# AltaworxTelegenceAWSDeviceDetails - SQL Retry and Staging Table Analysis

## Overview
This document analyzes the SQL retry logic and staging table clearing mechanisms in the `AltaworxTelegenceAWSDeviceDetails` Lambda function.

## Why SQL Retry is Done First

### 1. **SQL Retry Policy Implementation**
The SQL retry is implemented at the beginning of critical database operations to handle transient failures:

```csharp
// Line 225 in AltaworxTelegenceAWSGetDeviceDetails.cs
var sqlRetryPolicy = GetSqlRetryPolicy(context);

// Lines 415-425: SQL Retry Policy Definition
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

### 2. **Configuration Constants**
```csharp
// Lines 37-38
private const int MaxRetries = 5;
private const int RetryDelaySeconds = 5;
```

### 3. **Issues Prevented by SQL Retry**
The SQL retry mechanism prevents:

- **Transient SQL Connection Failures**: Network hiccups, temporary database unavailability
- **Timeout Exceptions**: Long-running queries that occasionally timeout
- **Deadlock Situations**: Temporary database locking conflicts
- **Resource Contention**: High database load causing temporary failures
- **Network Instability**: Brief network connectivity issues between Lambda and SQL Server

### 4. **Applied Retry Locations**
The retry policy is applied to critical operations:

```csharp
// Line 228: Getting subscriber numbers from database
sqlRetryPolicy.Execute(() => {
    // Database query to get subscriber numbers
});

// Line 303: Removing devices from queue on success
sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));

// Line 310: Removing devices from queue on error
sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));

// Line 328: Removing staged devices after processing
sqlRetryPolicy.Execute(() => RemoveStagedDevices(context, groupNumber));
```

## Staging Tables Clearing

### 1. **Device Staging Table Clearing - YES**
The `TelegenceDeviceMobilityFeature_Staging` table is cleared at the start of processing:

```sql
-- Line 402 in RemovePreviouslyStagedFeatures method
TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]
```

**When it's called:**
```csharp
// Lines 175-177: Called during initialization
LogInfo(context, "INFO", "Start execute RemovePreviouslyStagedFeatures");
RemovePreviouslyStagedFeatures(context);
LogInfo(context, "INFO", "End execute RemovePreviouslyStagedFeatures");
```

### 2. **BAN (Billing Account Number) Staging Table Clearing - YES**
From the related `AltaworxTelegenceAWSGetDevices.cs`, BAN staging tables are also cleared:

```sql
-- Called via stored procedure
usp_Telegence_Truncate_BillingAccountNumberStatusStaging
```

### 3. **Device and Usage Staging Clearing**
```sql
-- Called via stored procedure in AltaworxTelegenceAWSGetDevices.cs
usp_Telegence_Truncate_DeviceAndUsageStaging
```

## Key SQL Queries for AltaworxTelegenceAWSDeviceDetails

### 1. **Initial Setup Query**
```sql
-- Line 135: Stored procedure to filter devices for detail processing
EXEC usp_Telegence_Devices_GetDetailFilter @BatchSize = [BatchSize]
```

### 2. **Get Group Count Query**
```sql
-- Lines 153-154: Get maximum group number for processing
SELECT MAX(GroupNumber) 
FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) 
WHERE IsDeleted = 0
```

### 3. **Get Subscriber Numbers Query**
```sql
-- Lines 232-236: Get subscriber numbers to process
SELECT TOP [BatchSize] [SubscriberNumber], [ServiceProviderId] 
FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) 
WHERE GroupNumber = @GroupNumber AND [IsDeleted] = 0
```

### 4. **Remove Staged Devices Query**
```sql
-- Lines 345-348: Update processed devices as deleted
UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
SET [IsDeleted] = 1
WHERE GroupNumber = @GroupNumber 
    AND SubscriberNumber IN (SELECT SubscriberNumber FROM [dbo].[TelegenceDeviceDetailStaging])
```

### 5. **Remove Individual Device Query**
```sql
-- Lines 374-376: Remove specific device from processing queue
UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
SET [IsDeleted] = 1
WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber
```

### 6. **Clear Feature Staging Query**
```sql
-- Line 402: Clear previous feature staging data
TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]
```

## Processing Flow

1. **Initialization Phase**:
   - Clear `TelegenceDeviceMobilityFeature_Staging` table
   - Call `usp_Telegence_Devices_GetDetailFilter` to prepare devices for processing
   - Get group count for batch processing

2. **Processing Phase**:
   - Apply SQL retry policy to all database operations
   - Get subscriber numbers in batches
   - Process device details via Telegence API
   - Bulk copy results to `TelegenceDeviceDetailStaging`
   - Remove processed devices from queue

3. **Error Handling**:
   - Retry transient SQL failures up to 5 times with 5-second delays
   - Log retry attempts and failures
   - Continue processing other devices if individual failures occur

## Benefits of This Approach

1. **Resilience**: SQL retry prevents temporary failures from stopping the entire process
2. **Data Consistency**: Staging table clearing ensures clean state for each run
3. **Performance**: Batch processing and bulk copy operations optimize throughput
4. **Observability**: Comprehensive logging of retry attempts and failures
5. **Recovery**: Failed devices are properly removed from processing queue