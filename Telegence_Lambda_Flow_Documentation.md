# Telegence AWS Lambda Function Flow Documentation

## High-Level Flow

### 1. Entry Point
- **Method**: `FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)`
- **Purpose**: Main Lambda entry point that processes SQS messages

### 2. Initialization Path
- **Method**: `BaseFunctionHandler(ILambdaContext context)` (from AwsFunctionBase)
- **Method**: `StartDailyDeviceDetailProcessingAsync(KeySysLambdaContext context)` (when InitializeProcessing=true)

### 3. Processing Path
- **Method**: `ProcessDetailListAsync(KeySysLambdaContext context, int groupNumber)` (when InitializeProcessing=false)

### 4. Cleanup
- **Method**: `CleanUp(KeySysLambdaContext context)` (from AwsFunctionBase)

---

## Detailed Sequential Flow

### Phase 1: Lambda Function Handler Entry

#### FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
- Initialize KeySysLambdaContext via `BaseFunctionHandler(context)` [AwsFunctionBase]
- Perform Environment Variable Validation
- Execute SQS Records Processing Loop
  - Extract InitializeProcessing flag
  - Extract GroupNumber
  - Branch based on InitializeProcessing flag
- Execute `CleanUp(keysysContext)` [AwsFunctionBase]

### Phase 2A: Initialization Branch (InitializeProcessing = true)

#### StartDailyDeviceDetailProcessingAsync(KeySysLambdaContext context)
- Execute `CallDailyGetDeviceDetailSP(context)`
  - Execute SQL: `"usp_Telegence_Devices_GetDetailFilter"`
- Execute `GetGroupCount(context)`
  - Execute SQL: `"SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess]"`
- Execute `SendProcessMessagesToQueueAsync(context, groupCount)`
  - Execute `RemovePreviouslyStagedFeatures(context)`
    - Execute SQL: `"TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]"`
  - Loop: Execute `SendProcessMessageToQueueAsync(context, iGroup)` for each group
    - Send SQS Message with InitializeProcessing=false

### Phase 2B: Processing Branch (InitializeProcessing = false)

#### ProcessDetailListAsync(KeySysLambdaContext context, int groupNumber)
- Initialize `GetSqlRetryPolicy(context)` [Polly retry policy setup]
- Execute Database Query for Subscriber Numbers
  - Execute SQL: `"SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] FROM [dbo].[TelegenceDeviceDetailIdsToProcess]"`
- Execute `TelegenceCommon.GetTelegenceAuthenticationInformation(connectionString, serviceProviderId)`
  - Execute SQL: `"usp_Telegence_Get_AuthenticationByProviderId"`
- Initialize Objects:
  - TelegenceAPIAuthentication
  - TelegenceDeviceDetailSyncTable
  - TelegenceDeviceFeatureSyncTable
  - TelegenceAPIClient
- Process Each Subscriber Number:
  - Check Remaining Lambda Time
  - Execute `TelegenceAPIClient.GetDeviceDetails(deviceDetailUrl, MaxRetries)`
  - Process Device Detail Response:
    - Execute `TelegenceDeviceDetailSyncTable.AddRow(deviceDetail, serviceProviderId)`
      - Execute `TelegenceServicegGetCharacteristicHelper(deviceDetail)` [Parse characteristics]
    - Execute `TelegenceDeviceFeatureSyncTable.AddRow(subscriberNumber, feature)` [For each offering code]
      - Execute `GetDeviceOfferingCodes(deviceDetail)` [Extract offering codes]
  - Handle Errors: Execute `RemoveDeviceFromQueue(connectionString, groupNumber, subscriberNumber)`
- Execute Bulk Copy Operations:
  - Execute `SqlBulkCopy(context, connectionString, table.DataTable, tableName)` [AwsFunctionBase]
  - Execute `RemoveStagedDevices(context, groupNumber)`
- Execute `SendProcessMessageToQueueAsync(context, groupNumber)` [Continue processing]

---

## Low-Level Method Details

### 1. FunctionHandler Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Main entry point for Lambda execution

#### Flow:
- Initialize KeySysLambdaContext via `BaseFunctionHandler(context)`
- Validate environment variables:
  - TelegenceDeviceDetailGetURL
  - TelegenceDeviceDetailQueueURL
  - ProxyUrl
  - BatchSize
- Process each SQS record in the event:
  - Extract message attributes: InitializeProcessing and GroupNumber
  - Branch execution based on InitializeProcessing flag
  - Handle exceptions and log messages
- Execute `CleanUp(keysysContext)` for cleanup

