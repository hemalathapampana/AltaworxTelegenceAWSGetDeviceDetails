# AltaworxTelegenceAWSGetDeviceDetails Lambda Flow Analysis

## Overview
This document provides a comprehensive analysis of the `AltaworxTelegenceAWSGetDeviceDetails` Lambda function flow, detailing every method call from start to finish in sequential order.

**Main Lambda Class:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Base Class:** `AwsFunctionBase`  
**Trigger:** SQS Events

---

## High-Level Flow

### 1. Entry Point
- **Method:** `FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)`
- **Purpose:** Main Lambda entry point that processes SQS messages

### 2. Initialization Path
- **Method:** `BaseFunctionHandler(ILambdaContext context)` *(from AwsFunctionBase)*
- **Method:** `StartDailyDeviceDetailProcessingAsync(KeySysLambdaContext context)` *(when InitializeProcessing=true)*

### 3. Processing Path  
- **Method:** `ProcessDetailListAsync(KeySysLambdaContext context, int groupNumber)` *(when InitializeProcessing=false)*

### 4. Cleanup
- **Method:** `CleanUp(KeySysLambdaContext context)` *(from AwsFunctionBase)*

---

## Detailed Sequential Flow

### Phase 1: Lambda Function Handler Entry
```
FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
├── BaseFunctionHandler(context) [AwsFunctionBase]
├── Environment Variable Validation
├── SQS Records Processing Loop
│   ├── Extract InitializeProcessing flag
│   ├── Extract GroupNumber
│   └── Branch based on InitializeProcessing flag
└── CleanUp(keysysContext) [AwsFunctionBase]
```

### Phase 2A: Initialization Branch (InitializeProcessing = true)
```
StartDailyDeviceDetailProcessingAsync(KeySysLambdaContext context)
├── CallDailyGetDeviceDetailSP(context)
│   └── Execute SQL: "usp_Telegence_Devices_GetDetailFilter"
├── GetGroupCount(context)
│   └── Execute SQL: "SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess]"
└── SendProcessMessagesToQueueAsync(context, groupCount)
    ├── RemovePreviouslyStagedFeatures(context)
    │   └── Execute SQL: "TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]"
    └── Loop: SendProcessMessageToQueueAsync(context, iGroup) for each group
        └── Send SQS Message with InitializeProcessing=false
```

### Phase 2B: Processing Branch (InitializeProcessing = false)
```
ProcessDetailListAsync(KeySysLambdaContext context, int groupNumber)
├── GetSqlRetryPolicy(context) [Polly retry policy setup]
├── Database Query for Subscriber Numbers
│   └── Execute SQL: "SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] FROM [dbo].[TelegenceDeviceDetailIdsToProcess]"
├── TelegenceCommon.GetTelegenceAuthenticationInformation(connectionString, serviceProviderId)
│   └── Execute SQL: "usp_Telegence_Get_AuthenticationByProviderId"
├── Initialize Objects:
│   ├── TelegenceAPIAuthentication
│   ├── TelegenceDeviceDetailSyncTable
│   ├── TelegenceDeviceFeatureSyncTable
│   └── TelegenceAPIClient
└── Process Each Subscriber Number:
    ├── Check Remaining Lambda Time
    ├── TelegenceAPIClient.GetDeviceDetails(deviceDetailUrl, MaxRetries)
    ├── Process Device Detail Response:
    │   ├── TelegenceDeviceDetailSyncTable.AddRow(deviceDetail, serviceProviderId)
    │   │   └── TelegenceServicegGetCharacteristicHelper(deviceDetail) [Parse characteristics]
    │   └── TelegenceDeviceFeatureSyncTable.AddRow(subscriberNumber, feature) [For each offering code]
    │       └── GetDeviceOfferingCodes(deviceDetail) [Extract offering codes]
    └── Error Handling: RemoveDeviceFromQueue(connectionString, groupNumber, subscriberNumber)
├── Bulk Copy Operations:
│   ├── SqlBulkCopy(context, connectionString, table.DataTable, tableName) [AwsFunctionBase]
│   └── RemoveStagedDevices(context, groupNumber)
└── SendProcessMessageToQueueAsync(context, groupNumber) [Continue processing]
```

