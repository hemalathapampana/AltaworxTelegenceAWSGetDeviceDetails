# TelegenceGetDevices Lambda Analysis

## Overview
The TelegenceGetDevices Lambda is a comprehensive data synchronization service that retrieves device information from the Telegence API and processes it through multiple stages including BAN (Billing Account Number) status validation, device staging, and error handling.

## 1. Lambda Triggers

### Primary Trigger: SQS Queue
```csharp
public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
```

**Trigger Sources:**
- **SQS Queue**: Primary trigger via `TelegenceDestinationQueueGetDevicesURL`
- **Manual Execution**: Can be invoked manually (when `sqsEvent?.Records` is null)
- **Self-Requeue**: TelegenceGetDevices Lambda re-queues itself for continuation processing

**Message Attributes Used:**
- `CurrentPage`: API pagination tracking
- `HasMoreData`: Indicates if more API pages exist
- `CurrentServiceProviderId`: Service provider being processed
- `InitializeProcessing`: Determines processing phase
- `IsProcessDeviceNotExistsStaging`: Device validation phase flag
- `GroupNumber`: Batch processing group identifier
- `RetryNumber`: Retry attempt counter

## 2. SQL Retry Logic and Issue Prevention

### SQL Retry Implementation
```csharp
policyFactory.SqlRetryPolicy(CommonConstants.NUMBER_OF_RETRIES).Execute(() =>
{
    deviceCount = SqlQueryHelper.ExecuteStoredProcedureWithIntResult(ParameterizedLog(context),
    context.CentralDbConnectionString,
    SQLConstant.StoredProcedureName.TELEGENCE_GET_CURRENT_DEVICES_COUNT,
    parameters,
    SQLConstant.ShortTimeoutSeconds);
});
```

**Why SQL Retry is Done First:**
- **Transient Connection Issues**: Network hiccups, connection pool exhaustion
- **Database Lock Contention**: Temporary table locks during concurrent operations
- **Timeout Prevention**: Prevents premature failures on slow queries
- **Data Consistency**: Ensures critical operations complete successfully

**Issues Prevented:**
- Connection timeouts
- Deadlock scenarios
- Network intermittency
- Resource contention
- Transaction rollbacks

**Retry Configuration:**
- `NUMBER_OF_RETRIES = 3`
- Applied to all critical SQL operations
- Uses exponential backoff pattern

## 3. Staging Table Management

### Initial Staging Table Clearing
```csharp
TruncateTelegenceDeviceAndUsageStaging(context);
TruncateTelegenceBillingAccountNumberStatusStaging(context);
syncState.CurrentServiceProviderId = serviceProvider;
```

**Staging Tables Cleared:**
1. **TelegenceDeviceStaging** - via `usp_Telegence_Truncate_DeviceAndUsageStaging`
2. **TelegenceDeviceDetailStaging** - via `usp_Telegence_Truncate_DeviceAndUsageStaging`
3. **TelegenceAllUsageStaging** - via `usp_Telegence_Truncate_DeviceAndUsageStaging`
4. **TelegenceDeviceUsageMubuStaging** - via `usp_Telegence_Truncate_DeviceAndUsageStaging`
5. **TelegenceDeviceBillingNumberAccountStatusStaging** - via `usp_Telegence_Truncate_BillingAccountNumberStatusStaging`
6. **TelegenceDeviceBANToProcess** - via `usp_Telegence_Truncate_BillingAccountNumberStatusStaging`

**Clearing Logic:**
- Only for new service providers (when `syncState.CurrentServiceProviderId == 0`)
- Not cleared between retry attempts - data persists for continuation
- Fresh start per service provider - ensures clean processing state

**Persistence Between Runs:**
Staging tables are NOT cleared after previous runs to support:
- Continuation processing across Lambda timeouts
- Retry mechanisms without data loss
- Multi-page API processing state maintenance
- Error recovery scenarios

## 4. BAN, FAN, and Subscriber Number Status Storage