### 2. BaseFunctionHandler Method

**Location**: AwsFunctionBase  
**Purpose**: Initialize Lambda context and OU-specific settings

#### Flow:
- Create new KeySysLambdaContext instance
- Load OU-specific settings if not skipped
- Return initialized context

### 3. StartDailyDeviceDetailProcessingAsync Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Initialize daily device detail processing

#### Flow:

##### CallDailyGetDeviceDetailSP(context):
- Open SQL connection to CentralDb
- Execute stored procedure: `usp_Telegence_Devices_GetDetailFilter`
- Pass @BatchSize parameter
- Close connection

##### GetGroupCount(context):
- Open SQL connection to CentralDb
- Execute query: `SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE IsDeleted = 0`
- Return maximum group number
- Close connection

##### SendProcessMessagesToQueueAsync(context, groupCount):
- Call `RemovePreviouslyStagedFeatures(context)`
- Loop through each group (0 to groupCount)
- Call `SendProcessMessageToQueueAsync(context, iGroup)` for each group

### 4. RemovePreviouslyStagedFeatures Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Clean up previously staged feature data

#### Flow:
- Open SQL connection to CentralDb
- Execute: `TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]`
- Close connection

### 5. SendProcessMessageToQueueAsync Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Send processing messages to SQS queue

#### Flow:
- Validate TelegenceDeviceDetailQueueURL
- Create AmazonSQSClient with AWS credentials
- Build SendMessageRequest with:
  - DelaySeconds: 5 seconds
  - MessageAttributes: InitializeProcessing=false, GroupNumber
  - MessageBody: Processing request message
- Send message to SQS queue
- Log response status

### 6. ProcessDetailListAsync Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Process device details for a specific group

#### Flow:

##### Setup:
- Initialize subscriber numbers list
- Create SQL retry policy using Polly
- Initialize counters

##### Database Query:
- Execute SQL with retry policy:
  ```sql
  SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] 
  FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) 
  WHERE GroupNumber = @GroupNumber AND [IsDeleted] = 0
  ```
- Populate subscriber numbers list

##### Authentication Setup:
- Call `TelegenceCommon.GetTelegenceAuthenticationInformation(connectionString, serviceProviderId)`
- Create TelegenceAPIAuthentication object
- Initialize sync tables and API client

##### Process Each Subscriber:
- Check Lambda remaining time
- Format device detail URL
- Call `TelegenceAPIClient.GetDeviceDetails(deviceDetailUrl, MaxRetries)`
- Process response:
  - Add to TelegenceDeviceDetailSyncTable
  - Extract offering codes and add to TelegenceDeviceFeatureSyncTable
- Handle errors by calling `RemoveDeviceFromQueue`

##### Bulk Operations:
- Bulk copy device details to staging table
- Call `RemoveStagedDevices(context, groupNumber)`
- Bulk copy feature staging table

##### Continue Processing:
- Call `SendProcessMessageToQueueAsync(context, groupNumber)`

### 7. TelegenceCommon.GetTelegenceAuthenticationInformation Method

**Location**: TelegenceCommon  
**Purpose**: Retrieve Telegence authentication information from database

#### Flow:
- Open SQL connection
- Execute stored procedure: `usp_Telegence_Get_AuthenticationByProviderId`
- Pass @providerId parameter
- Read result and populate TelegenceAuthentication object:
  - TelegenceAuthenticationId
  - ProductionUrl, SandboxUrl
  - ClientId, ClientSecret
  - WriteIsEnabled, BillPeriodEndDay
  - Password, UserName
- Close connection and return authentication object

### 8. TelegenceAPIClient.GetDeviceDetails Method

**Location**: TelegenceAPIClient  
**Purpose**: Retrieve device details from Telegence API

#### Flow:

##### Validation:
- Check if WriteIsEnabled
- Return error result if disabled

##### Request Setup:
- Determine base URI (Production/Sandbox)
- Build request URI
- Log request details

##### HTTP Request with Retry:
- Use Polly retry policy
- Build HTTP request message with:
  - Method: GET
  - Headers: Accept, app-id, app-secret
- Send request via HttpClient
- Handle response

##### Response Processing:
- Check if successful
- Deserialize JSON to TelegenceDeviceDetailResponse
- Handle parsing errors
- Return DeviceChangeResult with response data

### 9. TelegenceDeviceDetailSyncTable.AddRow Method

**Location**: TelegenceDeviceDetailSyncTable  
**Purpose**: Add device detail row to staging table

#### Flow:

