# Telegence Device Details Lambda Function Flow Analysis

## Main Lambda Function: AltaworxTelegenceAWSGetDeviceDetails.cs

### Sequential Function Flow

#### 1. Entry Point Functions
- **FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)** - Main entry point for Lambda execution

#### 2. Primary Processing Functions (Sequential Order)
1. **BaseFunctionHandler(context)** - Initialize KeySysLambdaContext
2. **StartDailyDeviceDetailProcessingAsync(keysysContext)** - Initialize processing (if InitializeProcessing = true)
3. **ProcessDetailListAsync(keysysContext, groupNumber)** - Process device details (if InitializeProcessing = false)

#### 3. Supporting Functions
- **CallDailyGetDeviceDetailSP(context)** - Execute stored procedure
- **GetGroupCount(context)** - Get maximum group number
- **SendProcessMessagesToQueueAsync(context, groupCount)** - Queue multiple messages
- **SendProcessMessageToQueueAsync(context, groupNumber)** - Queue individual message
- **RemovePreviouslyStagedFeatures(context)** - Clean staging table
- **RemoveStagedDevices(context, groupNumber)** - Remove processed devices
- **RemoveDeviceFromQueue(connectionString, groupNumber, subscriberNumber)** - Remove failed devices
- **GetDeviceOfferingCodes(deviceDetail)** - Extract offering codes
- **GetSqlRetryPolicy(context)** - Create retry policy for SQL operations

---

## Low-Level Flow Analysis

### 1. FunctionHandler Flow
```
START: Lambda receives SQS event
├── Initialize KeySysLambdaContext
├── Parse environment variables (TelegenceDeviceDetailGetURL, ProxyUrl, BatchSize)
├── FOR each SQS record:
│   ├── Extract MessageAttributes (InitializeProcessing, GroupNumber)
│   ├── IF InitializeProcessing = true:
│   │   └── CALL StartDailyDeviceDetailProcessingAsync()
│   └── ELSE:
│       └── CALL ProcessDetailListAsync()
└── Clean up context
END
```

### 2. StartDailyDeviceDetailProcessingAsync Flow
```
START: Initialize daily processing
├── CALL CallDailyGetDeviceDetailSP()
│   ├── Execute stored procedure: "usp_Telegence_Devices_GetDetailFilter"
│   └── Pass BatchSize parameter
├── CALL GetGroupCount()
│   ├── Query: "SELECT MAX(GroupNumber) FROM TelegenceDeviceDetailIdsToProcess"
│   └── Return maximum group number
├── CALL SendProcessMessagesToQueueAsync()
│   ├── CALL RemovePreviouslyStagedFeatures()
│   │   └── Execute: "TRUNCATE TABLE TelegenceDeviceMobilityFeature_Staging"
│   └── FOR each group (0 to groupCount):
│       └── CALL SendProcessMessageToQueueAsync()
END
```

### 3. ProcessDetailListAsync Flow
```
START: Process device details for specific group
├── Query subscriber numbers and service provider ID:
│   ├── SQL: "SELECT TOP {BatchSize} SubscriberNumber, ServiceProviderId FROM TelegenceDeviceDetailIdsToProcess WHERE GroupNumber = @GroupNumber AND IsDeleted = 0"
│   └── Store results in subscriberNumbers list
├── Get Telegence authentication:
│   └── CALL TelegenceCommon.GetTelegenceAuthenticationInformation()
├── Initialize data tables:
│   ├── Create TelegenceDeviceDetailSyncTable
│   └── Create TelegenceDeviceFeatureSyncTable
├── Create TelegenceAPIClient
├── FOR each subscriberNumber:
│   ├── Check remaining execution time
│   ├── Format device detail URL
│   ├── CALL client.GetDeviceDetails()
│   ├── IF device detail received:
│   │   ├── Add to TelegenceDeviceDetailSyncTable
│   │   ├── Extract offering codes using GetDeviceOfferingCodes()
│   │   └── Add features to TelegenceDeviceFeatureSyncTable
│   └── ELSE:
│       └── CALL RemoveDeviceFromQueue()
├── IF data table has rows:
│   ├── Bulk copy to TelegenceDeviceDetailStaging
│   └── CALL RemoveStagedDevices()
├── IF feature table has rows:
│   └── Bulk copy to TelegenceDeviceMobilityFeature_Staging
└── CALL SendProcessMessageToQueueAsync() for continuation
END
```

---