---

## Low-Level Method Details

### 1. FunctionHandler Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Main entry point for Lambda execution

**Flow:**
1. Initialize `KeySysLambdaContext` via `BaseFunctionHandler(context)`
2. Validate environment variables (TelegenceDeviceDetailGetURL, TelegenceDeviceDetailQueueURL, ProxyUrl, BatchSize)
3. Process each SQS record in the event
4. Extract message attributes: `InitializeProcessing` and `GroupNumber`
5. Branch execution based on `InitializeProcessing` flag
6. Handle exceptions and log messages
7. Call `CleanUp(keysysContext)` for cleanup

### 2. BaseFunctionHandler Method
**Location:** `AwsFunctionBase`  
**Purpose:** Initialize Lambda context and OU-specific settings

**Flow:**
1. Create new `KeySysLambdaContext` instance
2. Load OU-specific settings if not skipped
3. Return initialized context

### 3. StartDailyDeviceDetailProcessingAsync Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Initialize daily device detail processing

**Flow:**
1. **CallDailyGetDeviceDetailSP(context):**
   - Open SQL connection to CentralDb
   - Execute stored procedure: `usp_Telegence_Devices_GetDetailFilter`
   - Pass `@BatchSize` parameter
   - Close connection

2. **GetGroupCount(context):**
   - Open SQL connection to CentralDb
   - Execute query: `SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE IsDeleted = 0`
   - Return maximum group number
   - Close connection

3. **SendProcessMessagesToQueueAsync(context, groupCount):**
   - Call `RemovePreviouslyStagedFeatures(context)`
   - Loop through each group (0 to groupCount)
   - Call `SendProcessMessageToQueueAsync(context, iGroup)` for each group

### 4. RemovePreviouslyStagedFeatures Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Clean up previously staged feature data

**Flow:**
1. Open SQL connection to CentralDb
2. Execute: `TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]`
3. Close connection

### 5. SendProcessMessageToQueueAsync Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Send processing messages to SQS queue

**Flow:**
1. Validate TelegenceDeviceDetailQueueURL
2. Create AmazonSQSClient with AWS credentials
3. Build SendMessageRequest with:
   - DelaySeconds: 5 seconds
   - MessageAttributes: InitializeProcessing=false, GroupNumber
   - MessageBody: Processing request message
4. Send message to SQS queue
5. Log response status

### 6. ProcessDetailListAsync Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Process device details for a specific group

**Flow:**
1. **Setup:**
   - Initialize subscriber numbers list
   - Create SQL retry policy using Polly
   - Initialize counters

2. **Database Query:**
   - Execute SQL with retry policy:
     ```sql
     SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] 
     FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) 
     WHERE GroupNumber = @GroupNumber AND [IsDeleted] = 0
     ```
   - Populate subscriber numbers list

3. **Authentication Setup:**
   - Call `TelegenceCommon.GetTelegenceAuthenticationInformation(connectionString, serviceProviderId)`
   - Create `TelegenceAPIAuthentication` object
   - Initialize sync tables and API client

4. **Process Each Subscriber:**
   - Check Lambda remaining time
   - Format device detail URL
   - Call `TelegenceAPIClient.GetDeviceDetails(deviceDetailUrl, MaxRetries)`
   - Process response:
     - Add to `TelegenceDeviceDetailSyncTable`
     - Extract offering codes and add to `TelegenceDeviceFeatureSyncTable`
   - Handle errors by calling `RemoveDeviceFromQueue`

5. **Bulk Operations:**
   - Bulk copy device details to staging table
   - Call `RemoveStagedDevices(context, groupNumber)`
   - Bulk copy feature staging table

6. **Continue Processing:**
   - Call `SendProcessMessageToQueueAsync(context, groupNumber)`

### 7. TelegenceCommon.GetTelegenceAuthenticationInformation Method
**Location:** `TelegenceCommon`  
**Purpose:** Retrieve Telegence authentication information from database

**Flow:**
1. Open SQL connection
2. Execute stored procedure: `usp_Telegence_Get_AuthenticationByProviderId`
3. Pass `@providerId` parameter
4. Read result and populate `TelegenceAuthentication` object:
   - TelegenceAuthenticationId
   - ProductionUrl, SandboxUrl
   - ClientId, ClientSecret
   - WriteIsEnabled, BillPeriodEndDay
   - Password, UserName
