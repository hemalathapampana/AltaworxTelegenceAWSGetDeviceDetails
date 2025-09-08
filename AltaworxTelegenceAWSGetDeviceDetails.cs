using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Altaworx.AWS.Core;
using Altaworx.AWS.Core.Models;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amop.Core.Constants;
using Amop.Core.Models.Telegence;
using Amop.Core.Models.Telegence.Api;
using Amop.Core.Resilience;
using Amop.Core.Services.Base64Service;
using Amop.Core.Services.Http;
using Amop.Core.Services.Telegence;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AltaworxTelegenceAWSGetDeviceDetails
{
    public class Function : AwsFunctionBase
    {
        private string TelegenceDeviceDetailGetURL = Environment.GetEnvironmentVariable("TelegenceDeviceDetailGetURL");
        private string TelegenceDeviceDetailQueueURL = Environment.GetEnvironmentVariable("TelegenceDeviceDetailQueueURL");
        private string ProxyUrl = Environment.GetEnvironmentVariable("ProxyUrl");
        private int BatchSize = Convert.ToInt32(Environment.GetEnvironmentVariable("BatchSize"));
        private const int MaxRetries = 5;
        private const int RetryDelaySeconds = 5;
        /// <summary>
        /// This method is called for every Lambda invocation. This method takes in an SQS event object and can be used 
        /// to respond to SQS messages.
        /// </summary>
        /// <param name="sqsEvent"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
        {
            KeySysLambdaContext keysysContext = null;
            try
            {
                keysysContext = BaseFunctionHandler(context);

                if (string.IsNullOrEmpty(TelegenceDeviceDetailGetURL))
                {
                    TelegenceDeviceDetailGetURL = context.ClientContext.Environment["TelegenceDeviceDetailGetURL"];
                    TelegenceDeviceDetailQueueURL = context.ClientContext.Environment["TelegenceDeviceDetailQueueURL"];
                    ProxyUrl = context.ClientContext.Environment["ProxyUrl"];
                    BatchSize = Convert.ToInt32(context.ClientContext.Environment["BatchSize"]);
                }

                if (sqsEvent?.Records != null)
                {
                    LogInfo(keysysContext, "STATUS", $"Beginning to process {sqsEvent.Records.Count} records...");

                    foreach (var record in sqsEvent.Records)
                    {
                        LogInfo(keysysContext, "MessageId", record.MessageId);
                        LogInfo(keysysContext, "EventSource", record.EventSource);
                        LogInfo(keysysContext, "Body", record.Body);

                        var initializeProcessing = false;
                        if (record.MessageAttributes.ContainsKey("InitializeProcessing"))
                        {
                            initializeProcessing = Convert.ToBoolean(record.MessageAttributes["InitializeProcessing"].StringValue);
                            LogInfo(keysysContext, "InitializeProcessing", initializeProcessing.ToString());
                        }

                        var groupNumber = 0;
                        if (record.MessageAttributes.ContainsKey("GroupNumber"))
                        {
                            groupNumber = Convert.ToInt32(record.MessageAttributes["GroupNumber"].StringValue);
                            LogInfo(keysysContext, "GroupNumber", groupNumber);
                        }

                        if (initializeProcessing)
                        {
                            try
                            {
                                await StartDailyDeviceDetailProcessingAsync(keysysContext);
                            }
                            catch (Exception ex)
                            {

                                LogInfo(keysysContext, "EXCEPTION", ex.Message);
                            }
                        }
                        else
                        {
                            try
                            {
                                await ProcessDetailListAsync(keysysContext, groupNumber);
                            }
                            catch (Exception ex)
                            {
                                LogInfo(keysysContext, "EXCEPTION", JsonConvert.SerializeObject(ex));
                            }
                        }
                    }

                    LogInfo(keysysContext, "STATUS", $"Processed {sqsEvent.Records.Count} records.");
                }
            }
            catch (Exception ex)
            {
                LogInfo(keysysContext, "EXCEPTION", ex.Message);
            }

            CleanUp(keysysContext);
        }

        private async Task StartDailyDeviceDetailProcessingAsync(KeySysLambdaContext context)
        {
            //Call proc
            CallDailyGetDeviceDetailSP(context);
            // get group count
            int groupCount = GetGroupCount(context);
            //Add next message to queue
            await SendProcessMessagesToQueueAsync(context, groupCount);
        }

        private void CallDailyGetDeviceDetailSP(KeySysLambdaContext context)
        {
            using (var con = new SqlConnection(context.CentralDbConnectionString))
            {
                using (var cmd = new SqlCommand("usp_Telegence_Devices_GetDetailFilter", con)
                {
                    CommandType = CommandType.StoredProcedure
                })
                {
                    con.Open();
                    cmd.Parameters.AddWithValue("@BatchSize", BatchSize);
                    cmd.ExecuteNonQuery();
                    con.Close();
                }
            }
        }

        private int GetGroupCount(KeySysLambdaContext context)
        {
            int groupCount = 0;
            using (var con = new SqlConnection(context.CentralDbConnectionString))
            {
                using (var cmd = new SqlCommand("SELECT MAX(GroupNumber) FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE IsDeleted = 0", con)
                {
                    CommandType = CommandType.Text
                })
                {
                    con.Open();
                    var scalarResult = cmd.ExecuteScalar();
                    if (scalarResult != null && scalarResult != DBNull.Value)
                    {
                        groupCount = (int)scalarResult;
                    }
                }
                // Close destination connection
                con.Close();
            }

            LogInfo(context, "Group Count", groupCount);
            return groupCount;
        }

        private async Task SendProcessMessagesToQueueAsync(KeySysLambdaContext context, int groupCount)
        {
            LogInfo(context, "INFO", "Start execute RemovePreviouslyStagedFeatures");
            RemovePreviouslyStagedFeatures(context);
            LogInfo(context, "INFO", "End execute RemovePreviouslyStagedFeatures");

            for (int iGroup = 0; iGroup <= groupCount; iGroup++)
            {
                await SendProcessMessageToQueueAsync(context, iGroup);
            }
        }

        private async Task SendProcessMessageToQueueAsync(KeySysLambdaContext context, int groupNumber)
        {
            LogInfo(context, "SUB", "SendProcessMessageToQueueAsync");
            LogInfo(context, "InitializeProcessing", false);
            LogInfo(context, "DeviceDetailQueueURL", TelegenceDeviceDetailQueueURL);
            LogInfo(context, "BatchSize", BatchSize.ToString());
            LogInfo(context, "GroupNumber", groupNumber.ToString());

            if (string.IsNullOrEmpty(TelegenceDeviceDetailQueueURL))
            {
                return; // so we don't have to enqueue messages during a test
            }

            using (var client = new AmazonSQSClient(AwsCredentials(context), RegionEndpoint.USEast1))
            {
                var request = new SendMessageRequest
                {
                    DelaySeconds = (int)TimeSpan.FromSeconds(5).TotalSeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {"InitializeProcessing", new MessageAttributeValue {DataType = "String", StringValue = false.ToString()}},
                        {"GroupNumber", new MessageAttributeValue {DataType = "String", StringValue = groupNumber.ToString()}}
                    },
                    MessageBody = $"Requesting next {BatchSize} records to process",
                    QueueUrl = TelegenceDeviceDetailQueueURL
                };
                LogInfo(context, "MessageBody", request.MessageBody);

                var response = await client.SendMessageAsync(request);
                LogInfo(context, "RESPONSE STATUS", $"{response.HttpStatusCode:d} {response.HttpStatusCode:g}");
            }
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Reviewed")]
        private async Task ProcessDetailListAsync(KeySysLambdaContext context, int groupNumber)
        {
            LogInfo(context, "BatchSize", BatchSize);

            //do loop from proc to get list of items, limit to batch size\
            List<string> subscriberNumbers = new List<string>(BatchSize);
            var sqlRetryPolicy = GetSqlRetryPolicy(context);
            int rowCount = 0;
            int serviceProviderId = 0;
            sqlRetryPolicy.Execute(() =>
            {
                using (var con = new SqlConnection(context.CentralDbConnectionString))
                {
                    string cmdText = $"SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE GroupNumber = @GroupNumber AND [IsDeleted] = 0";
                    if (groupNumber < 0)
                    {
                        cmdText = $"SELECT TOP {BatchSize} [SubscriberNumber], [ServiceProviderId] FROM [dbo].[TelegenceDeviceDetailIdsToProcess] WITH (NOLOCK) WHERE [IsDeleted] = 0";
                    }
                    using (var cmd = new SqlCommand(cmdText, con))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.Parameters.AddWithValue("@GroupNumber", groupNumber);
                        cmd.CommandTimeout = 600;
                        con.Open();
                        var rdr = cmd.ExecuteReader();
                        if (rdr != null && rdr.HasRows)
                        {
                            while (rdr.Read())
                            {
                                subscriberNumbers.Add(rdr[0].ToString());
                                serviceProviderId = int.Parse(rdr[1].ToString());

                                rowCount++;
                                if (rowCount == BatchSize)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            });

            if (rowCount <= 0)
            {
                return;
            }

            var telegenceAuth = TelegenceCommon.GetTelegenceAuthenticationInformation(context.CentralDbConnectionString, serviceProviderId);
            var telegenceAPIAuth = new TelegenceAPIAuthentication(new Base64Service())
            {
                ClientId = telegenceAuth.ClientId,
                ClientSecret = telegenceAuth.Password,
                ProductionURL = telegenceAuth.ProductionUrl,
                SandboxURL = telegenceAuth.SandboxUrl,
                WriteIsEnabled = telegenceAuth.WriteIsEnabled
            };
            var table = new TelegenceDeviceDetailSyncTable();
            var featureStagingTable = new TelegenceDeviceFeatureSyncTable();
            var client = new TelegenceAPIClient(new SingletonHttpClientFactory(), new Amop.Core.Services.Http.HttpRequestFactory(), telegenceAPIAuth, context.IsProduction, ProxyUrl, context.logger);
            foreach (var subscriberNumber in subscriberNumbers)
            {
                var remainingTime = context.Context.RemainingTime.TotalSeconds;
                if (remainingTime > CommonConstants.REMAINING_TIME_CUT_OFF)
                {
                    try
                    {
                        LogInfo(context, CommonConstants.INFO, $"Get Detail for Subscriber Number : {subscriberNumber}");
                        string deviceDetailUrl = string.Format(TelegenceDeviceDetailGetURL, subscriberNumber);
                        var apiResult = await client.GetDeviceDetails(deviceDetailUrl, MaxRetries);
                        var deviceDetail = apiResult?.ResponseObject?.TelegenceDeviceDetailResponse;
                        // add to data table
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
                            LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
                            sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
                        }
                    }
                    catch (Exception e)
                    {
                        LogInfo(context, CommonConstants.EXCEPTION, $"Error getting device detail {JsonConvert.SerializeObject(e)}");
                        LogInfo(context, CommonConstants.INFO, $"Start RemoveDeviceFromQueue");
                        sqlRetryPolicy.Execute(() => RemoveDeviceFromQueue(context.CentralDbConnectionString, groupNumber, subscriberNumber));
                        continue;
                    }
                }
                else
                {
                    LogInfo(context, CommonConstants.INFO, $"Remaining run time ({remainingTime} seconds) is not enough to continue.");
                    break;
                }
            }

            // load staging table
            if (table.HasRows())
            {
                LogInfo(context, "INFO", $"Start Bulk Copy TelegenceDeviceDetailSyncTable");
                SqlBulkCopy(context, context.CentralDbConnectionString, table.DataTable, table.DataTable.TableName);

                LogInfo(context, "INFO", $"Start execute RemoveStagedDevices");
                sqlRetryPolicy.Execute(() => RemoveStagedDevices(context, groupNumber));
            }

            //Load feature staging table
            if (featureStagingTable.HasRows())
            {
                LogInfo(context, "INFO", $"Start execute BulkCopy TelegenceDeviceFeatureSyncTable");
                SqlBulkCopy(context, context.CentralDbConnectionString, featureStagingTable.DataTable, featureStagingTable.DataTable.TableName);
            }

            await SendProcessMessageToQueueAsync(context, groupNumber);
        }

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

                using (var cmd = new SqlCommand(cmdText, con)
                {
                    CommandType = CommandType.Text
                })
                {
                    con.Open();
                    cmd.Parameters.AddWithValue("@GroupNumber", groupNumber);
                    cmd.CommandTimeout = 800;
                    cmd.ExecuteNonQuery();
                }
            }
        }

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

        private static IEnumerable<string> GetDeviceOfferingCodes(TelegenceDeviceDetailResponse deviceDetail)
        {
            if (deviceDetail?.ServiceCharacteristic == null)
            {
                return new List<string>();
            }

            return deviceDetail.ServiceCharacteristic.Where(x => x.Name.StartsWith("offeringCode")).Select(x => x.Value).ToList();
        }

        private void RemovePreviouslyStagedFeatures(KeySysLambdaContext context)
        {
            using (var con = new SqlConnection(context.CentralDbConnectionString))
            {
                string cmdText = "TRUNCATE TABLE [dbo].[TelegenceDeviceMobilityFeature_Staging]";
                using (var cmd = new SqlCommand(cmdText, con)
                {
                    CommandType = CommandType.Text
                })
                {
                    con.Open();
                    cmd.CommandTimeout = 800;
                    cmd.ExecuteNonQuery();
                }
            }
        }

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
    }
}