##### Column Setup:
- Check if columns exist, add if needed
- Columns: Id, ServiceProviderId, SubscriberNumber, SubscriberActivatedDate, etc.

##### Characteristic Parsing:
- Create `TelegenceServicegGetCharacteristicHelper(deviceDetail)`
- Extract device characteristics

##### Row Population:
- Create new DataTable row
- Populate with device detail data:
  - ServiceProviderId, SubscriberNumber
  - ActivatedDate, SingleUserCode, ServiceZipCode
  - NextBillCycleDate, ICCID, IMEI
  - DeviceMake, DeviceModel, etc.
- Add row to DataTable

### 10. TelegenceServicegGetCharacteristicHelper Constructor

**Location**: TelegenceServicegGetCharacteristicHelper  
**Purpose**: Parse and extract service characteristics from device detail

#### Flow:

##### Characteristic Extraction:
- Extract subscriberActivationDate and parse to DateTime
- Extract singleUserCode and singleUserCodeDescription
- Extract serviceZipCode
- Extract nextBillCycleDate and parse to DateTime
- Extract SIM (ICCID)
- Extract BLIMEI, BLDeviceBrand, BLDeviceModel
- Extract BLIMEIType, dataGroupIDCode1
- Extract contactName, BLDeviceTechnologyType
- Extract ipAddress, statusEffectiveDate

##### Data Validation:
- Trim 'Z' from date strings
- Parse dates with error handling
- Store characteristics as properties

### 11. TelegenceDeviceFeatureSyncTable.AddRow Method

**Location**: TelegenceDeviceFeatureSyncTable  
**Purpose**: Add device feature row to staging table

#### Flow:

##### Column Setup:
- Add columns if not exists: SubscriberNumber, OfferingCode

##### Row Creation:
- Create new DataTable row
- Set SubscriberNumber
- Set OfferingCode (truncate to 50 characters if needed)
- Add row to DataTable

### 12. GetDeviceOfferingCodes Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Extract offering codes from device characteristics

#### Flow:
- Check if ServiceCharacteristic exists
- Filter characteristics where Name starts with "offeringCode"
- Extract Value from matching characteristics
- Return list of offering codes

### 13. SqlBulkCopy Method

**Location**: AwsFunctionBase  
**Purpose**: Perform bulk copy operation to database

#### Flow:

##### Connection Setup:
- Open SQL connection

##### Bulk Copy Configuration:
- Create SqlBulkCopy instance
- Set DestinationTableName
- Set BulkCopyTimeout and BatchSize
- Configure column mappings if provided

##### Execution:
- Call WriteToServer with DataTable
- Handle SQL exceptions
- Log operation details

### 14. RemoveStagedDevices Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Mark processed devices as deleted

#### Flow:
- Open SQL connection
- Execute UPDATE statement:
  ```sql
  UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
  SET [IsDeleted] = 1
  WHERE GroupNumber = @GroupNumber 
  AND SubscriberNumber IN (SELECT SubscriberNumber FROM [dbo].[TelegenceDeviceDetailStaging])
  ```
- Close connection

### 15. RemoveDeviceFromQueue Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Remove specific device from processing queue

#### Flow:
- Open SQL connection
- Execute UPDATE statement:
  ```sql
  UPDATE [dbo].[TelegenceDeviceDetailIdsToProcess]
  SET [IsDeleted] = 1
  WHERE GroupNumber = @GroupNumber AND SubscriberNumber = @SubscriberNumber
  ```
- Close connection

### 16. GetSqlRetryPolicy Method

**Location**: AltaworxTelegenceAWSGetDeviceDetails.Function  
**Purpose**: Create Polly retry policy for SQL operations

#### Flow:

##### Policy Configuration:
- Handle SqlException with SqlServerTransientExceptionDetector
- Handle TimeoutException
- Configure WaitAndRetry with MaxRetries (5) and RetryDelaySeconds (5)
- Add logging for retry attempts

##### Return Policy:
- Return configured RetryPolicy instance

### 17. CleanUp Method

**Location**: AwsFunctionBase  
**Purpose**: Clean up Lambda context resources

#### Flow:
- Call `context.CleanUp()`
- Release any held resources

---

## Summary

This Lambda function implements a two-phase processing system:

1. **Initialization Phase**: Sets up the processing queue by calling stored procedures and sending messages to SQS
2. **Processing Phase**: Processes device details in batches, making API calls to Telegence, and storing results in staging tables

The system uses SQS for orchestration, SQL Server for data persistence, and implements retry policies for resilience. Each phase is designed to handle large volumes of data efficiently through batching and parallel processing via SQS messages.