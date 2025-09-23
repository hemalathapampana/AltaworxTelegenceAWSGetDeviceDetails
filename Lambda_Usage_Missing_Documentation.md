# Lambda Usage — Missing Documentation

This document addresses the missing documentation areas identified in the TelegenceGetDevices Lambda usage analysis.

## 1. Continuation Mechanics: Multi-Run Attributes

### Overview
The TelegenceGetDevices Lambda implements sophisticated continuation mechanics to handle large-scale data synchronization across multiple Lambda invocations, ensuring no data loss and maintaining processing state.

### Key Continuation Attributes

#### IsDownloadNextInstance
```csharp
// Message attribute for continuation control
{"IsDownloadNextInstance", new MessageAttributeValue {DataType = "String", StringValue = "true"}}
```

**Purpose**: Controls whether the Lambda should continue downloading the next batch of data in subsequent invocations.

**Usage Scenarios**:
- **True**: Indicates more data is available and Lambda should re-enqueue itself for continuation
- **False**: Signals completion of current processing cycle
- **Timeout Handling**: Automatically set to true when Lambda approaches execution time limits

#### HasMoreData
```csharp
// Pagination continuation flag
syncState.HasMoreData = syncState.CurrentPage < pageTotal;
{"HasMoreData", new MessageAttributeValue {DataType = "String", StringValue = syncState.HasMoreData ? "true" : "false"}}
```

**Purpose**: Tracks API pagination state across Lambda invocations.

**Implementation**:
- Calculated based on current page vs total pages from API response
- Preserved in SQS message attributes for state continuity
- Used to determine if additional API calls are needed

#### CurrentPage & PageTotal
```csharp
// Page tracking for API pagination
{"CurrentPage", new MessageAttributeValue {DataType = "String", StringValue = syncState.CurrentPage.ToString()}}

// API response header processing
if (int.TryParse(headers[CommonConstants.PAGE_TOTAL].ToString(), out int pageTotal))
{
    syncState.HasMoreData = syncState.CurrentPage < pageTotal;
}
```

**Purpose**: Maintains pagination state across multiple Lambda executions.

**State Management**:
- `CurrentPage`: Tracks the current API page being processed
- `PageTotal`: Total pages available from API (received in response headers)
- Automatically incremented with each successful API call

#### RetryNumber
```csharp
// Retry attempt tracking
{"RetryNumber", new MessageAttributeValue {DataType = "String", StringValue = syncState.RetryNumber.ToString()}}

// Retry limit enforcement
if (syncState.RetryNumber <= CommonConstants.NUMBER_OF_TELEGENCE_LAMBDA_RETRIES)
{
    await SendMessageToGetDevicesQueueAsync(context, syncState, TelegenceDestinationQueueGetDevicesURL, delaySeconds: CommonConstants.DELAY_IN_SECONDS_FIVE_SECONDS);
}
```

**Purpose**: Prevents infinite retry loops while allowing recovery from transient failures.

**Configuration**:
- Maximum retries: `NUMBER_OF_TELEGENCE_LAMBDA_RETRIES = 5`
- Retry delay: `DELAY_IN_SECONDS_FIVE_SECONDS = 5`
- Incremented with each retry attempt

### Multi-Run Continuation Flow

#### 1. Initial Invocation
```csharp
// First run initialization
syncState.CurrentPage = 1;
syncState.HasMoreData = true;
syncState.RetryNumber = 0;
syncState.InitializeProcessing = true;
```

#### 2. Timeout Detection
```csharp
// Time remaining check
var remainingTime = context.RemainingTime;
if (remainingTime.TotalSeconds <= CommonConstants.REMAINING_TIME_CUT_OFF)
{
    // Prepare for continuation
    await SendMessageToGetDevicesQueueAsync(context, syncState, TelegenceDestinationQueueGetDevicesURL);
}
```

#### 3. State Preservation
```csharp
// Complete state preservation in SQS message
MessageAttributes = new Dictionary<string, MessageAttributeValue>
{
    {"HasMoreData", new MessageAttributeValue {DataType = "String", StringValue = syncState.HasMoreData ? "true" : "false"}},
    {"CurrentPage", new MessageAttributeValue {DataType = "String", StringValue = syncState.CurrentPage.ToString()}},
    {"CurrentServiceProviderId", new MessageAttributeValue {DataType = "String", StringValue = syncState.CurrentServiceProviderId.ToString()}},
    {"InitializeProcessing", new MessageAttributeValue {DataType = "String", StringValue = syncState.InitializeProcessing.ToString()}},
    {"IsProcessDeviceNotExistsStaging", new MessageAttributeValue {DataType = "String", StringValue = isProcessDeviceNotExistsStaging.ToString()}},
    {"GroupNumber", new MessageAttributeValue {DataType = "String", StringValue = groupNumber.ToString()}},
    {"IsLastProcessDeviceNotExistsStaging", new MessageAttributeValue {DataType = "String", StringValue = isLastGroup.ToString()}},
    {"RetryNumber", new MessageAttributeValue {DataType = "String", StringValue = syncState.RetryNumber.ToString()}}
}
```

