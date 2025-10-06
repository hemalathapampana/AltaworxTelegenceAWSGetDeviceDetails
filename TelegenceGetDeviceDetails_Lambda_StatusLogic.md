## Telegence GetDeviceDetails Lambda — Status Retention Logic Review

### TL;DR
- The current GetDeviceDetails Lambda does NOT implement a "retain last known device data for three consecutive sync misses" strategy nor "set status to Unknown after three misses".
- It performs HTTP-level retries within a single Lambda invocation and, on missing/failed detail responses, removes the device from the processing queue. Status transitions like "Unknown" are not handled here.

---

### What the Lambda actually does
- **Trigger/Orchestration**: SQS-driven; optionally seeds work via a stored procedure and then enqueues group messages.
- **Work selection**: Reads a batch of `SubscriberNumber` values from `[dbo].[TelegenceDeviceDetailIdsToProcess]`.
- **Per-subscriber processing**:
  - Builds `TelegenceDeviceDetailGetURL` for the subscriber
  - Calls Telegence API via `TelegenceAPIClient.GetDeviceDetails(...)`
  - On success: stages rows into `TelegenceDeviceDetailStaging` and `TelegenceDeviceMobilityFeature_Staging`
  - On null/error: marks the subscriber as deleted in the work queue (removes it from further processing)
- **Retries present**: HTTP retries (Polly) within one invocation; SQL transient retries for DB ops
- **Not present**: Any cross-day/cross-run retention counter, or a rule to set status to `Unknown` after N (3) daily misses

---

### Key code points

- HTTP retry count is controlled by a Lambda constant (single invocation scope), not a day-over-day retention policy:

```csharp
// AltaworxTelegenceAWSGetDeviceDetails.cs
private const int MaxRetries = 5; // used for HTTP retries in a single run
```

- Device detail fetch and staging path, with removal on null/error:

```csharp
// AltaworxTelegenceAWSGetDeviceDetails.cs (simplified)
var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
var deviceDetail = apiResult?.ResponseObject?.TelegenceDeviceDetailResponse;
if (deviceDetail != null && !string.IsNullOrWhiteSpace(deviceDetail.SubscriberNumber))
{
    table.AddRow(deviceDetail, serviceProviderId);
    foreach (var feature in GetDeviceOfferingCodes(deviceDetail))
    {
        featureStagingTable.AddRow(subscriberNumber, feature);
    }
}
else
{
    // remove from queue on missing detail
    sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(connStr, groupNumber, subscriberNumber));
}
```

- HTTP-level retry is implemented inside the client using Polly; this is not the "3 consecutive daily sync attempts" logic:

```csharp
// TelegenceAPIClient.cs (simplified)
public async Task<DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>> GetDeviceDetails(string endpoint, int retryNumber)
{
    var response = await RetryPolicyHelper
        .PollyRetryHttpRequestAsync(_logger, retryNumber)
        .ExecuteAsync(async () =>
        {
            var request = _httpRequestFactory.BuildRequestMessage(...);
            return await _httpClientFactory.GetClient().SendAsync(request);
        });
    // ... deserialize on success, log on failure ...
}
```

---

### Evidence that "Unknown after 3 misses" is not here
- No counters or state persisted per subscriber for day-over-day missing-detail tracking.
- No code paths that set a subscriber/device status to `Unknown` in this Lambda.
- The only reference to `Unknown` found in this repository is in a separate analysis document illustrating a filter excluding `Unknown` during a different staging step; it is not executable logic in this Lambda.

```sql
-- TelegenceGetDevices_Lambda_Analysis.md (documentation only)
AND SubscriberNumberStatus <> 'Unknown'
```

---

### Implications
- If carrier suspends a SIM and stops returning details, this Lambda will not flip the status to `Unknown`; it will simply stop staging new detail for that subscriber and remove it from the current processing queue on each miss/error.
- Any retention of prior device data and the eventual transition to `Unknown` (after multiple sync cycles) must be implemented elsewhere (e.g., DB procedures or another processing step) — it is not present here.

---

### Options to meet the desired behavior
- **DB-driven miss counter**: Add/maintain `sync_miss_count` per subscriber; increment when detail is missing for a daily run; reset on success; set status to `Unknown` after 3.
- **Lambda-enforced policy**: Instead of removing from queue immediately, persist a miss record and re-enqueue until miss threshold reached; when threshold met, emit a status update event or write to a staging table that downstream SQL uses to set `Unknown`.
- **Last-seen timestamps**: Persist `last_seen_at` and compute `Unknown` if `now - last_seen_at > N days` with configurable `N`.

---

### Files reviewed
- `AltaworxTelegenceAWSGetDeviceDetails.cs`
- `TelegenceAPIClient.cs`
- `TelegenceDeviceDetailSyncTable.cs`
- `TelegenceDeviceFeatureSyncTable.cs`
- `TelegenceServicegGetCharacteristicHelper.cs`
- `TelegenceGetDevices_Lambda_Analysis.md` (documentation)
