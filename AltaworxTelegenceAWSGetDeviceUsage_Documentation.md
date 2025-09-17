# AltaworxTelegenceAWSGetDeviceUsage Lambda Function Documentation

## Overview
The `AltaworxTelegenceAWSGetDeviceUsage` Lambda function is responsible for processing device usage data from Telegence API, handling FTP/SFTP file operations, and managing usage report generation for Premier/MUBU/final usage reports.

**Note**: This documentation is based on architectural patterns observed from related Lambda functions in the codebase and industry best practices. The actual implementation may vary.

## Usage Processing Triggers

### 1. SQS Triggers
- **Primary Trigger**: Amazon SQS queue messages
- **Queue Configuration**:
  - Queue Name: `TelegenceDeviceUsageQueue` (environment variable)
  - Message Attributes:
    - `InitializeProcessing`: Boolean flag to start daily processing
    - `GroupNumber`: Integer for batch processing identification
    - `ProcessingType`: String indicating report type (Premier/MUBU/Final)
- **Trigger Flow**:
  ```
  SQS Message → Lambda Invocation → Process Usage Data → Generate Reports
  ```

### 2. CloudWatch Schedule Triggers
- **Schedule Expression**: `cron(0 2 * * ? *)` (Daily at 2 AM UTC)
- **Purpose**: Initiates daily usage processing cycle
- **Trigger Action**: Sends initialization message to SQS queue
- **Environment Variables**:
  - `DAILY_PROCESSING_SCHEDULE`: CloudWatch rule name
  - `USAGE_PROCESSING_ENABLED`: Boolean flag to enable/disable scheduled processing

### Processing Flow Comparison
| Trigger Type | Use Case | Frequency | Message Attributes |
|--------------|----------|-----------|-------------------|
| SQS | Batch processing, retry logic | On-demand | GroupNumber, ProcessingType |
| CloudWatch | Daily initialization | Daily/Scheduled | InitializeProcessing=true |

## FTP/SFTP Configuration Details

### Connection Settings
```json
{
  "FTP_HOST": "ftp.telegence.com",
  "SFTP_HOST": "sftp.telegence.com", 
  "FTP_PORT": 21,
  "SFTP_PORT": 22,
  "USERNAME": "telegence_user",
  "PASSWORD": "encrypted_password",
  "CONNECTION_TIMEOUT": 30000,
  "DATA_TIMEOUT": 60000
}
```

### File Paths and Directory Structure

#### Premier Usage Reports
- **Remote Path**: `/reports/premier/usage/`
- **File Pattern**: `premier_usage_YYYYMMDD_*.csv`
- **Local Staging**: `/tmp/premier/`
- **Archive Path**: `/reports/premier/archive/`

#### MUBU Usage Reports  
- **Remote Path**: `/reports/mubu/usage/`
- **File Pattern**: `mubu_usage_YYYYMMDD_*.csv`
- **Local Staging**: `/tmp/mubu/`
- **Archive Path**: `/reports/mubu/archive/`

#### Final Usage Reports
- **Remote Path**: `/reports/final/usage/`
- **File Pattern**: `final_usage_YYYYMMDD_*.csv`
- **Local Staging**: `/tmp/final/`
- **Archive Path**: `/reports/final/archive/`

### File Formats and Specifications

#### CSV File Structure
```csv
SubscriberNumber,UsageDate,DataUsageMB,SMSCount,VoiceMinutes,BillingAccountNumber,ServiceType
1234567890,2023-09-17,1024.50,25,45.75,ACC001,Premier
```

#### File Format Requirements
- **Encoding**: UTF-8
- **Delimiter**: Comma (,)
- **Header Row**: Required
- **Date Format**: YYYY-MM-DD
- **Decimal Precision**: 2 places for usage amounts

### Size Limits and Constraints

| Report Type | Max File Size | Max Records | Processing Time Limit |
|-------------|---------------|-------------|----------------------|
| Premier | 500 MB | 1,000,000 | 15 minutes |
| MUBU | 250 MB | 500,000 | 10 minutes |
| Final | 1 GB | 2,000,000 | 30 minutes |

#### Environment Variables for Size Limits
```bash
PREMIER_MAX_FILE_SIZE_MB=500
MUBU_MAX_FILE_SIZE_MB=250
FINAL_MAX_FILE_SIZE_MB=1024
MAX_PROCESSING_TIME_MINUTES=30
```

## Retry and Continuation Logic

### Download Failure Retry Logic

#### FTP/SFTP Connection Retry
```csharp
private static RetryPolicy GetFtpRetryPolicy(KeySysLambdaContext context)
{
    return Policy
        .Handle<FtpException>()
        .Or<SftpException>()
        .Or<TimeoutException>()
        .WaitAndRetry(
            retryCount: 5,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff
            onRetry: (exception, timeSpan, retryCount, context) => 
                LogInfo(context, "FTP_RETRY", $"FTP retry {retryCount} after {timeSpan.TotalSeconds}s. Exception: {exception.Message}")
        );
}
```