#### 4. Continuation Recovery
```csharp
// State restoration from SQS message
if (sqsEvent?.Records != null && sqsEvent.Records.Count > 0)
{
    var record = sqsEvent.Records[0];
    if (record.MessageAttributes.ContainsKey("HasMoreData"))
    {
        syncState.HasMoreData = bool.Parse(record.MessageAttributes["HasMoreData"].StringValue);
    }
    if (record.MessageAttributes.ContainsKey("CurrentPage"))
    {
        syncState.CurrentPage = int.Parse(record.MessageAttributes["CurrentPage"].StringValue);
    }
    // ... additional state restoration
}
```

### Continuation Triggers

1. **Lambda Timeout Approaching**: When remaining execution time falls below 180 seconds
2. **API Pagination**: When more pages are available (`HasMoreData = true`)
3. **Processing Incomplete**: When device processing groups remain unfinished
4. **Transient Failures**: When recoverable errors occur within retry limits

---

## 2. MUBU Multi-Step Sync: TelegenceSyncDataStep Chaining

### Overview
MUBU (Mobile Usage Billing Unit) synchronization involves a multi-step process that chains data processing steps through the TelegenceSyncDataStep mechanism, ensuring comprehensive device usage data collection and processing.

### TelegenceSyncDataStep Architecture

#### Step Definition Structure
```csharp
public class TelegenceSyncDataStep
{
    public int StepId { get; set; }
    public string StepName { get; set; }
    public string StepType { get; set; }
    public int ServiceProviderId { get; set; }
    public DateTime ScheduledTime { get; set; }
    public string Status { get; set; }
    public string DependsOnStep { get; set; }
    public Dictionary<string, object> StepParameters { get; set; }
}
```

### Multi-Step Sync Chain

#### Step 1: Device List Synchronization
```csharp
// Initial device list retrieval
var deviceSyncStep = new TelegenceSyncDataStep
{
    StepId = 1,
    StepName = "DeviceListSync",
    StepType = "TelegenceGetDevices",
    ServiceProviderId = syncState.CurrentServiceProviderId,
    Status = "InProgress",
    DependsOnStep = null, // Root step
    StepParameters = new Dictionary<string, object>
    {
        {"BatchSize", 250},
        {"CurrentPage", 1},
        {"InitializeProcessing", true}
    }
};
```

#### Step 2: Device Detail Enrichment
```csharp
// Device detail retrieval step
var deviceDetailStep = new TelegenceSyncDataStep
{
    StepId = 2,
    StepName = "DeviceDetailSync",
    StepType = "TelegenceGetDeviceDetail",
    ServiceProviderId = syncState.CurrentServiceProviderId,
    Status = "Pending",
    DependsOnStep = "DeviceListSync", // Depends on Step 1
    StepParameters = new Dictionary<string, object>
    {
        {"ProcessDeviceNotExistsStaging", true},
        {"GroupNumber", groupNumber}
    }
};
```

#### Step 3: MUBU Usage Data Collection
```csharp
// MUBU usage data synchronization
var mubuUsageStep = new TelegenceSyncDataStep
{
    StepId = 3,
    StepName = "MUBUUsageSync",
    StepType = "TelegenceGetDeviceUsage",
    ServiceProviderId = syncState.CurrentServiceProviderId,
    Status = "Pending",
    DependsOnStep = "DeviceDetailSync", // Depends on Step 2
    StepParameters = new Dictionary<string, object>
    {
        {"UsageType", "MUBU"},
        {"BillingPeriod", telegenceAuth.BillPeriodEndDay},
        {"IncludeDetailedUsage", true}
    }
};
```

#### Step 4: Usage Data Processing
```csharp
// Usage data processing and staging
var usageProcessingStep = new TelegenceSyncDataStep
{
    StepId = 4,
    StepName = "UsageDataProcessing",
    StepType = "TelegenceProcessUsage",
    ServiceProviderId = syncState.CurrentServiceProviderId,
    Status = "Pending",
    DependsOnStep = "MUBUUsageSync", // Depends on Step 3
    StepParameters = new Dictionary<string, object>
    {
        {"StagingTable", "TelegenceDeviceUsageMubuStaging"},
        {"ProcessingMode", "Incremental"},
        {"ValidateUsageData", true}
    }
};
```

### Step Chaining Implementation

#### Step Execution Engine
```csharp
public async Task ExecuteNextStep(TelegenceSyncDataStep currentStep, KeySysLambdaContext context)
{
    // Mark current step as completed
    await UpdateStepStatus(currentStep.StepId, "Completed");
    
    // Find dependent steps
    var nextSteps = await GetDependentSteps(currentStep.StepName);
    
    foreach (var nextStep in nextSteps)
    {
        if (await AreAllDependenciesCompleted(nextStep))
        {
            // Trigger next step execution
            await TriggerStepExecution(nextStep, context);
        }
    }
}
```

#### Dependency Resolution
```csharp
private async Task<bool> AreAllDependenciesCompleted(TelegenceSyncDataStep step)
{
    if (string.IsNullOrEmpty(step.DependsOnStep))
        return true; // Root step, no dependencies
    
    var dependencies = step.DependsOnStep.Split(',');
    foreach (var dependency in dependencies)
    {
        var dependencyStep = await GetStepByName(dependency.Trim());
        if (dependencyStep.Status != "Completed")
            return false;
    }
    return true;
}
```