### BAN Status Storage Table: TelegenceDeviceBillingNumberAccountStatusStaging
```csharp
using (var sqlCommand = new SqlCommand("SELECT BillingAccountNumber, Status, ServiceProviderId FROM TelegenceDeviceBillingNumberAccountStatusStaging where BillingAccountNumber IS NOT NULL", connection))
{
    using (var reader = sqlCommand.ExecuteReader())
    {
        while (reader.Read())
        {
            var key = reader[0].ToString();
            if (!banStatuses.ContainsKey(key))
            {
                banStatuses.Add(key, reader[1].ToString());
            }
        }
    }
}
```

**Storage Fields:**
- `BillingAccountNumber`: BAN identifier
- `Status`: BAN status from Telegence API
- `ServiceProviderId`: Associated service provider

### Device Storage Table: TelegenceDeviceStaging
```csharp
table.Columns.Add(CommonColumnNames.Id);
table.Columns.Add(CommonColumnNames.ServiceProviderId);
table.Columns.Add(CommonColumnNames.FoundationAccountNumber); // FAN
table.Columns.Add(CommonColumnNames.BillingAccountNumber);    // BAN
table.Columns.Add(CommonColumnNames.SubscriberNumber);        // Subscriber Number
table.Columns.Add(CommonColumnNames.SubscriberNumberStatus);  // Subscriber Number Status
table.Columns.Add(CommonColumnNames.RefreshTimestamp);
table.Columns.Add(CommonColumnNames.CreatedDate);
table.Columns.Add(CommonColumnNames.BanStatus);
```

**Data Elements Stored:**
- **FAN (Foundation Account Number)**: Primary account identifier
- **BAN (Billing Account Number)**: Billing-specific account number
- **Subscriber Number**: Device phone number/MSISDN
- **Subscriber Number Status**: Current status of the subscriber number
- **BAN Status**: Status retrieved from BAN detail API calls

## 5. BAN List Status Source

### BAN Status Retrieval Process
```csharp
private Dictionary<string, string> GetBanListStatusesStaging(string centralDbConnectionString)
{
    Dictionary<string, string> banStatuses = new Dictionary<string, string>();
    using (SqlConnection connection = new SqlConnection(centralDbConnectionString))
    {
        connection.Open();
        using (var sqlCommand = new SqlCommand("SELECT BillingAccountNumber, Status, ServiceProviderId FROM TelegenceDeviceBillingNumberAccountStatusStaging where BillingAccountNumber IS NOT NULL", connection))
```

**BAN Status Flow:**
1. **Initial Population**: Retrieved from Telegence API via `TelegenceBanDetailGetURL`
2. **Staged Storage**: Saved to `TelegenceDeviceBillingNumberAccountStatusStaging`
3. **Processing Use**: Read from staging for device validation
4. **API Source**: GET `/billingaccounts/{ban}` endpoint

**Related Stored Procedures:**
- `usp_Telegence_Devices_Prepare_BANs_To_Process`: Prepares distinct BAN list
- `usp_Get_BAN_List_Need_To_Process`: Retrieves unprocessed BANs
- `usp_Mark_Processed_For_BAN`: Marks BANs as processed after status retrieval

## 6. Telegence API Endpoint Details

### Primary API Call
```csharp
return await TelegenceCommon.GetTelegenceDevicesAsync(context, syncState, ProxyUrl, telegenceDeviceList, TelegenceDevicesGetURL, BatchSize);
```

**Primary API Endpoints:**
1. **Device List**: `TelegenceDevicesGetURL` - Main device retrieval endpoint
2. **BAN Detail**: `TelegenceBanDetailGetURL` - BAN status endpoint (pattern: `/billingaccounts/{ban}`)
3. **Device Detail**: `TelegenceDeviceDetailGetURL` - Individual device details (pattern: `/devices/{subscriberNumber}`)

**API Configuration:**
- **Base URL**: `https://apsapi.att.com:8082`
- **Device Endpoint**: `/sp/mobility/lineconfig/api/v1/service/<SubscriberNumber>`
- **Authentication**: OAuth2 with ClientId and ClientSecret

**Authentication Headers:**
```json
{
  "app-id": "ClientId",
  "app-secret": "ClientSecret"
}
```

