# Telegence Lambda Functions - Trigger Analysis

## Overview
This document analyzes the trigger mechanisms and SQS message attributes for two AWS Lambda functions in the Telegence system:
1. `AltaworxTelegenceAWSGetDeviceDetails`
2. `AltaworxTelegenceAWSGetDevices`

## AltaworxTelegenceAWSGetDeviceDetails Lambda

### What Triggers This Lambda
**Primary Trigger:** SQS Events
- The Lambda is triggered by SQS messages from the `TelegenceDeviceDetailQueueURL` queue
- **Self-triggering:** The Lambda sends messages to its own queue to continue processing
- **Triggered by AltaworxTelegenceAWSGetDevices:** The GetDevices Lambda sends messages to this queue to initiate device detail processing

### SQS Message Attributes
The following SQS message attributes are processed by this Lambda:

| Attribute Name | Data Type | Purpose | Values |
|----------------|-----------|---------|---------|
| `InitializeProcessing` | String | Determines if this is an initialization run | "true" or "false" |
| `GroupNumber` | String | Identifies the batch group to process | Integer value (0, 1, 2, etc.) |

### Processing Flow
1. **Initialization Phase** (`InitializeProcessing = true`):
   - Calls stored procedure `usp_Telegence_Devices_GetDetailFilter`
   - Gets group count from `TelegenceDeviceDetailIdsToProcess` table
   - Sends multiple messages to queue for each group

2. **Processing Phase** (`InitializeProcessing = false`):
   - Processes devices in batches based on `GroupNumber`
   - Fetches device details from Telegence API
   - Stores data in staging tables
   - Continues processing by sending next message to queue

### Key Environment Variables
- `TelegenceDeviceDetailGetURL`: API endpoint for device details
- `TelegenceDeviceDetailQueueURL`: SQS queue URL for this Lambda
- `BatchSize`: Number of records to process per batch

---

## AltaworxTelegenceAWSGetDevices Lambda

### What Triggers This Lambda
**Primary Trigger:** SQS Events
- The Lambda is triggered by SQS messages from the `TelegenceDestinationQueueGetDevicesURL` queue
- **Self-triggering:** The Lambda sends messages to its own queue to continue processing
- **Manual/Scheduled:** Can be triggered without SQS events (fallback mode)

### SQS Message Attributes
The following SQS message attributes are processed by this Lambda:

| Attribute Name | Data Type | Purpose | Values |
|----------------|-----------|---------|---------|
| `CurrentPage` | String | Current page number for API pagination | Integer value (1, 2, 3, etc.) |
| `HasMoreData` | String | Indicates if more data is available | "true" or "false" |
| `CurrentServiceProviderId` | String | Service provider being processed | Integer value |
| `InitializeProcessing` | String | Determines processing mode | "true" or "false" |
| `IsProcessDeviceNotExistsStaging` | String | Flag for processing non-existent devices | "0" or "1" |
| `IsLastProcessDeviceNotExistsStaging` | String | Flag for last batch of non-existent devices | "0" or "1" |
| `GroupNumber` | String | Batch group identifier | Integer value |
| `RetryNumber` | String | Number of retry attempts | Integer value |

### Processing Flow
1. **Initialization Phase** (`InitializeProcessing = true`):
   - Truncates staging tables
   - Processes BAN (Billing Account Number) status
   - Continues with device list processing

2. **Device List Processing** (`InitializeProcessing = false`):
   - Fetches devices from Telegence API with pagination
   - Saves devices to staging table
   - Handles service provider switching

3. **Device Not Exists Processing** (`IsProcessDeviceNotExistsStaging = "1"`):
   - Processes devices that exist in AMOP but not in API
   - Verifies device status via API calls
   - Updates staging tables accordingly

### Key Environment Variables
- `TelegenceDevicesGetURL`: API endpoint for device list
- `TelegenceDestinationQueueGetDevicesURL`: SQS queue URL for this Lambda
- `TelegenceDeviceDetailQueueURL`: Queue URL for device details Lambda
- `TelegenceDeviceUsageQueueURL`: Queue URL for device usage Lambda
- `BatchSize`: Number of records to process per batch

---

## Lambda Relationship Analysis

### Does GetDevices Lambda Trigger GetDeviceDetails Lambda?
**YES** - The relationship is confirmed through the following evidence:

#### Direct Triggering Scenarios:
1. **End of Processing Completion** (Line 694 in GetDevices):
   ```csharp
   await SendMessageToGetDeviceDetailQueueAsync(context, DeviceDetailQueueURL, delayQueue);
   ```
   - When GetDevices completes all processing phases
   - Sends message to `DeviceDetailQueueURL` with 60-second delay
   - Message includes `InitializeProcessing = true` and `GroupNumber = 0`

#### Triggering Flow:
```
GetDevices Lambda → Completes Processing → Sends SQS Message → GetDeviceDetails Lambda
```

#### Message Attributes Sent:
- `InitializeProcessing`: "true"
- `GroupNumber`: "0"
- `MessageBody`: "Start processing Telegence device details"
- `DelaySeconds`: 60 (gives usage processing time to complete)

### Trigger Confirmation:
- ✅ **GetDevices triggers GetDeviceDetails**: YES (at end of processing)
- ✅ **GetDeviceDetails is self-triggering**: YES (continues its own processing)
- ✅ **Both scenarios occur**: YES (GetDevices initiates, GetDeviceDetails continues)

### Processing Sequence:
1. GetDevices Lambda processes all devices and staging
2. GetDevices triggers Usage Lambda (immediate processing)
3. GetDevices triggers GetDeviceDetails Lambda (60-second delay)
4. GetDeviceDetails Lambda takes over and processes device details in batches
5. GetDeviceDetails continues processing by triggering itself until complete

---

## Summary

Both Lambda functions are primarily **SQS-triggered** with complex message attribute systems for state management. The GetDevices Lambda **does trigger** the GetDeviceDetails Lambda at the completion of its processing cycle, establishing a clear workflow dependency. The GetDeviceDetails Lambda then becomes self-sustaining, processing batches until all device details are retrieved and processed.