### MUBU-Specific Processing

#### MUBU Data Structure
```csharp
public class MUBUUsageData
{
    public string SubscriberNumber { get; set; }
    public DateTime UsageDate { get; set; }
    public decimal DataUsageMB { get; set; }
    public decimal VoiceUsageMinutes { get; set; }
    public decimal SMSUsageCount { get; set; }
    public string BillingAccountNumber { get; set; }
    public string UsageType { get; set; } // "MUBU"
    public decimal ChargeAmount { get; set; }
}
```

#### MUBU Staging Table Population
```csharp
// Populate MUBU staging table
private async Task PopulateMUBUStagingTable(List<MUBUUsageData> mubuData, int serviceProviderId)
{
    var stagingTable = new DataTable("TelegenceDeviceUsageMubuStaging");
    
    // Add columns for MUBU data
    stagingTable.Columns.Add("SubscriberNumber", typeof(string));
    stagingTable.Columns.Add("UsageDate", typeof(DateTime));
    stagingTable.Columns.Add("DataUsageMB", typeof(decimal));
    stagingTable.Columns.Add("VoiceUsageMinutes", typeof(decimal));
    stagingTable.Columns.Add("SMSUsageCount", typeof(decimal));
    stagingTable.Columns.Add("BillingAccountNumber", typeof(string));
    stagingTable.Columns.Add("ChargeAmount", typeof(decimal));
    stagingTable.Columns.Add("ServiceProviderId", typeof(int));
    stagingTable.Columns.Add("CreatedDate", typeof(DateTime));
    
    foreach (var usage in mubuData)
    {
        var row = stagingTable.NewRow();
        row["SubscriberNumber"] = usage.SubscriberNumber;
        row["UsageDate"] = usage.UsageDate;
        row["DataUsageMB"] = usage.DataUsageMB;
        row["VoiceUsageMinutes"] = usage.VoiceUsageMinutes;
        row["SMSUsageCount"] = usage.SMSUsageCount;
        row["BillingAccountNumber"] = usage.BillingAccountNumber;
        row["ChargeAmount"] = usage.ChargeAmount;
        row["ServiceProviderId"] = serviceProviderId;
        row["CreatedDate"] = DateTime.UtcNow;
        stagingTable.Rows.Add(row);
    }
    
    // Bulk insert to staging table
    await BulkInsertToStaging(stagingTable, "TelegenceDeviceUsageMubuStaging");
}
```

### Step Monitoring and Error Handling

#### Step Status Tracking
```csharp
public enum StepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled,
    Retrying
}
```

#### Error Recovery in Step Chain
```csharp
private async Task HandleStepFailure(TelegenceSyncDataStep failedStep, Exception error)
{
    // Log the failure
    LogError($"Step {failedStep.StepName} failed: {error.Message}");
    
    // Determine if step can be retried
    if (failedStep.RetryCount < MAX_STEP_RETRIES)
    {
        failedStep.Status = "Retrying";
        failedStep.RetryCount++;
        failedStep.ScheduledTime = DateTime.UtcNow.AddMinutes(RETRY_DELAY_MINUTES);
        
        // Re-queue the step for retry
        await ScheduleStepExecution(failedStep);
    }
    else
    {
        // Mark as failed and handle dependent steps
        failedStep.Status = "Failed";
        await HandleDependentStepsOnFailure(failedStep);
    }
}
```

---

## 3. Blank Files: Special Notification Handling

### Overview
The Lambda implements specialized handling for blank or empty files encountered during synchronization, including detection, notification, and recovery mechanisms.

### Blank File Detection

#### File Content Validation
```csharp
private bool IsFileBlankOrEmpty(string fileContent)
{
    if (string.IsNullOrWhiteSpace(fileContent))
        return true;
    
    // Check for files with only whitespace or minimal content
    var trimmedContent = fileContent.Trim();
    if (trimmedContent.Length == 0)
        return true;
    
    // Check for files with only structural elements (JSON/XML)
    if (IsOnlyStructuralContent(trimmedContent))
        return true;
    
    return false;
}

private bool IsOnlyStructuralContent(string content)
{
    // JSON empty array or object
    if (content == "[]" || content == "{}")
        return true;
    
    // XML with no data elements
    if (content.StartsWith("<?xml") && !ContainsDataElements(content))
        return true;
    
    return false;
}
```

#### API Response Validation
```csharp
private async Task<bool> ValidateAPIResponse(string responseBody, string endpoint)
{
    if (IsFileBlankOrEmpty(responseBody))
    {
        // Log blank file detection
        LogWarning($"Blank or empty response detected from endpoint: {endpoint}");
        
        // Trigger blank file notification
        await TriggerBlankFileNotification(endpoint, responseBody);
        
        return false;
    }
    
    return true;
}
```

### Blank File Notification System

