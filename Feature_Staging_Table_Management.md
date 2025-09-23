# Feature Staging Table Management Strategy

## Overview

This document outlines the feature staging table management implementation for the Telegence Device Detail processing system. The strategy follows a "truncated only at init, repopulated fresh" approach, ensuring data consistency and optimal performance through batch processing operations.

## 1. Architecture Overview

### Core Components

- **TelegenceDeviceFeatureSyncTable**: In-memory staging table for feature data
- **ProcessDetailListAsync**: Main processing method coordinating feature extraction
- **GetDeviceOfferingCodes**: Feature extraction utility from device details
- **SqlBulkCopy**: High-performance database loading mechanism

### Data Flow Pattern

```
API Response → Feature Extraction → Memory Staging → Bulk Database Load → Queue Cleanup
```

## 2. Feature Staging Table Implementation

### Table Initialization

```csharp
public TelegenceDeviceFeatureSyncTable()
{
    DataTable = new DataTable("TelegenceDeviceMobilityFeature_Staging");
}
```

**Key Characteristics:**
- **Table Name**: `TelegenceDeviceMobilityFeature_Staging`
- **Initialization**: Created fresh for each processing batch
- **Memory-Based**: Built entirely in memory before database operations
- **Truncation Strategy**: Implicitly truncated through fresh instantiation

### Feature Extraction Process

```csharp
// Extract offering codes from device details
foreach (var feature in GetDeviceOfferingCodes(deviceDetail))
{
    featureStagingTable.AddRow(subscriberNumber, feature);
}
```

**Feature Extraction Logic:**
```csharp
private static IEnumerable<string> GetDeviceOfferingCodes(TelegenceDeviceDetailResponse deviceDetail)
{
    if (deviceDetail?.ServiceCharacteristic == null)
    {
        return new List<string>();
    }

    return deviceDetail.ServiceCharacteristic
        .Where(x => x.Name.StartsWith("offeringCode"))
        .Select(x => x.Value)
        .ToList();
}
```

## 3. Staging Table Management Strategy

### "Truncated Only at Init, Repopulated Fresh" Pattern

#### Initialization Phase
- **Fresh Instance Creation**: New `TelegenceDeviceFeatureSyncTable` created for each batch
- **Memory Allocation**: DataTable initialized with proper schema
- **Clean State**: No residual data from previous processing cycles

#### Population Phase
```csharp
var featureStagingTable = new TelegenceDeviceFeatureSyncTable();
var client = new TelegenceAPIClient(/* parameters */);

foreach (var subscriberNumber in subscriberNumbers)
{
    // API call and device detail retrieval
    var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
    var deviceDetail = apiResult?.ResponseObject?.TelegenceDeviceDetailResponse;
    
    if (deviceDetail != null && !string.IsNullOrWhiteSpace(deviceDetail.SubscriberNumber))
    {
        // Add device details to main staging table
        table.AddRow(deviceDetail, serviceProviderId);

        // Extract and stage features
        foreach (var feature in GetDeviceOfferingCodes(deviceDetail))
        {
            featureStagingTable.AddRow(subscriberNumber, feature);
        }
    }
}
```

#### Bulk Loading Phase
```csharp
// Load feature staging table
if (featureStagingTable.HasRows())
{
    LogInfo(context, "INFO", $"Start execute BulkCopy TelegenceDeviceFeatureSyncTable");
    SqlBulkCopy(context, context.CentralDbConnectionString, 
                featureStagingTable.DataTable, 
                featureStagingTable.DataTable.TableName);
}
```

## 4. Performance Optimization Strategies

### Batch Processing Coordination

```csharp
// Coordinated staging operations
if (table.HasRows())
{
    LogInfo(context, "INFO", $"Start Bulk Copy TelegenceDeviceDetailSyncTable");
    SqlBulkCopy(context, context.CentralDbConnectionString, table.DataTable, table.DataTable.TableName);

    LogInfo(context, "INFO", $"Start execute RemoveStagedDevices");
    sqlRetryPolicy.Execute(() => RemoveStagedDevices(context, groupNumber));
}

// Separate feature staging operation
if (featureStagingTable.HasRows())
{
    LogInfo(context, "INFO", $"Start execute BulkCopy TelegenceDeviceFeatureSyncTable");
    SqlBulkCopy(context, context.CentralDbConnectionString, 
                featureStagingTable.DataTable, 
                featureStagingTable.DataTable.TableName);
}
```

### Key Performance Benefits

1. **Memory Efficiency**: Features accumulated in memory before database operations
2. **Bulk Operations**: Single bulk copy operation per batch reduces database round trips
3. **Conditional Processing**: Only executes bulk copy when staging table contains data
4. **Separate Staging**: Device details and features processed independently for flexibility

## 5. Error Handling and Resilience