## Helper Classes and Their Methods

### AwsFunctionBase.cs

#### Core Methods (Sequential Order):
1. **BaseFunctionHandler(context, skipOUSpecificLogic)** - Initialize context
2. **BaseAmopFunctionHandler(context, skipOUSpecificLogic)** - Initialize AMOP context
3. **LogInfo(context, desc, detail, file, line, functionName)** - Logging functionality
4. **CleanUp(context)** - Context cleanup

#### Database Query Methods:
1. **GetCustomerName(context, customerId)** - Get customer name from RevCustomer table
2. **GetInstance(context, instanceId)** - Get optimization instance details
3. **GetQueue(context, queueId)** - Get optimization queue details
4. **GetCommGroups(context, instanceId)** - Get communication groups
5. **GetSimCardCount(context, jasperDbConnectionString)** - Count SIM cards
6. **GetBatchedJasperSimCardCountByServiceProviderId(context, serviceProviderId, batchSize)** - Get batched device counts

#### Utility Methods:
1. **SqlBulkCopy(context, connectionString, table, tableName, columnMappings)** - Bulk data operations
2. **AwsCredentials(context)** - AWS credential management
3. **AwsSesCredentials(context)** - AWS SES credentials
4. **GetStringValueFromEnvironmentVariable(context, environmentRepo, key)** - Environment variable retrieval
5. **GetLongValueFromEnvironmentVariable(lambdaContext, environmentRepo, variableKey, defaultValue)** - Long value parsing
6. **GetBooleanValueFromEnvironmentVariable(lambdaContext, environmentRepo, variableKey, defaultValue)** - Boolean value parsing
7. **GetIntValueFromEnvironmentVariable(lambdaContext, environmentRepo, variableKey, defaultValue)** - Integer value parsing
8. **GetFANFilter(context, currentServiceProviderId)** - Get FAN filter settings

### ServiceProviderCommon.cs

#### Service Provider Management Methods:
1. **GetNextServiceProviderId(connectionString, integrationType, currentServiceProviderId)**
   - Execute stored procedure: "usp_DeviceSync_Get_NextServiceProviderIdByIntegration"
   - Returns next provider ID for iteration

2. **GetServiceProvider(connectionString, serviceProviderId)**
   - Query service provider details by ID
   - Returns ServiceProvider object with all configuration

3. **GetServiceProviders(connectionString)**
   - Query all service providers
   - Returns List<ServiceProvider>

4. **GetServiceProviderByName(connectionString, serviceProviderName)**
   - Query service provider by name
   - Uses ServiceProviderFromReader() helper

5. **ServiceProviderFromReader(reader)** (Private)
   - Maps SqlDataReader to ServiceProvider object
   - Handles null value conversions

### TelegenceCommon.cs

#### Authentication and Configuration Methods:
1. **GetTelegenceAuthenticationInformation(connectionString, serviceProviderId)**
   - Execute stored procedure: "usp_Telegence_Get_AuthenticationByProviderId"
   - Returns TelegenceAuthentication object

2. **GetTelegenceBillingAccounts(connectionString, serviceProviderId)**
   - Execute stored procedure: "usp_Telegence_Get_BillingAccountsByProviderId"
   - Returns List<TelegenceBillingAccount>

#### API Communication Methods:
1. **UpdateTelegenceDeviceStatus(logger, base64Service, telegenceAuthentication, isProduction, request, endpoint, proxyUrl)**
   - HTTP POST to Telegence API for device status updates
   - Supports both proxy and direct calls

2. **UpdateTelegenceSubscriber(logger, base64Service, telegenceAuthentication, isProduction, request, subscriberNo, endpoint, proxyUrl)**
   - HTTP PATCH to Telegence API for subscriber updates

3. **UpdateTelegenceMobilityConfiguration(logger, base64Service, telegenceAuthentication, isProduction, request, subscriberNo, endpoint, proxyUrl)**
   - HTTP PATCH to Telegence API for mobility configuration

4. **GetTelegenceDeviceBySubscriberNumber(context, telegenceAuthentication, isProduction, subscriberNo, endpoint, proxyUrl)**
   - HTTP GET to retrieve device details by subscriber number
   - Async operation with retry logic

5. **TelegenceGetDetailDataUsage(logger, base64Service, telegenceAuthentication, isProduction, subscriberNo, endpoint, proxyUrl)**
   - HTTP GET to retrieve data usage details
   - Async operation