#### Notification Message Structure
```csharp
public class BlankFileNotification
{
    public string NotificationType { get; set; } = "BlankFile";
    public string Endpoint { get; set; }
    public string ServiceProvider { get; set; }
    public DateTime DetectedAt { get; set; }
    public string FileContent { get; set; }
    public int ContentLength { get; set; }
    public string ProcessingContext { get; set; }
    public BlankFileMetadata Metadata { get; set; }
}

public class BlankFileMetadata
{
    public string ExpectedContentType { get; set; }
    public int ExpectedMinimumSize { get; set; }
    public string LastSuccessfulSync { get; set; }
    public int ConsecutiveBlankFiles { get; set; }
}
```

#### Notification Trigger Implementation
```csharp
private async Task TriggerBlankFileNotification(string endpoint, string responseBody)
{
    var notification = new BlankFileNotification
    {
        Endpoint = endpoint,
        ServiceProvider = syncState.CurrentServiceProviderId.ToString(),
        DetectedAt = DateTime.UtcNow,
        FileContent = responseBody?.Substring(0, Math.Min(responseBody.Length, 500)), // First 500 chars
        ContentLength = responseBody?.Length ?? 0,
        ProcessingContext = "TelegenceGetDevices",
        Metadata = new BlankFileMetadata
        {
            ExpectedContentType = "application/json",
            ExpectedMinimumSize = 100, // Minimum expected response size
            LastSuccessfulSync = await GetLastSuccessfulSyncTime(),
            ConsecutiveBlankFiles = await GetConsecutiveBlankFileCount(endpoint)
        }
    };
    
    // Send to notification queue
    await SendNotificationMessage(notification, TelegenceDeviceNotificationQueueURL);
}
```

### Special Handling Mechanisms

#### Blank File Recovery Strategy
```csharp
private async Task<string> HandleBlankFileRecovery(string endpoint, int attemptNumber)
{
    // Wait before retry (exponential backoff)
    var delaySeconds = Math.Pow(2, attemptNumber) * 5; // 5, 10, 20, 40 seconds
    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
    
    // Retry the API call
    var retryResponse = await RetryAPICall(endpoint);
    
    if (!IsFileBlankOrEmpty(retryResponse))
    {
        // Log successful recovery
        LogInfo($"Blank file recovery successful for endpoint: {endpoint} after {attemptNumber} attempts");
        
        // Send recovery notification
        await SendBlankFileRecoveryNotification(endpoint, attemptNumber);
        
        return retryResponse;
    }
    
    return null; // Recovery failed
}
```

#### Consecutive Blank File Tracking
```csharp
private async Task<int> TrackConsecutiveBlankFiles(string endpoint)
{
    var key = $"BlankFileCount_{endpoint}_{syncState.CurrentServiceProviderId}";
    
    // Get current count from cache/database
    var currentCount = await GetBlankFileCount(key);
    currentCount++;
    
    // Update count
    await UpdateBlankFileCount(key, currentCount);
    
    // Check if threshold exceeded
    if (currentCount >= BLANK_FILE_THRESHOLD)
    {
        await TriggerBlankFileThresholdAlert(endpoint, currentCount);
    }
    
    return currentCount;
}
```

### Notification Alert Types

#### 1. Immediate Blank File Alert
```csharp
private async Task SendImmediateBlankFileAlert(BlankFileNotification notification)
{
    var alertMessage = new
    {
        AlertType = "ImmediateBlankFile",
        Severity = "Warning",
        Message = $"Blank file detected from {notification.Endpoint}",
        Details = notification,
        Timestamp = DateTime.UtcNow,
        RequiresAction = false
    };
    
    await SendToNotificationQueue(alertMessage);
}
```

#### 2. Threshold Exceeded Alert
```csharp
private async Task TriggerBlankFileThresholdAlert(string endpoint, int consecutiveCount)
{
    var thresholdAlert = new
    {
        AlertType = "BlankFileThresholdExceeded",
        Severity = "Critical",
        Message = $"Consecutive blank files threshold exceeded for {endpoint}",
        ConsecutiveCount = consecutiveCount,
        Threshold = BLANK_FILE_THRESHOLD,
        Endpoint = endpoint,
        ServiceProviderId = syncState.CurrentServiceProviderId,
        Timestamp = DateTime.UtcNow,
        RequiresAction = true,
        SuggestedActions = new[]
        {
            "Check API endpoint availability",
            "Verify authentication credentials",
            "Review service provider configuration",
            "Contact API provider support"
        }
    };
    
    await SendToNotificationQueue(thresholdAlert);
}
```

#### 3. Recovery Success Notification
```csharp
private async Task SendBlankFileRecoveryNotification(string endpoint, int attemptNumber)
{
    var recoveryNotification = new
    {
        AlertType = "BlankFileRecovery",
        Severity = "Info",
        Message = $"Blank file issue resolved for {endpoint}",
        RecoveryAttempts = attemptNumber,
        Endpoint = endpoint,
        ServiceProviderId = syncState.CurrentServiceProviderId,
        Timestamp = DateTime.UtcNow,
        RequiresAction = false
    };
    
    await SendToNotificationQueue(recoveryNotification);
    
    // Reset consecutive blank file counter
    await ResetBlankFileCount(endpoint);
}
```