5. Close connection and return authentication object

### 8. TelegenceAPIClient.GetDeviceDetails Method
**Location:** `TelegenceAPIClient`  
**Purpose:** Retrieve device details from Telegence API

**Flow:**
1. **Validation:**
   - Check if WriteIsEnabled
   - Return error result if disabled

2. **Request Setup:**
   - Determine base URI (Production/Sandbox)
   - Build request URI
   - Log request details

3. **HTTP Request with Retry:**
   - Use Polly retry policy
   - Build HTTP request message with:
     - Method: GET
     - Headers: Accept, app-id, app-secret
   - Send request via HttpClient
   - Handle response

4. **Response Processing:**
   - Check if successful
   - Deserialize JSON to `TelegenceDeviceDetailResponse`
   - Handle parsing errors
   - Return `DeviceChangeResult` with response data

### 9. TelegenceDeviceDetailSyncTable.AddRow Method
**Location:** `TelegenceDeviceDetailSyncTable`  
**Purpose:** Add device detail row to staging table

**Flow:**
1. **Column Setup:**
   - Check if columns exist, add if needed
   - Columns: Id, ServiceProviderId, SubscriberNumber, SubscriberActivatedDate, etc.

2. **Characteristic Parsing:**
   - Create `TelegenceServicegGetCharacteristicHelper(deviceDetail)`
   - Extract device characteristics

3. **Row Population:**
   - Create new DataTable row
   - Populate with device detail data:
     - ServiceProviderId, SubscriberNumber
     - ActivatedDate, SingleUserCode, ServiceZipCode
     - NextBillCycleDate, ICCID, IMEI
     - DeviceMake, DeviceModel, etc.
   - Add row to DataTable

### 10. TelegenceServicegGetCharacteristicHelper Constructor
**Location:** `TelegenceServicegGetCharacteristicHelper`  
**Purpose:** Parse and extract service characteristics from device detail

**Flow:**
1. **Characteristic Extraction:**
   - Extract subscriberActivationDate and parse to DateTime
   - Extract singleUserCode and singleUserCodeDescription
   - Extract serviceZipCode
   - Extract nextBillCycleDate and parse to DateTime
   - Extract SIM (ICCID)
   - Extract BLIMEI, BLDeviceBrand, BLDeviceModel
   - Extract BLIMEIType, dataGroupIDCode1
   - Extract contactName, BLDeviceTechnologyType
   - Extract ipAddress, statusEffectiveDate

2. **Data Validation:**
   - Trim 'Z' from date strings
   - Parse dates with error handling
   - Store characteristics as properties

### 11. TelegenceDeviceFeatureSyncTable.AddRow Method
**Location:** `TelegenceDeviceFeatureSyncTable`  
**Purpose:** Add device feature row to staging table

**Flow:**
1. **Column Setup:**
   - Add columns if not exists: SubscriberNumber, OfferingCode

2. **Row Creation:**
   - Create new DataTable row
   - Set SubscriberNumber
   - Set OfferingCode (truncate to 50 characters if needed)
   - Add row to DataTable

### 12. GetDeviceOfferingCodes Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Extract offering codes from device characteristics

**Flow:**
1. Check if ServiceCharacteristic exists
2. Filter characteristics where Name starts with "offeringCode"
3. Extract Value from matching characteristics
4. Return list of offering codes

### 13. SqlBulkCopy Method
**Location:** `AwsFunctionBase`  
**Purpose:** Perform bulk copy operation to database

**Flow:**
1. **Connection Setup:**
   - Open SQL connection

2. **Bulk Copy Configuration:**
   - Create SqlBulkCopy instance
   - Set DestinationTableName
   - Set BulkCopyTimeout and BatchSize
   - Configure column mappings if provided

3. **Execution:**
   - Call WriteToServer with DataTable
   - Handle SQL exceptions
   - Log operation details

### 14. RemoveStagedDevices Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Mark processed devices as deleted

