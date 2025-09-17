# AltaworxTelegenceAWSGetDeviceDetails Lambda - Failed Device Skip Processing Logic

## Overview
This document provides a detailed analysis of how the AltaworxTelegenceAWSGetDeviceDetails Lambda function handles failed devices and continues processing with remaining subscribers.

## Processing Flow Summary

The Lambda function processes devices in batches and implements a robust error handling mechanism that:
1. **Automatically removes failed subscribers** from the processing queue
2. **Handles retries** using Polly policies 
3. **Logs all failures** with full exception details
4. **Skips failed subscribers** and continues processing remaining subscribers

## Detailed Processing Logic with Code Evidence

### 1. Main Processing Loop

The core processing happens in the `ProcessDetailListAsync` method which iterates through subscriber numbers:

```csharp
// Lines 279-319 in AltaworxTelegenceAWSGetDeviceDetails.cs
foreach (var subscriberNumber in subscriberNumbers)
{
    var remainingTime = context.Context.RemainingTime.TotalSeconds;
    if (remainingTime > CommonConstants.REMAINING_TIME_CUT_OFF)
    {
        try
        {
            // Process individual device
            LogInfo(context, CommonConstants.INFO, $"Get Detail for Subscriber Number : {subscriberNumber}");
            string deviceDetailUrl = string.Format(TelegenceDeviceDetailGetURL, subscriberNumber);
            var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
            var deviceDetail = apiResult?.ResponseObject?.TelegenceDeviceDetailResponse;
            
            // Success path - add to staging tables
            if (deviceDetail != null && !string.IsNullOrWhiteSpace(deviceDetail.SubscriberNumber))
            {
                table.AddRow(deviceDetail, serviceProviderId);
                // Add features to staging table
                foreach (var feature in GetDeviceOfferingCodes(deviceDetail))
                {
                    featureStagingTable.AddRow(subscriberNumber, feature);
                }
            }
            else
            {
                // Failed response - remove from queue
                LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
                sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
            }
        }
        catch (Exception e)
        {
            // Exception handling - log error and remove from queue
            LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");
            LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
            sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
            continue;  // SKIP TO NEXT SUBSCRIBER
        }
    }
    else
    {
        // Time cutoff reached - break out of loop
        LogInfo(context, CommonConstants.INFO, $"Remaining run time ({remainingTime} seconds) is not enough to continue.");
        break;
    }
}
```

### 2. Critical Skip Logic - The `continue` Statement

**Key Code Evidence:**
```csharp
// Lines 306-312 in AltaworxTelegenceAWSGetDeviceDetails.cs
catch (Exception e)
{
    LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");
    LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
    sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
    continue;  // ← THIS IS THE SKIP MECHANISM
}
```

**How it works:**
- When any exception occurs during device processing, the `continue` statement immediately skips to the next iteration of the `foreach` loop
- This means processing continues with the next subscriber in the batch
- The failed subscriber is removed from the queue but processing doesn't stop

### 3. Failed Device Removal Logic

The `RemoveDeviceFromQueue` method marks failed devices as deleted:

```csharp
// Lines 369-386 in AltaworxTelegenceAWSGetDeviceDetails.cs
private static void RemoveDeviceFromQueue(string connectionString, int groupNumber, string subscriberNumber)
{
    using (var con = new SqlConnection(connectionString))
    {
        using (var cmd = new SqlCommand(
            @"UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
                            SET [IsDeleted] = 1
                            WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber", con))
        {
            cmd.CommandType = CommandType.Text;
            con.Open();
            cmd.Parameters.AddWithValue("@GroupNumber", groupNumber);
            cmd.Parameters.AddWithValue("@SubscriberNumber", subscriberNumber);
            cmd.CommandTimeout = 800;
            cmd.ExecuteNonQuery();
        }
    }
}
```