### Configuration Constants

```csharp
// Blank file handling configuration
private const int BLANK_FILE_THRESHOLD = 3; // Consecutive blank files before alert
private const int MAX_BLANK_FILE_RETRIES = 3; // Maximum retry attempts
private const int BLANK_FILE_RETRY_DELAY_BASE = 5; // Base delay in seconds
private const int BLANK_FILE_CONTENT_LOG_LIMIT = 500; // Max chars to log
```

---

## 4. Initialization Cleanup: Old Queue Entries Clearing

### Overview
The Lambda implements comprehensive initialization cleanup procedures to ensure clean processing state by clearing old queue entries, stale staging data, and orphaned processing records.

### Queue Entry Cleanup Process

#### SQS Queue Purging
```csharp
private async Task ClearOldQueueEntries(KeySysLambdaContext context)
{
    try
    {
        // Clear self-continuation queue
        await PurgeQueue(TelegenceDestinationQueueGetDevicesURL, "TelegenceGetDevices continuation queue");
        
        // Clear device usage queue if initialization
        if (syncState.InitializeProcessing)
        {
            await PurgeQueue(TelegenceDeviceUsageQueueURL, "TelegenceDeviceUsage queue");
        }
        
        // Clear device detail queue
        await PurgeQueue(TelegenceDeviceDetailQueueURL, "TelegenceDeviceDetail queue");
        
        // Clear notification queue of old processing messages
        await ClearOldNotificationMessages(TelegenceDeviceNotificationQueueURL);
        
        LogInfo(context, "Queue cleanup completed successfully");
    }
    catch (Exception ex)
    {
        LogError(context, $"Queue cleanup failed: {ex.Message}");
        // Continue processing - cleanup failure shouldn't stop sync
    }
}
```

#### Individual Queue Purging
```csharp
private async Task PurgeQueue(string queueUrl, string queueName)
{
    try
    {
        using (var sqsClient = new AmazonSQSClient())
        {
            // Get queue attributes to check message count
            var attributesRequest = new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string> { "ApproximateNumberOfMessages" }
            };
            
            var attributesResponse = await sqsClient.GetQueueAttributesAsync(attributesRequest);
            var messageCount = int.Parse(attributesResponse.Attributes["ApproximateNumberOfMessages"]);
            
            if (messageCount > 0)
            {
                LogInfo($"Purging {messageCount} messages from {queueName}");
                
                // Purge the queue
                var purgeRequest = new PurgeQueueRequest
                {
                    QueueUrl = queueUrl
                };
                
                await sqsClient.PurgeQueueAsync(purgeRequest);
                
                LogInfo($"Successfully purged {queueName}");
            }
            else
            {
                LogInfo($"{queueName} is already empty");
            }
        }
    }
    catch (Exception ex)
    {
        LogError($"Failed to purge {queueName}: {ex.Message}");
        throw;
    }
}
```

### Staging Table Cleanup

#### Comprehensive Staging Cleanup
```csharp
private void ClearAllStagingTables(KeySysLambdaContext context)
{
    try
    {
        // Clear device-related staging tables
        TruncateTelegenceDeviceAndUsageStaging(context);
        
        // Clear BAN-related staging tables
        TruncateTelegenceBillingAccountNumberStatusStaging(context);
        
        // Clear processing control tables
        ClearProcessingControlTables(context);
        
        // Clear temporary processing tables
        ClearTemporaryTables(context);
        
        LogInfo(context, "All staging tables cleared successfully");
    }
    catch (Exception ex)
    {
        LogError(context, $"Staging table cleanup failed: {ex.Message}");
        throw; // Staging cleanup failure should stop processing
    }
}
```

#### Processing Control Tables Cleanup
```csharp
private void ClearProcessingControlTables(KeySysLambdaContext context)
{
    var controlTables = new[]
    {
        "TelegenceDeviceBANToProcess",
        "TelegenceDeviceNotExistsStagingToProcess",
        "TelegenceProcessingStatus",
        "TelegenceProcessingLocks"
    };
    
    foreach (var tableName in controlTables)
    {
        try
        {
            var sql = $"TRUNCATE TABLE [dbo].[{tableName}]";
            SqlQueryHelper.ExecuteNonQuery(context.CentralDbConnectionString, sql, SQLConstant.ShortTimeoutSeconds);
            
            LogInfo(context, $"Cleared processing control table: {tableName}");
        }
        catch (Exception ex)
        {
            LogError(context, $"Failed to clear {tableName}: {ex.Message}");
            // Continue with other tables
        }
    }
}
```

### Old Processing Session Cleanup