**Authentication Values:**
- **ClientId**: Retrieved from `Integration_Authentication.OAuth2ClientId`
- **ClientSecret**: Retrieved from `Integration_Authentication.OAuth2ClientSecret`
- **Example ClientId**: `17fac23b77124a399cd87184f79ec608`
- **Example ClientSecret**: `d0Da41D483884cE3A6Aa45b1B79DE9E0`

### API Call Implementation
```csharp
public static async Task<TelegenceGetDevicesSyncState> GetTelegenceDevicesAsync(KeySysLambdaContext context, TelegenceGetDevicesSyncState syncState, string proxyUrl,
   List<TelegenceDeviceResponse> telegenceDeviceList, string deviceDetailEndpoint, int pageSize)
```

## 7. API Pagination Configuration

### Page Size Configuration
```csharp
private int BatchSize = Convert.ToInt32(Environment.GetEnvironmentVariable("BatchSize")); // 250
private int DEFAULT_BATCH_SIZE = 250;
```

**Pagination Parameters:**
- **Page Size**: `BatchSize` (default 250, configurable via environment variable)
- **Current Page**: Tracked in `syncState.CurrentPage`
- **Max Cycles**: `MaxCyclesToProcess` (configurable limit per Lambda execution)

**Header Implementation:**
```csharp
headerContent.Add(CommonConstants.CURRENT_PAGE, syncState.CurrentPage.ToString());
headerContent.Add(CommonConstants.PAGE_SIZE, pageSize);
```

**Environment Configuration:**
- **BatchSize**: `250` (from environment variables)
- **Configurable**: Can be adjusted per environment needs
- **Performance Impact**: Larger batch sizes reduce API calls but increase memory usage

## 8. API Pagination Completion Detection

### Completion Detection Logic
```csharp
if (int.TryParse(headers[CommonConstants.PAGE_TOTAL].ToString(), out int pageTotal))
{
    syncState.HasMoreData = syncState.CurrentPage < pageTotal;
}
syncState.IsLastCycle = !syncState.HasMoreData;
```

**Completion Detection Methods:**
1. **Page Total Header**: API returns `page-total` header indicating total pages
2. **HasMoreData Flag**: Calculated as `CurrentPage < PageTotal`
3. **IsLastCycle Flag**: Set when no more data available
4. **Empty Response**: No devices returned indicates completion

**State Management:**
- `syncState.HasMoreData`: Boolean flag for continuation
- `syncState.IsLastCycle`: Final cycle indicator
- `syncState.CurrentPage`: Current page number being processed

## 9. GetTelegenceDeviceBySubscriberNumber Parameters

### Individual Device Detail Retrieval
```csharp
string resultAPI = await TelegenceCommon.GetTelegenceDeviceBySubscriberNumber(context, telegenceAuthenticationInfo, context.IsProduction,
                            telegenceDevice.SubscriberNumber, TelegenceDeviceDetailGetURL, ProxyUrl);
```

**Parameters Used:**
- **subscriberNumber**: Device identifier (phone number/MSISDN)
- **endpoint**: `TelegenceDeviceDetailGetURL` template
- **isProduction**: Environment flag (production vs sandbox)
- **proxyUrl**: Proxy configuration for external calls
- **telegenceAuthenticationInfo**: Authentication credentials

**Endpoint Construction:**
```csharp
var deviceDetailEndpoint = $"{endpoint}{subscriberNo}";
```

**Data Source:**
- Subscriber numbers retrieved from `TelegenceDeviceNotExistsStagingToProcess` table
- Populated via `usp_get_Telegence_Device_Not_Exists_On_Staging_To_Process` stored procedure

## 10. Device Validation and Failure Handling