### Exception Management
```csharp
try
{
    LogInfo(context, CommonConstants.INFO, $"Get Detail for Subscriber Number : {subscriberNumber}");
    string deviceDetailUrl = string.Format(TelegenceDeviceDetailGetURL, subscriberNumber);
    var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
    var deviceDetail = apiResult?.ResponseObject?.TelegenceDeviceDetailResponse;
    
    // Feature processing with error isolation
    if (deviceDetail != null && !string.IsNullOrWhiteSpace(deviceDetail.SubscriberNumber))
    {
        foreach (var feature in GetDeviceOfferingCodes(deviceDetail))
        {
            featureStagingTable.AddRow(subscriberNumber, feature);
        }
    }
}
catch (Exception e)
{
    LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");
    LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
    sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
    continue; // Continue processing other subscribers
}
```

### Retry Policy Integration
- **SQL Operations**: Protected by `sqlRetryPolicy.Execute()`
- **API Calls**: Built-in retry mechanism with `MaxRetries` parameter
- **Graceful Degradation**: Failed individual records don't stop batch processing

## 6. Data Consistency and Integrity

### Referential Consistency
- **Subscriber Linking**: Features linked to subscriber numbers for data integrity
- **Service Provider Context**: Features processed within service provider scope
- **Batch Coordination**: Features processed alongside corresponding device details

### Queue Management Integration
```csharp
private static void RemoveStagedDevices(KeySysLambdaContext context, int groupNumber)
{
    using (var con = new SqlConnection(context.CentralDbConnectionString))
    {
        string cmdText = @$"UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
                            SET [IsDeleted] = 1
                            WHERE GroupNumber = @GroupNumber 
                            AND SubscriberNumber IN (SELECT SubscriberNumber FROM [dbo].[TelegenceDeviceDetailStaging])";
        
        if (groupNumber < 0)
        {
            cmdText = @$"UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
                         SET [IsDeleted] = 1
                         WHERE SubscriberNumber IN (SELECT SubscriberNumber FROM [dbo].[TelegenceDeviceDetailStaging])";
        }

        using (var cmd = new SqlCommand(cmdText, con))
        {
            con.Open();
            cmd.Parameters.AddWithValue("@GroupNumber", groupNumber);
            cmd.CommandTimeout = 800;
            cmd.ExecuteNonQuery();
        }
    }
}
```

## 7. Configuration and Authentication

### API Authentication Setup
```csharp
var telegenceAuth = TelegenceCommon.GetTelegenceAuthenticationInformation(context.CentralDbConnectionString, serviceProviderId);
var telegenceAPIAuth = new TelegenceAPIAuthentication(new Base64Service())
{
    ClientId = telegenceAuth.ClientId,
    ClientSecret = telegenceAuth.Password,
    ProductionURL = telegenceAuth.ProductionUrl,
    SandboxURL = telegenceAuth.SandboxUrl,
    WriteIsEnabled = telegenceAuth.WriteIsEnabled
};
```

### Processing Configuration
- **Batch Size**: Configurable batch processing limit
- **Timeout Settings**: Command timeout set to 600-800 seconds
- **Retry Configuration**: Configurable retry attempts for API calls
- **Environment Awareness**: Production vs. Sandbox URL selection

## 8. Monitoring and Logging

### Comprehensive Logging Strategy
```csharp
LogInfo(context, "INFO", $"Start execute BulkCopy TelegenceDeviceFeatureSyncTable");
LogInfo(context, CommonConstants.INFO, $"Get Detail for Subscriber Number : {subscriberNumber}");
LogInfo(context, "BatchSize", BatchSize);
```

### Key Monitoring Points
- **Batch Processing Progress**: Subscriber number processing status
- **API Call Success/Failure**: Device detail retrieval outcomes
- **Bulk Copy Operations**: Staging table load completion
- **Queue Management**: Device removal from processing queue

## 9. Best Practices and Recommendations

### Implementation Guidelines

1. **Fresh Staging Approach**: Always create new staging table instances per batch
2. **Conditional Operations**: Check `HasRows()` before expensive bulk operations
3. **Error Isolation**: Handle individual record failures without stopping batch
4. **Resource Management**: Proper disposal of database connections and HTTP clients
5. **Logging Consistency**: Comprehensive logging for troubleshooting and monitoring

### Performance Considerations

1. **Memory Management**: Balance batch size with available memory
2. **Database Connections**: Minimize connection lifetime and use connection pooling
3. **API Rate Limiting**: Respect API rate limits with appropriate retry policies
4. **Bulk Operations**: Prefer bulk operations over individual record processing

### Scalability Factors

1. **Horizontal Scaling**: Design supports multiple concurrent processing groups
2. **Queue-Based Processing**: Asynchronous processing through SQS integration
3. **Configurable Batching**: Adjustable batch sizes for different environments
4. **Resource Optimization**: Efficient memory and database resource utilization

## 10. Conclusion

The feature staging table management strategy implements a robust "truncated only at init, repopulated fresh" pattern that ensures:

- **Data Consistency**: Fresh staging tables eliminate residual data issues
- **Performance Optimization**: Bulk operations minimize database overhead
- **Error Resilience**: Comprehensive error handling maintains processing continuity
- **Scalability**: Queue-based architecture supports horizontal scaling
- **Maintainability**: Clear separation of concerns and comprehensive logging

This approach provides a reliable foundation for high-volume feature data processing while maintaining data integrity and system performance.