#### Session Tracking and Cleanup
```csharp
private async Task ClearOldProcessingSessions(KeySysLambdaContext context)
{
    try
    {
        // Clear sessions older than configured retention period
        var cutoffDate = DateTime.UtcNow.AddHours(-PROCESSING_SESSION_RETENTION_HOURS);
        
        var cleanupSql = @"
            DELETE FROM TelegenceProcessingSessions 
            WHERE CreatedDate < @CutoffDate 
            AND Status IN ('Completed', 'Failed', 'Cancelled')";
        
        var parameters = new[]
        {
            new SqlParameter("@CutoffDate", cutoffDate)
        };
        
        var deletedCount = SqlQueryHelper.ExecuteNonQuery(
            context.CentralDbConnectionString, 
            cleanupSql, 
            parameters, 
            SQLConstant.ShortTimeoutSeconds);
        
        LogInfo(context, $"Cleaned up {deletedCount} old processing sessions");
        
        // Clear orphaned processing locks
        await ClearOrphanedProcessingLocks(context, cutoffDate);
    }
    catch (Exception ex)
    {
        LogError(context, $"Processing session cleanup failed: {ex.Message}");
    }
}
```

#### Orphaned Lock Cleanup
```csharp
private async Task ClearOrphanedProcessingLocks(KeySysLambdaContext context, DateTime cutoffDate)
{
    try
    {
        var lockCleanupSql = @"
            DELETE FROM TelegenceProcessingLocks 
            WHERE CreatedDate < @CutoffDate 
            OR (LockExpiry < GETUTCDATE() AND Status = 'Active')";
        
        var parameters = new[]
        {
            new SqlParameter("@CutoffDate", cutoffDate)
        };
        
        var deletedLocks = SqlQueryHelper.ExecuteNonQuery(
            context.CentralDbConnectionString, 
            lockCleanupSql, 
            parameters, 
            SQLConstant.ShortTimeoutSeconds);
        
        LogInfo(context, $"Cleaned up {deletedLocks} orphaned processing locks");
    }
    catch (Exception ex)
    {
        LogError(context, $"Processing lock cleanup failed: {ex.Message}");
    }
}
```

### Initialization Cleanup Orchestration

#### Main Cleanup Coordinator
```csharp
public async Task PerformInitializationCleanup(KeySysLambdaContext context)
{
    LogInfo(context, "Starting initialization cleanup process");
    
    var cleanupTasks = new List<Task>();
    
    try
    {
        // Queue cleanup (can run in parallel)
        cleanupTasks.Add(ClearOldQueueEntries(context));
        
        // Processing session cleanup (can run in parallel)
        cleanupTasks.Add(ClearOldProcessingSessions(context));
        
        // Wait for parallel cleanup tasks
        await Task.WhenAll(cleanupTasks);
        
        // Staging table cleanup (must run after queue cleanup)
        ClearAllStagingTables(context);
        
        // Reset processing state
        await ResetProcessingState(context);
        
        LogInfo(context, "Initialization cleanup completed successfully");
    }
    catch (Exception ex)
    {
        LogError(context, $"Initialization cleanup failed: {ex.Message}");
        throw;
    }
}
```

#### Processing State Reset
```csharp
private async Task ResetProcessingState(KeySysLambdaContext context)
{
    try
    {
        // Reset service provider processing state
        var resetSql = @"
            UPDATE ServiceProvider 
            SET LastSyncStartTime = NULL,
                LastSyncEndTime = NULL,
                SyncInProgress = 0
            WHERE IntegrationId = 6"; // Telegence integration
        
        SqlQueryHelper.ExecuteNonQuery(
            context.CentralDbConnectionString, 
            resetSql, 
            SQLConstant.ShortTimeoutSeconds);
        
        // Clear any processing flags
        syncState.InitializeProcessing = true;
        syncState.CurrentPage = 1;
        syncState.HasMoreData = true;
        syncState.RetryNumber = 0;
        
        LogInfo(context, "Processing state reset completed");
    }
    catch (Exception ex)
    {
        LogError(context, $"Processing state reset failed: {ex.Message}");
        throw;
    }
}
```

### Cleanup Configuration

```csharp
// Cleanup configuration constants
private const int PROCESSING_SESSION_RETENTION_HOURS = 24; // Keep sessions for 24 hours
private const int QUEUE_MESSAGE_RETENTION_HOURS = 2; // Clear messages older than 2 hours
private const int MAX_CLEANUP_RETRY_ATTEMPTS = 3; // Maximum cleanup retry attempts
private const bool CLEANUP_ON_INITIALIZATION_ONLY = true; // Only cleanup on initialization
```

---

## 5. Notifications: Stale/Blank File Alerts and Threshold-Based Notifications

### Overview
The Lambda implements a comprehensive notification system that monitors data freshness, detects stale or blank files, and provides threshold-based alerting for proactive issue detection and resolution.

### Stale File Detection System

#### Stale File Criteria Definition
```csharp
public class StaleFileDetectionConfig
{
    public TimeSpan MaxDataAge { get; set; } = TimeSpan.FromHours(6); // 6 hours
    public TimeSpan CriticalDataAge { get; set; } = TimeSpan.FromHours(12); // 12 hours
    public int MinimumRecordCount { get; set; } = 10; // Minimum expected records
    public TimeSpan SyncFrequency { get; set; } = TimeSpan.FromHours(2); // Expected sync frequency
}
```