### Device Status Validation
```csharp
if (telegenceDevice.SubscriberNumberStatus != subscriberStatus && subscriberStatus != CANCEL_STATUS)
{
    var banStatusText = GetBanStatusTextForDevice(banStatus, telegenceDevice);
    var deviceAdd = new TelegenceDeviceResponse()
    {
        BillingAccountNumber = telegenceDevice.BillingAccountNumber,
        FoundationAccountNumber = telegenceDevice.FoundationAccountNumber,
        SubscriberNumber = telegenceDevice.SubscriberNumber,
        SubscriberNumberStatus = subscriberStatus,
        RefreshTimestamp = telegenceDevice.RefreshTimestamp,
    };
    var dr = AddToDataRow(context, table, telegenceDevice, banStatusText, syncState.CurrentServiceProviderId);
    table.Rows.Add(dr);
}
```

**Validation Rules:**
- **Status Changes**: Only process devices with status changes
- **Cancelled Status**: Ignore devices with status "C" (cancelled) - `CANCEL_STATUS = "C"`
- **API Failures**: Devices with API failures are skipped but logged
- **Null Validation**: Handle null or empty subscriber number status

**Failure Handling:**
- **Logging**: Failed API calls logged with error details
- **Continuation**: Processing continues with remaining devices
- **Retry Logic**: Polly retry policy handles transient failures
- **Error Tracking**: Failed devices tracked for monitoring

**Constants Used:**
- `CANCEL_STATUS = "C"`: Cancelled device status
- `SUBSCRIBER_STATUS = "subscriberStatus"`: Status field identifier

## 11. Retry Configuration (Polly)

### API-Level Retry Configuration
```csharp
var responseMessage = await Amop.Core.Helpers.RetryPolicyHelper.PollyRetryForProxyRequestAsync(context.logger, CommonConstants.NUMBER_OF_TELEGENCE_RETRIES).ExecuteAsync(async () =>
{
    using (var client = new HttpClient())
    {
        ConfigHttpClient(client);
        var responseContent = MappingProxyResponseContent(client.GetWithProxy(proxyUrl, payload, context.logger));
        return await Task.FromResult(responseContent);
    }
});
```

**Retry Configuration:**
- **Attempts**: `NUMBER_OF_TELEGENCE_RETRIES = 3`
- **Policy Type**: Polly retry with exponential backoff
- **Scope**: Applied to all Telegence API calls
- **Delay**: Progressive delays between attempts

### Lambda-Level Retries
```csharp
if (remainingBanNeedToProcesses.Count > 0 && syncState.RetryNumber <= CommonConstants.NUMBER_OF_TELEGENCE_LAMBDA_RETRIES)
{
    await SendMessageToGetDevicesQueueAsync(context, syncState, TelegenceDestinationQueueGetDevicesURL, delaySeconds: CommonConstants.DELAY_IN_SECONDS_FIVE_SECONDS);
}
```

**Lambda Retry Constants:**
- `NUMBER_OF_TELEGENCE_LAMBDA_RETRIES = 5`: Maximum Lambda retry attempts
- `DELAY_IN_SECONDS_FIVE_SECONDS = 5`: Delay between Lambda retries
- `REMAINING_TIME_CUT_OFF = 180`: Time threshold for continuation (3 minutes)

**Retry Scenarios:**
- Incomplete BAN processing
- API timeout scenarios
- Database connection failures
- Partial page processing

## 12. Re-enqueuing for Incomplete Processing

### Re-enqueuing Logic
```csharp
if ((!syncState.IsLastCycle || syncState.HasMoreData) && syncState.RetryNumber <= CommonConstants.NUMBER_OF_TELEGENCE_LAMBDA_RETRIES)
{
    await SendMessageToGetDevicesQueueAsync(context, syncState, TelegenceDestinationQueueGetDevicesURL, delaySeconds: CommonConstants.DELAY_IN_SECONDS_FIVE_SECONDS);
}
```

**Re-enqueuing Triggers:**
1. **Timeout Conditions**: TelegenceGetDevices Lambda approaching time limit
2. **Incomplete Pages**: More API pages to process (`syncState.HasMoreData`)
3. **Retry Scenarios**: Failed operations within retry limits
4. **Batch Processing**: Group-based device processing incomplete