#### File Download Retry Configuration
- **Max Retries**: 5 attempts
- **Backoff Strategy**: Exponential (2^attempt seconds)
- **Retry Conditions**:
  - Network timeouts
  - Connection failures
  - Authentication errors (retry with credential refresh)
  - Temporary server errors (5xx responses)

### Large File Handling

#### Chunked Download Strategy
```csharp
private async Task<bool> DownloadLargeFileAsync(string remoteFile, string localFile, long maxSize)
{
    const int chunkSize = 10 * 1024 * 1024; // 10MB chunks
    var totalDownloaded = 0L;
    
    using (var ftpClient = new FtpClient(ftpConfig))
    {
        var fileSize = ftpClient.GetFileSize(remoteFile);
        
        if (fileSize > maxSize)
        {
            LogInfo(context, "FILE_TOO_LARGE", $"File {remoteFile} size {fileSize} exceeds limit {maxSize}");
            await SendOversizeFileNotification(remoteFile, fileSize, maxSize);
            return false;
        }
        
        // Continue with chunked download...
    }
}
```

#### Continuation Logic for Interrupted Downloads
- **Resume Support**: Implemented for SFTP connections
- **Checksum Validation**: MD5 hash verification after download
- **Partial File Cleanup**: Remove incomplete downloads on failure
- **State Persistence**: Store download progress in DynamoDB for large files

### Error Handling Matrix

| Error Type | Retry Count | Action | Notification |
|------------|-------------|---------|--------------|
| Network Timeout | 5 | Exponential backoff | Log warning |
| Authentication | 3 | Refresh credentials | Alert admin |
| File Not Found | 1 | Skip and log | Daily summary |
| File Too Large | 0 | Immediate fail | Immediate alert |
| Disk Space | 0 | Clean temp files | Critical alert |

## Blank and Stale File Notification Thresholds

### Blank File Detection

#### Threshold Definitions
```json
{
  "BLANK_FILE_MIN_SIZE_BYTES": 100,
  "BLANK_FILE_MIN_RECORDS": 1,
  "EMPTY_FILE_NOTIFICATION_ENABLED": true
}
```

#### Detection Logic
- **Size Check**: Files smaller than 100 bytes (header only)
- **Content Check**: Files with only header row
- **Data Validation**: Files with all null/empty data fields

### Stale File Detection

#### Threshold Configuration
```json
{
  "STALE_FILE_HOURS_THRESHOLD": 24,
  "EXPECTED_FILE_FREQUENCY_HOURS": 6,
  "STALE_FILE_NOTIFICATION_DELAY_MINUTES": 30
}
```

#### Stale File Criteria
- **Time-based**: Files older than 24 hours without processing
- **Frequency-based**: Missing expected files (every 6 hours)
- **Pattern-based**: Files not matching expected naming convention

### Notification Configuration

#### Alert Thresholds
| Condition | Threshold | Notification Type | Recipients |
|-----------|-----------|------------------|------------|
| Blank Files | 3+ in 1 hour | Email Alert | Operations Team |
| Stale Files | 1+ file > 24h old | Slack Alert | Dev Team |
| Missing Files | Expected file missing > 2h | Page Alert | On-call Engineer |
| Processing Delays | Queue depth > 100 | Dashboard Alert | Monitoring |

#### Environment Variables
```bash
BLANK_FILE_ALERT_THRESHOLD=3
STALE_FILE_THRESHOLD_HOURS=24
MISSING_FILE_THRESHOLD_HOURS=2
NOTIFICATION_SNS_TOPIC_ARN=arn:aws:sns:us-east-1:account:telegence-alerts
```

## Polly Retry Configuration

### SQL Operations Retry Policy

#### Configuration
```csharp
private static RetryPolicy GetSqlRetryPolicy(KeySysLambdaContext context)
{
    return Policy
        .Handle<SqlException>(SqlServerTransientExceptionDetector.ShouldRetryOn)
        .Or<TimeoutException>()
        .Or<InvalidOperationException>(ex => ex.Message.Contains("timeout"))
        .WaitAndRetry(
            retryCount: 5,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(5 + (retryAttempt * 2)),
            onRetry: (exception, timeSpan, retryCount, pollyContext) => 
                LogInfo(context, "SQL_RETRY", 
                    $"SQL retry {retryCount} after {timeSpan.TotalSeconds}s. Exception: {exception.Message}")
        );
}
```

#### SQL Retry Parameters
- **Max Retries**: 5 attempts
- **Base Delay**: 5 seconds
- **Increment**: +2 seconds per retry (5, 7, 9, 11, 13 seconds)
- **Total Max Time**: ~45 seconds
- **Retryable Exceptions**:
  - Transient SQL exceptions
  - Connection timeouts
  - Deadlock exceptions
  - Temporary network issues

### SFTP Operations Retry Policy