#### Stale Data Detection Logic
```csharp
private async Task<StaleFileDetectionResult> DetectStaleFiles(KeySysLambdaContext context)
{
    var result = new StaleFileDetectionResult();
    
    try
    {
        // Check last successful sync time
        var lastSyncTime = await GetLastSuccessfulSyncTime(syncState.CurrentServiceProviderId);
        var timeSinceLastSync = DateTime.UtcNow - lastSyncTime;
        
        // Check if data is stale
        if (timeSinceLastSync > staleConfig.MaxDataAge)
        {
            result.IsStale = true;
            result.StaleDuration = timeSinceLastSync;
            result.Severity = timeSinceLastSync > staleConfig.CriticalDataAge ? "Critical" : "Warning";
            
            // Check record counts
            var currentRecordCount = await GetCurrentRecordCount();
            var expectedRecordCount = await GetExpectedRecordCount();
            
            result.RecordCountDeficit = Math.Max(0, expectedRecordCount - currentRecordCount);
            result.HasRecordCountIssue = result.RecordCountDeficit > 0;
        }
        
        return result;
    }
    catch (Exception ex)
    {
        LogError(context, $"Stale file detection failed: {ex.Message}");
        return new StaleFileDetectionResult { DetectionFailed = true, Error = ex.Message };
    }
}
```

### Threshold-Based Notification System

#### Threshold Configuration
```csharp
public class NotificationThresholds
{
    // Data freshness thresholds
    public TimeSpan StaleDataWarningThreshold { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan StaleDataCriticalThreshold { get; set; } = TimeSpan.FromHours(12);
    
    // Processing performance thresholds
    public int MaxProcessingTimeMinutes { get; set; } = 30;
    public int MinRecordsPerMinute { get; set; } = 50;
    
    // Error rate thresholds
    public double MaxErrorRatePercent { get; set; } = 5.0;
    public int MaxConsecutiveFailures { get; set; } = 3;
    
    // API response thresholds
    public int MaxAPIResponseTimeSeconds { get; set; } = 30;
    public int MinAPIResponseSizeBytes { get; set; } = 100;
    
    // Queue depth thresholds
    public int MaxQueueDepth { get; set; } = 100;
    public int CriticalQueueDepth { get; set; } = 500;
}
```

#### Threshold Monitoring Implementation
```csharp
private async Task MonitorThresholds(KeySysLambdaContext context)
{
    var thresholdResults = new List<ThresholdCheckResult>();
    
    // Check data freshness threshold
    thresholdResults.Add(await CheckDataFreshnessThreshold(context));
    
    // Check processing performance threshold
    thresholdResults.Add(await CheckProcessingPerformanceThreshold(context));
    
    // Check error rate threshold
    thresholdResults.Add(await CheckErrorRateThreshold(context));
    
    // Check API response threshold
    thresholdResults.Add(await CheckAPIResponseThreshold(context));
    
    // Check queue depth threshold
    thresholdResults.Add(await CheckQueueDepthThreshold(context));
    
    // Process threshold violations
    await ProcessThresholdViolations(thresholdResults, context);
}
```

### Notification Alert Types

#### 1. Stale Data Alert
```csharp
private async Task SendStaleDataAlert(StaleFileDetectionResult staleResult, KeySysLambdaContext context)
{
    var alert = new NotificationAlert
    {
        AlertType = "StaleData",
        Severity = staleResult.Severity,
        Title = "Stale Data Detected",
        Message = $"Data has not been updated for {staleResult.StaleDuration.TotalHours:F1} hours",
        Details = new
        {
            ServiceProviderId = syncState.CurrentServiceProviderId,
            LastSyncTime = await GetLastSuccessfulSyncTime(syncState.CurrentServiceProviderId),
            StaleDuration = staleResult.StaleDuration,
            RecordCountDeficit = staleResult.RecordCountDeficit,
            ExpectedSyncFrequency = staleConfig.SyncFrequency,
            Timestamp = DateTime.UtcNow
        },
        Actions = new[]
        {
            "Check API endpoint availability",
            "Verify service provider configuration",
            "Review Lambda execution logs",
            "Manually trigger synchronization"
        },
        RequiresImmediateAction = staleResult.Severity == "Critical"
    };
    
    await SendNotificationAlert(alert, context);
}
```

#### 2. Processing Performance Alert
```csharp
private async Task<ThresholdCheckResult> CheckProcessingPerformanceThreshold(KeySysLambdaContext context)
{
    var result = new ThresholdCheckResult { ThresholdType = "ProcessingPerformance" };
    
    try
    {
        var processingStartTime = syncState.ProcessingStartTime ?? DateTime.UtcNow;
        var processingDuration = DateTime.UtcNow - processingStartTime;
        var recordsProcessed = syncState.TotalRecordsProcessed;
        
        // Check processing time threshold
        if (processingDuration.TotalMinutes > thresholds.MaxProcessingTimeMinutes)
        {
            result.IsViolated = true;
            result.Severity = "Warning";
            result.Message = $"Processing time exceeded threshold: {processingDuration.TotalMinutes:F1} minutes";
        }
        
        // Check processing rate threshold
        var recordsPerMinute = recordsProcessed / Math.Max(1, processingDuration.TotalMinutes);
        if (recordsPerMinute < thresholds.MinRecordsPerMinute)
        {
            result.IsViolated = true;
            result.Severity = "Warning";
            result.Message = $"Processing rate below threshold: {recordsPerMinute:F1} records/minute";
        }
        
        return result;
    }
    catch (Exception ex)
    {
        result.DetectionFailed = true;
        result.Error = ex.Message;
        return result;
    }
}
```