### State Preservation in SQS Messages
```csharp
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

**State Attributes Preserved:**
- **HasMoreData**: Pagination continuation flag
- **CurrentPage**: Current API page being processed
- **CurrentServiceProviderId**: Service provider context
- **InitializeProcessing**: Processing phase indicator
- **IsProcessDeviceNotExistsStaging**: Device validation phase
- **GroupNumber**: Batch processing group identifier
- **IsLastProcessDeviceNotExistsStaging**: Final group indicator
- **RetryNumber**: Current retry attempt count

## 13. Stored Procedures in the Processing Flow

### BAN Processing Procedures
1. **`usp_Telegence_Devices_Prepare_BANs_To_Process`**
   ```sql
   INSERT INTO [dbo].[TelegenceDeviceBANToProcess] ([BillingAccountNumber])  
   SELECT DISTINCT [BillingAccountNumber]  
   FROM [TelegenceDevice]  
   WHERE ISNULL([BillingAccountNumber], '') <> '';
   ```
   - Prepares distinct BAN list for processing
   - Filters out null/empty BANs

2. **`usp_Get_BAN_List_Need_To_Process`**
   ```sql
   SELECT [BillingAccountNumber]  
   FROM [TelegenceDeviceBANToProcess]  
   WHERE [IsProcessed] = 0;
   ```
   - Retrieves unprocessed BANs for status updates

3. **`usp_Mark_Processed_For_BAN`**
   ```sql
   UPDATE [TelegenceDeviceBANToProcess] WITH (ROWLOCK)  
   SET [IsProcessed] = 1  
   WHERE [BillingAccountNumber] IN (SELECT [BillingAccountNumber] FROM [#BillingAccountNumbers]);
   ```
   - Marks BANs as processed after status retrieval
   - Uses comma-separated parameter input

### Device Processing Procedures
4. **`usp_get_Telegence_Device_Not_Exists_On_Staging_To_Process`**
   ```sql
   SELECT [SubscriberNumber], [ServiceProviderId], [FoundationAccountNumber], 
          [BillingAccountNumber], [SubscriberNumberStatus]  
   FROM [dbo].[TelegenceDeviceNotExistsStagingToProcess]  
   WHERE GroupNumber = @groupNumber AND [IsProcessed] = 0;
   ```
   - Retrieves devices for existence validation
   - Supports group-based processing

5. **`usp_GetTelegenDevice_NotExists_Stagging`**
   ```sql
   INSERT INTO [dbo].[TelegenceDeviceNotExistsStagingToProcess]
   SELECT SubscriberNumber, ServiceProviderId, FoundationAccountNumber, BillingAccountNumber, 
          SubscriberNumberStatus, ROW_NUMBER() OVER(PARTITION BY ServiceProviderId ORDER BY d.id) / @BatchSize AS GroupNumber  
   FROM TelegenceDevice d  
   WHERE NOT EXISTS (SELECT 1 FROM TelegenceDeviceStaging s WHERE s.subscriberNumber = d.subscriberNumber)  
   AND SubscriberNumberStatus <> 'Unknown'
   ```
   - Populates device not-exists staging table
   - Creates group numbers for batch processing

### Service Provider Management
6. **`usp_DeviceSync_Get_NextServiceProviderIdByIntegration`**
   ```sql
   SELECT ISNULL(nxtSrvPId, -1) as NextServiceProviderId   
   FROM (SELECT TOP (1) srvP.id as nxtSrvPId  
         FROM dbo.ServiceProvider srvP   
         INNER JOIN [dbo].[Integration_Authentication] intAuth on srvP.id = intAuth.ServiceProviderId  
         WHERE srvP.IntegrationId = @integrationid AND srvP.IsActive = 1 AND srvP.IsDeleted = 0  
         AND intAuth.isDeleted = 0 AND intAuth.isActive = 1 AND srvP.id > @providerId  
         ORDER BY srvP.id) a
   ```
   - Gets next service provider for processing
   - Ensures active and authenticated providers only

7. **`usp_Telegence_Get_AuthenticationByProviderId`**
   ```sql
   SELECT auth.id as integrationAuthenticationId, intg.ProductionURL, intg.SandboxURL,
          auth.Username, auth.[Password], servP.WriteIsEnabled, servP.BillPeriodEndDay,
          auth.OAuth2ClientId as ClientId, auth.OAuth2ClientSecret as ClientSecrect  
   FROM [dbo].[Integration_Authentication] auth  
   INNER JOIN dbo.Integration_Connection intg on auth.IntegrationId = intg.IntegrationId  
   INNER JOIN dbo.ServiceProvider servP on servP.id = auth.ServiceProviderId  
   WHERE auth.IntegrationId = 6 AND servP.id = @providerId
   ```
   - Retrieves authentication information for Telegence API calls
   - IntegrationId = 6 specifically for Telegence integration

### Cleanup Operations
8. **`usp_Telegence_Truncate_DeviceAndUsageStaging`**
   ```sql
   TRUNCATE TABLE [dbo].[TelegenceDeviceStaging]  
   TRUNCATE TABLE [dbo].[TelegenceDeviceDetailStaging]  
   TRUNCATE TABLE [dbo].[TelegenceAllUsageStaging]  
   TRUNCATE TABLE [dbo].[TelegenceDeviceUsageMubuStaging]
   ```
   - Clears device-related staging tables

9. **`usp_Telegence_Truncate_BillingAccountNumberStatusStaging`**
   ```sql
   TRUNCATE TABLE [dbo].[TelegenceDeviceBillingNumberAccountStatusStaging]  
   TRUNCATE TABLE [dbo].[TelegenceDeviceBANToProcess]
   ```
   - Clears BAN-related staging tables

## 14. Summary Logging Details

### Comprehensive Logging Categories
```csharp
LogInfo(keysysContext, "STATUS", $"TelegenceGetDevices::Beginning to process {processedRecordCount} records...");
```

**Key Log Categories:**
1. **Processing Status**: Record counts, phase transitions
2. **API Interactions**: Request URLs, response status, retry attempts
3. **Database Operations**: SQL execution, row counts, stored procedure calls
4. **Error Conditions**: Exceptions, failures, timeout scenarios
5. **State Tracking**: Page numbers, service providers, retry counts
6. **Performance Metrics**: Processing times, batch sizes

**Detailed Log Examples:**
- **Record Processing**: "TelegenceGetDevices::Beginning to process {processedRecordCount} records..."
- **API Calls**: "TelegenceGetDevices::Calling API endpoint: {endpoint} for subscriber: {subscriberNumber}"
- **SQL Operations**: "TelegenceGetDevices::Executing stored procedure: {procedureName} with parameters: {parameters}"
- **State Changes**: "TelegenceGetDevices::Moving to next service provider: {serviceProviderId}"
- **Error Handling**: "TelegenceGetDevices::API call failed for subscriber {subscriberNumber}: {errorMessage}"
- **Completion Status**: "TelegenceGetDevices::Processing completed. Total processed: {totalCount}, Errors: {errorCount}"

**Performance Logging:**
- Batch processing times
- API response times
- SQL execution durations
- Memory usage patterns
- Queue message processing times

## 15. Reference Items Usage and Orchestration

### Lambda Functions Coordination
**Primary Function:**
- **TelegenceGetDevices Lambda**: Main orchestrator for device synchronization

**Triggered Functions:**
- **TelegenceGetDeviceUsage Lambda**: Triggered after device processing completion
- **TelegenceGetDeviceDetail Lambda**: Triggered for detailed device information retrieval

### Queue Management
**Primary Queues:**
1. **TelegenceDestinationQueueGetDevicesURL**: Self-continuation queue for TelegenceGetDevices Lambda
   - URL: `https://sqs.us-east-1.amazonaws.com/130265568833/Telegence_Get_Devices_TEST`
   - Purpose: Lambda continuation and retry handling

2. **TelegenceDeviceUsageQueueURL**: Device usage processing queue
   - URL: `https://sqs.us-east-1.amazonaws.com/130265568833/Telegence_Usage_TEST`
   - Purpose: Triggers TelegenceGetDeviceUsage Lambda

3. **TelegenceDeviceDetailQueueURL**: Device detail processing queue
   - Purpose: Triggers TelegenceGetDeviceDetail Lambda for individual device details

4. **TelegenceDeviceNotificationQueueURL**: Notification queue
   - URL: `https://sqs.us-east-1.amazonaws.com/130265568833/Jasper_Get_Device_Sync_Notification_TEST`
   - Purpose: Status notifications and alerts

### Processing Flow Orchestration
```csharp
// Self-continuation for incomplete processing
await SendMessageToGetDevicesQueueAsync(context, syncState, TelegenceDestinationQueueGetDevicesURL, delaySeconds: CommonConstants.DELAY_IN_SECONDS_FIVE_SECONDS);

// Trigger usage processing after device completion
await SendMessageToGetDeviceUsageQueueAsync(context, TelegenceDeviceUsageQueueURL, 5);

// Trigger detail processing
await SendMessageToGetDeviceDetailQueueAsync(context, TelegenceDeviceDetailQueueURL, deviceDetails);
```

**Orchestration Flow:**
1. **Device List Processing** → Self-requeue for continuation (TelegenceGetDevices Lambda)
2. **Device Completion** → Trigger Usage and Detail queues (TelegenceGetDeviceUsage and TelegenceGetDeviceDetail Lambdas)
3. **Error Scenarios** → Retry mechanisms and error queues
4. **Notification Events** → Status updates via notification queue

**Queue Message Flow:**
- **Continuation Messages**: Preserve processing state for TelegenceGetDevices Lambda resumption
- **Trigger Messages**: Initiate downstream processing in other Lambda functions
- **Error Messages**: Handle failed processing scenarios
- **Notification Messages**: Provide status updates and monitoring information

## Environment Configuration

### Key Environment Variables
| Variable | Value | Purpose |
|----------|--------|---------|
| `BatchSize` | `250` | API pagination page size |
| `ConnectionString` | Database connection | Central database access |
| `BaseMultiTenantConnectionString` | Multi-tenant DB connection | Multi-tenant data access |
| `EnvName` | `Test` | Environment identifier |
| `VerboseLogging` | `false` | Detailed logging control |
| `DaysToKeep` | `90` | Data retention period |
| `DeviceCleanupMaxRetries` | `15` | Cleanup retry limit |

### Processing Constants
- `NUMBER_OF_RETRIES = 3`: SQL operation retries
- `NUMBER_OF_TELEGENCE_RETRIES = 3`: API call retries
- `NUMBER_OF_TELEGENCE_LAMBDA_RETRIES = 5`: Lambda execution retries
- `DELAY_IN_SECONDS_FIVE_SECONDS = 5`: Retry delay interval
- `REMAINING_TIME_CUT_OFF = 180`: Lambda timeout threshold (3 minutes)
- `CANCEL_STATUS = "C"`: Cancelled device status identifier
- `SUBSCRIBER_STATUS = "subscriberStatus"`: Status field name

## Error Handling and Monitoring

### Error Categories
1. **API Errors**: Telegence service unavailability, authentication failures
2. **Database Errors**: Connection timeouts, deadlocks, constraint violations
3. **Processing Errors**: Data validation failures, transformation errors
4. **Timeout Errors**: Lambda execution time limits, API response timeouts
5. **Queue Errors**: Message delivery failures, attribute parsing errors

### Monitoring Points
- **Device Processing Counts**: Total devices processed per service provider
- **API Success Rates**: Successful vs failed API calls
- **Processing Duration**: Time taken per batch and overall processing
- **Error Rates**: Frequency and types of errors encountered
- **Queue Message Metrics**: Message processing times and failure rates
- **Database Performance**: SQL execution times and connection health

### Recovery Mechanisms
- **Automatic Retries**: Built-in retry logic for transient failures
- **State Preservation**: Processing state maintained across Lambda invocations
- **Partial Processing**: Ability to resume from last successful checkpoint
- **Error Isolation**: Failed devices don't stop overall processing
- **Manual Intervention**: Ability to manually trigger processing for specific scenarios

This comprehensive analysis covers all aspects of the TelegenceGetDevices Lambda processing flow, from initial triggers through final completion and error handling.