**Key Points:**
- Failed devices are marked as `IsDeleted = 1` in the `TelegenceDeviceDetailIdsToProcess` table
- This prevents them from being processed again in future iterations
- Uses SQL retry policy for resilient database operations

### 4. Retry Logic Implementation

The function uses Polly retry policies at multiple levels:

#### HTTP API Retries
```csharp
// Lines 395-400 in TelegenceAPIClient.cs
var response = await RetryPolicyHelper.PollyRetryHttpRequestAsync(_logger, retryNumber).ExecuteAsync(async () =>
{
    var request = _httpRequestFactory.BuildRequestMessage(_authentication, new HttpMethod(CommonConstants.METHOD_GET), requestUri,
        new Dictionary<string, string> { { CommonConstants.ACCEPT, CommonConstants.APPLICATION_JSON }, { CommonConstants.APP_ID, _authentication.ClientId }, { CommonConstants.APP_SECRET, _authentication.ClientSecret } });
    return await _httpClientFactory.GetClient().SendAsync(request);
});
```

#### SQL Retry Policy
```csharp
// Lines 415-425 in AltaworxTelegenceAWSGetDeviceDetails.cs
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

**Retry Configuration:**
- `MaxRetries = 5` (Line 37)
- `RetryDelaySeconds = 5` (Line 38)
- Handles `SqlException` and `TimeoutException`

### 5. Processing Continuation Mechanism

After processing individual subscribers (with skips), the function:

1. **Bulk loads successful results:**
```csharp
// Lines 322-329 in AltaworxTelegenceAWSGetDeviceDetails.cs
if (table.HasRows())
{
    LogInfo(context, "INFO", $"Start Bulk Copy TelegenceDeviceDetailSyncTable");
    SqlBulkCopy(context, context.CentralDbConnectionString, table.DataTable, table.DataTable.TableName);

    LogInfo(context, "INFO", $"Start execute RemoveStagedDevices");
    sqlRetryPolicy.Execute(() => RemoveStagedDevices(context, groupNumber));
}
```

2. **Queues next batch for processing:**
```csharp
// Line 338 in AltaworxTelegenceAWSGetDeviceDetails.cs
await SendProcessMessageToQueueAsync(context, groupNumber);
```

### 6. Error Scenarios and Handling

| Scenario | Code Location | Action Taken | Processing Continues? |
|----------|---------------|--------------|----------------------|
| **API Call Fails** | Lines 306-312 | Log exception → Remove from queue → `continue` | ✅ Yes |
| **Null/Empty Response** | Lines 300-304 | Log info → Remove from queue | ✅ Yes |
| **SQL Transient Error** | Lines 303, 310 | Retry with Polly policy | ✅ Yes (after retries) |
| **Timeout/Lambda Time Limit** | Lines 314-318 | Break loop gracefully | ✅ Yes (next invocation) |
| **JSON Deserialization Error** | TelegenceAPIClient | Handled in retry policy | ✅ Yes (after retries) |

### 7. Logging and Observability

The function provides comprehensive logging for failed devices:

```csharp
// Exception logging with full details
LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");

// Status logging for queue operations
LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");

// Processing status logging
LogInfo(context, CommonConstants.INFO, $"Get Detail for Subscriber Number : {subscriberNumber}");
```

## Summary

The AltaworxTelegenceAWSGetDeviceDetails Lambda implements a robust **"fail-fast, continue-processing"** pattern:

1. **Individual device failures are isolated** - they don't affect other devices in the batch
2. **Failed devices are automatically removed** from future processing queues via database updates
3. **The `continue` statement ensures immediate skip** to the next subscriber when exceptions occur
4. **Retry policies handle transient failures** at both HTTP and SQL levels
5. **Comprehensive logging captures all failure scenarios** for debugging and monitoring
6. **Processing always continues** until the batch is complete or time limits are reached

This design ensures maximum throughput and fault tolerance - if 1 device out of 100 fails, the other 99 still get processed successfully.