6. **GetTelegenceDevicesAsync(context, syncState, proxyUrl, telegenceDeviceList, deviceDetailEndpoint, pageSize)**
   - Paginated retrieval of device list
   - Manages sync state and pagination

7. **GetBanStatusAsync(context, telegenceAuth, proxyUrl, ban, telegenceBanDetailGetURL)**
   - Retrieve billing account status by BAN

#### Private Helper Methods:
1. **GetTelegenceDeviceBySubscriberNumberByProxy(context, telegenceAuthentication, deviceDetailUrl, proxyUrl, baseUrl)**
2. **GetTelegenceDeviceBySubscriberNumberWithoutProxy(context, telegenceAuthentication, deviceDetailUrl)**
3. **GetTelegenceDevicesAsyncByProxy(context, syncState, telegenceAuth, proxyUrl, telegenceDeviceList, pageSize, deviceDetailUrl, baseUrl)**
4. **GetTelegenceDevicesAsyncWithoutProxy(context, telegenceAuth, syncState, telegenceDeviceList, telegenceDevicesGetUrl, pageSize)**
5. **GetBanStatusAsyncByProxy(context, telegenceAuth, banDetailUrl, baseUrl, proxyUrl)**
6. **GetBanStatusAsyncWithoutProxy(context, telegenceAuth, banDetailUrl)**
7. **BuildRequestHeaders(client, telegenceAuth)** - HTTP header construction
8. **BuildHeaderContent(telegenceAuth)** - Header content for proxy calls
9. **BuildPayloadModel(endPoint, baseUrl, headerContentString)** - Payload model creation
10. **ConfigHttpClient(httpClient)** - HTTP client configuration
11. **MappingProxyResponseContent(proxyResponseContent)** - Response mapping
12. **GetBillingAccountStatus(responseBody)** - Extract status from response
13. **GetTelegenceDeviceList(deviceList, telegenceDeviceList, syncState, refreshTimestamp)** - Device list processing

### SqlQueryHelper.cs

#### Generic SQL Execution Methods:
1. **ExecuteStoredProcedureWithListResult<T>(logFunction, connectionString, storedProcedureName, parseFunction, parameters, commandTimeout, shouldThrowOnException)**
   - Execute stored procedure and return list of objects
   - Generic method with custom parsing function

2. **ExecuteStoredProcedureWithRowCountResult(logFunction, connectionString, storedProcedureName, parameters, commandTimeout, shouldExceptionOnNoRowsAffected, shouldThrowOnException)**
   - Execute stored procedure and return affected row count
   - Useful for INSERT/UPDATE/DELETE operations

3. **ExecuteStoredProcedureWithIntResult(logFunction, connectionString, storedProcedureName, parameters, commandTimeout, shouldExceptionOnNoRowsAffected, defaultValue, shouldThrowOnException)**
   - Execute stored procedure and return integer result
   - Uses ExecuteScalar() for single value results

4. **ExecuteStoredProcedureWithSingleValueResult<T>(logFunction, connectionString, storedProcedureName, outputParamName, defaultValue, parameters, commandTimeout, shouldExceptionOnNoRowsAffected, shouldThrowOnException)**
   - Execute stored procedure with output parameter
   - Returns single value of specified type

#### Private Helper Methods:
1. **CheckAllExecuteStoredProcedureParameters(logFunction, connectionString, storedProcedureName)** - Parameter validation
2. **CheckValidStringParameter(parameter, parameterName)** - String parameter validation
3. **CheckValidObjectParameter(parameter, parameterName)** - Object parameter validation

#### Extension Methods:
1. **CloneParameters(parameters)** - Create deep copy of SqlParameter list

### TelegenceAPIClient.cs

#### Device Management Methods:
1. **ActivateDevicesAsync(telegenceActivationRequest, activationEndpoint)**
   - Activate devices via Telegence API
   - Returns DeviceChangeResult with activation response

2. **ActivateDevicesAsync(telegenceActivationRequest, activationEndpoint, httpClient)** (Overload)
   - Same as above but with provided HttpClient

3. **UpdateSubscriberAsync(telegenceSubscriberUpdateRequest, subscriberUpdateEndpoint)**
   - Update subscriber information
   - HTTP PATCH operation

4. **UpdateTelegenceMobilityConfigurationAsync(telegenceConfigurationUpdateRequest, subscriberUpdateEndpoint)**
   - Update mobility configuration
   - Apply/remove SOC codes