**Flow:**
1. Open SQL connection
2. Execute UPDATE statement:
   ```sql
   UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
   SET [IsDeleted] = 1
   WHERE GroupNumber = @GroupNumber 
   AND SubscriberNumber IN (SELECT SubscriberNumber FROM [dbo].[TelegenceDeviceDetailStaging])
   ```
3. Close connection

### 15. RemoveDeviceFromQueue Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Remove specific device from processing queue

**Flow:**
1. Open SQL connection
2. Execute UPDATE statement:
   ```sql
   UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
   SET [IsDeleted] = 1
   WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber
   ```
3. Close connection

### 16. GetSqlRetryPolicy Method
**Location:** `AltaworxTelegenceAWSGetDeviceDetails.Function`  
**Purpose:** Create Polly retry policy for SQL operations

**Flow:**
1. **Policy Configuration:**
   - Handle SqlException with SqlServerTransientExceptionDetector
   - Handle TimeoutException
   - Configure WaitAndRetry with MaxRetries (5) and RetryDelaySeconds (5)
   - Add logging for retry attempts

2. **Return Policy:**
   - Return configured RetryPolicy instance

### 17. CleanUp Method
**Location:** `AwsFunctionBase`  
**Purpose:** Clean up Lambda context resources

**Flow:**
1. Call context.CleanUp()
2. Release any held resources

---

## Data Flow Summary

### Input Data Sources:
1. **SQS Messages** - Trigger Lambda execution
2. **Environment Variables** - Configuration settings
3. **Database Tables:**
   - `TelegenceDeviceDetailIdsToProcess` - Devices to process
   - `ServiceProvider` - Authentication information
   - `IntegrationAuthentication` - Telegence credentials

### Processing Steps:
1. **Initialization** - Set up processing groups via stored procedure
2. **Authentication** - Retrieve Telegence API credentials
3. **API Calls** - Fetch device details from Telegence API
4. **Data Transformation** - Parse characteristics and features
5. **Staging** - Store processed data in staging tables

### Output Data Destinations:
1. **Staging Tables:**
   - `TelegenceDeviceDetailStaging` - Device detail information
   - `TelegenceDeviceMobilityFeature_Staging` - Device features
2. **SQS Queue** - Continuation messages for processing
3. **Logs** - CloudWatch logging via Lambda context

### Error Handling:
1. **SQL Retry Policy** - Handles transient database errors
2. **API Retry Policy** - Handles transient API errors
3. **Device Removal** - Failed devices removed from processing queue
4. **Exception Logging** - All exceptions logged for troubleshooting

---

## Dependencies and External Services

### AWS Services:
- **Lambda** - Execution environment
- **SQS** - Message queuing
- **CloudWatch** - Logging

### External APIs:
- **Telegence API** - Device detail retrieval

### Database:
- **SQL Server** - Central database for configuration and staging

### Third-Party Libraries:
- **Polly** - Resilience and retry policies
- **Newtonsoft.Json** - JSON serialization/deserialization

---

## Performance Considerations

### Batch Processing:
- Configurable batch size via environment variable
- Group-based processing for parallel execution
- Lambda timeout monitoring

### Database Optimization:
- Bulk copy operations for efficient data loading
- NOLOCK hints for read operations
- Stored procedures for complex operations

### API Efficiency:
- Retry policies for resilience
- Proxy support for network routing
- Connection reuse where possible

### Memory Management:
- DataTable usage for bulk operations
- Proper disposal of database connections
- Context cleanup at Lambda termination

---

## Configuration Parameters

### Environment Variables:
- `TelegenceDeviceDetailGetURL` - API endpoint template
- `TelegenceDeviceDetailQueueURL` - SQS queue URL
- `ProxyUrl` - HTTP proxy URL (optional)
- `BatchSize` - Processing batch size

### Database Configuration:
- Connection strings via Lambda context
- Authentication stored in database
- Service provider specific settings

### Retry Configuration:
- MaxRetries: 5 attempts
- RetryDelaySeconds: 5 seconds
- Timeout settings for various operations

This comprehensive flow analysis covers every method call and data transformation in the AltaworxTelegenceAWSGetDeviceDetails Lambda function, providing complete visibility into the sequential execution from start to finish.