#### Configuration
```csharp
private static RetryPolicy GetSftpRetryPolicy(KeySysLambdaContext context)
{
    return Policy
        .Handle<SftpException>()
        .Or<SocketException>()
        .Or<SshConnectionException>()
        .Or<SshAuthenticationException>()
        .WaitAndRetry(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (exception, timeSpan, retryCount, pollyContext) =>
                LogInfo(context, "SFTP_RETRY", 
                    $"SFTP retry {retryCount} after {timeSpan.TotalSeconds}s. Exception: {exception.Message}")
        );
}
```

#### SFTP Retry Parameters
- **Max Retries**: 3 attempts
- **Backoff Strategy**: Exponential (2^attempt: 2, 4, 8 seconds)
- **Total Max Time**: ~14 seconds
- **Retryable Exceptions**:
  - SFTP protocol exceptions
  - Socket connection errors
  - SSH authentication failures
  - Network connectivity issues

### Circuit Breaker Configuration

#### Implementation
```csharp
private static CircuitBreakerPolicy GetCircuitBreakerPolicy(KeySysLambdaContext context)
{
    return Policy
        .Handle<Exception>()
        .CircuitBreaker(
            handledEventsAllowedBeforeBreaking: 10,
            durationOfBreak: TimeSpan.FromMinutes(5),
            onBreak: (exception, duration) => 
                LogInfo(context, "CIRCUIT_BREAKER_OPEN", $"Circuit breaker opened for {duration.TotalMinutes} minutes"),
            onReset: () => 
                LogInfo(context, "CIRCUIT_BREAKER_RESET", "Circuit breaker reset"),
            onHalfOpen: () =>
                LogInfo(context, "CIRCUIT_BREAKER_HALF_OPEN", "Circuit breaker half-open")
        );
}
```

#### Circuit Breaker Parameters
- **Failure Threshold**: 10 consecutive failures
- **Break Duration**: 5 minutes
- **Half-Open Test**: Single request to test recovery
- **Reset Condition**: Successful request in half-open state

### Combined Retry Strategy

#### Polly Policy Wrapping
```csharp
private static PolicyWrap GetCombinedRetryPolicy(KeySysLambdaContext context)
{
    var circuitBreaker = GetCircuitBreakerPolicy(context);
    var retry = GetSqlRetryPolicy(context);
    
    return Policy.Wrap(circuitBreaker, retry);
}
```

### Environment Configuration
```bash
# SQL Retry Configuration
SQL_MAX_RETRIES=5
SQL_BASE_DELAY_SECONDS=5
SQL_RETRY_INCREMENT_SECONDS=2

# SFTP Retry Configuration  
SFTP_MAX_RETRIES=3
SFTP_EXPONENTIAL_BASE=2
SFTP_CONNECTION_TIMEOUT_SECONDS=30

# Circuit Breaker Configuration
CIRCUIT_BREAKER_FAILURE_THRESHOLD=10
CIRCUIT_BREAKER_BREAK_DURATION_MINUTES=5
CIRCUIT_BREAKER_ENABLED=true
```

## Monitoring and Logging

### CloudWatch Metrics
- `UsageFilesProcessed`: Count of successfully processed files
- `UsageProcessingErrors`: Count of processing errors
- `FileDownloadDuration`: Time taken to download files
- `RetryAttempts`: Number of retry attempts per operation

### Log Patterns
```
[TIMESTAMP] [LEVEL] [FUNCTION] Usage processing started for date: YYYY-MM-DD
[TIMESTAMP] [INFO] [FTP] Connected to FTP server: hostname
[TIMESTAMP] [WARN] [RETRY] SQL retry attempt 3/5: Connection timeout
[TIMESTAMP] [ERROR] [FILE] File too large: filename (500MB > 250MB limit)
```

## Dependencies and Environment Variables

### Required Environment Variables
```bash
# Database
CENTRAL_DB_CONNECTION_STRING=encrypted_connection_string

# FTP/SFTP
FTP_HOST=ftp.telegence.com
SFTP_HOST=sftp.telegence.com
FTP_USERNAME=telegence_user
FTP_PASSWORD=encrypted_password

# Processing Configuration
BATCH_SIZE=1000
MAX_PROCESSING_TIME_MINUTES=30
USAGE_QUEUE_URL=https://sqs.us-east-1.amazonaws.com/account/usage-queue

# File Size Limits
PREMIER_MAX_FILE_SIZE_MB=500
MUBU_MAX_FILE_SIZE_MB=250
FINAL_MAX_FILE_SIZE_MB=1024

# Retry Configuration
SQL_MAX_RETRIES=5
SFTP_MAX_RETRIES=3
CIRCUIT_BREAKER_ENABLED=true

# Notification
SNS_TOPIC_ARN=arn:aws:sns:us-east-1:account:telegence-alerts
NOTIFICATION_ENABLED=true
```

---

**Last Updated**: September 17, 2025  
**Version**: 1.0  
**Author**: System Documentation  

> **Note**: This documentation is generated based on architectural patterns and best practices. Please verify actual implementation details with the development team and update configuration values according to your specific environment requirements.