5. **UpdateCarrierRatePlanAsync(carrierRatePlanUpdateRequest, subscriberUpdateEndpoint)**
   - Update carrier rate plan
   - HTTP PATCH operation

6. **CheckActivationStatus(activationCheckEndpoint)**
   - Check device activation status
   - HTTP GET operation

7. **CheckActivationStatus(activationCheckEndpoint, httpClient)** (Overload)
   - Same as above but with provided HttpClient

8. **GetDeviceDetails(deviceDetailsEndpoint, retryNumber)**
   - Retrieve device details
   - Includes retry logic with Polly

9. **GetDeviceDetails(deviceDetailsEndpoint, httpClient)** (Overload)
   - Same as above but with provided HttpClient

#### IP Management Methods:
1. **RequestIPAddresses(apiEndpoint, telegenceIPAdressesRequest, httpClient)**
   - Request IP addresses from Telegence
   - HTTP POST operation

2. **SubmitIPProvisioningRequest(apiEndpoint, telegenceIPProvisionRequest, httpClient)**
   - Submit IP provisioning request
   - HTTP POST operation

3. **CheckIPProvisioningStatus(apiEndpoint, batchId, subscriberNumber, httpClient)**
   - Check IP provisioning status
   - HTTP GET with batch-id or subscriber-number header

4. **GetBillingAccountNumber(ban, endpoindUrl)**
   - Get billing account details by BAN
   - Includes retry logic

#### Private Helper Methods:
1. **ToTelegenceActivationProxyResponse(response)** - Response conversion
2. **CreatePayloadModel(json, baseUri, endpoint)** - Payload creation for proxy calls
3. **CreateHeaderOnlyPayloadModel(baseUri, endpoint, headerParams)** - Header-only payload creation
4. **BuildHeaderContent(telegenceAuth)** - Build header content dictionary
5. **MappingHttpResponseContent(httpResponseMessage)** - Map HTTP response to internal model

### TelegenceServicegGetCharacteristicHelper.cs

#### Service Characteristic Extraction:
1. **Constructor(TelegenceDeviceDetailResponse deviceDetail)**
   - Extracts all service characteristics from device detail response
   - Parses dates and maps characteristics to properties

#### Properties Extracted:
- **activatedDate** - Subscriber activation date
- **singleUserCodeCharacteristic** - Single user code
- **singleUserDescCharacteristic** - Single user code description
- **serviceZipCodeCharacteristic** - Service ZIP code
- **nextBillCycleDate** - Next billing cycle date
- **iccidCharacteristic** - SIM card ICCID
- **imeiCharacteristic** - Device IMEI
- **deviceMakeCharacteristic** - Device brand/make
- **deviceModelCharacteristic** - Device model
- **imeiTypeCharacteristic** - IMEI type
- **dataGroupIdCharacteristic** - Data group ID
- **contactNameCharacteristic** - Contact name
- **techTypeNameCharacteristic** - Technology type
- **ipAddressCharacteristic** - IP address
- **statusEffectiveDate** - Status effective date

---

## Error Handling and Retry Logic

### Retry Patterns:
1. **SQL Retry Policy** - Uses Polly for transient SQL exceptions
2. **HTTP Retry Policy** - Built into TelegenceAPIClient for API calls
3. **Proxy Retry Policy** - Separate retry logic for proxy-based calls

### Exception Handling:
1. **SqlException** - Database connectivity and query issues
2. **InvalidOperationException** - Database connection problems
3. **JsonException** - API response parsing errors
4. **TimeoutException** - Operation timeout handling
5. **HttpRequestException** - HTTP communication errors

### Logging Strategy:
- All operations include comprehensive logging
- Different log levels: INFO, WARNING, EXCEPTION
- Context-aware logging with file, line, and function information
- Structured logging for better troubleshooting

---

## Data Flow Summary

1. **Initialization**: Lambda receives SQS message with processing instructions
2. **Setup**: Initialize database connections and API clients
3. **Data Retrieval**: Query database for subscriber numbers to process
4. **API Calls**: Retrieve device details from Telegence API
5. **Data Processing**: Extract and transform device characteristics
6. **Storage**: Bulk insert processed data into staging tables
7. **Cleanup**: Remove processed records and queue next batch
8. **Continuation**: Send message to SQS for next group processing

This architecture supports high-throughput processing with proper error handling, retry logic, and scalable batch processing through SQS messaging.