#### 3. Error Rate Alert
```csharp
private async Task<ThresholdCheckResult> CheckErrorRateThreshold(KeySysLambdaContext context)
{
    var result = new ThresholdCheckResult { ThresholdType = "ErrorRate" };
    
    try
    {
        var totalAttempts = syncState.TotalAPIAttempts;
        var failedAttempts = syncState.FailedAPIAttempts;
        var errorRate = totalAttempts > 0 ? (double)failedAttempts / totalAttempts * 100 : 0;
        
        if (errorRate > thresholds.MaxErrorRatePercent)
        {
            result.IsViolated = true;
            result.Severity = errorRate > thresholds.MaxErrorRatePercent * 2 ? "Critical" : "Warning";
            result.Message = $"Error rate exceeded threshold: {errorRate:F1}%";
            result.Details = new
            {
                TotalAttempts = totalAttempts,
                FailedAttempts = failedAttempts,
                ErrorRate = errorRate,
                Threshold = thresholds.MaxErrorRatePercent
            };
        }
        
        // Check consecutive failures
        if (syncState.ConsecutiveFailures >= thresholds.MaxConsecutiveFailures)
        {
            result.IsViolated = true;
            result.Severity = "Critical";
            result.Message = $"Consecutive failures exceeded threshold: {syncState.ConsecutiveFailures}";
        }
        
        return result;
    }
    catch (Exception ex)
    {
        result.DetectionFailed = true;
        result.Error = ex.Message;
        return result;
    }
}
```

#### 4. Queue Depth Alert
```csharp
private async Task<ThresholdCheckResult> CheckQueueDepthThreshold(KeySysLambdaContext context)
{
    var result = new ThresholdCheckResult { ThresholdType = "QueueDepth" };
    
    try
    {
        var queueDepths = await GetQueueDepths();
        
        foreach (var queue in queueDepths)
        {
            if (queue.Value > thresholds.CriticalQueueDepth)
            {
                result.IsViolated = true;
                result.Severity = "Critical";
                result.Message = $"Critical queue depth: {queue.Key} has {queue.Value} messages";
            }
            else if (queue.Value > thresholds.MaxQueueDepth)
            {
                result.IsViolated = true;
                result.Severity = result.Severity == "Critical" ? "Critical" : "Warning";
                result.Message = $"High queue depth: {queue.Key} has {queue.Value} messages";
            }
        }
        
        return result;
    }
    catch (Exception ex)
    {
        result.DetectionFailed = true;
        result.Error = ex.Message;
        return result;
    }
}
```

### Notification Delivery System

#### Multi-Channel Notification Delivery
```csharp
private async Task SendNotificationAlert(NotificationAlert alert, KeySysLambdaContext context)
{
    try
    {
        // Send to SQS notification queue
        await SendToSQSQueue(alert, TelegenceDeviceNotificationQueueURL);
        
        // Send email for critical alerts
        if (alert.Severity == "Critical" || alert.RequiresImmediateAction)
        {
            await SendEmailNotification(alert, context);
        }
        
        // Log the notification
        LogNotification(alert, context);
        
        // Update notification tracking
        await UpdateNotificationTracking(alert);
        
    }
    catch (Exception ex)
    {
        LogError(context, $"Failed to send notification alert: {ex.Message}");
    }
}
```

#### Notification Aggregation and Deduplication
```csharp
private async Task<bool> ShouldSendNotification(NotificationAlert alert)
{
    // Check for duplicate notifications within time window
    var duplicateWindow = TimeSpan.FromMinutes(30);
    var recentNotifications = await GetRecentNotifications(alert.AlertType, duplicateWindow);
    
    if (recentNotifications.Any(n => n.Message == alert.Message))
    {
        // Update existing notification count instead of sending new one
        await UpdateNotificationCount(alert);
        return false;
    }
    
    // Check notification rate limiting
    var notificationCount = await GetNotificationCount(alert.AlertType, TimeSpan.FromHours(1));
    if (notificationCount > MAX_NOTIFICATIONS_PER_HOUR)
    {
        LogWarning($"Notification rate limit exceeded for {alert.AlertType}");
        return false;
    }
    
    return true;
}
```

### Notification Configuration

```csharp
// Notification system configuration
private const int MAX_NOTIFICATIONS_PER_HOUR = 10;
private const int NOTIFICATION_RETRY_ATTEMPTS = 3;
private const int NOTIFICATION_BATCH_SIZE = 50;
private const bool ENABLE_EMAIL_NOTIFICATIONS = true;
private const bool ENABLE_SMS_NOTIFICATIONS = false; // Future enhancement

// Notification severity levels
public enum NotificationSeverity
{
    Info,
    Warning,
    Critical,
    Emergency
}
```

This comprehensive documentation addresses all the missing Lambda usage areas, providing detailed implementation guidance, code examples, and configuration options for each component of the TelegenceGetDevices Lambda system.