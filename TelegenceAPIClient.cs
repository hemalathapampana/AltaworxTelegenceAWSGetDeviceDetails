using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Amop.Core.Constants;
using Amop.Core.Helpers;
using Amop.Core.Logger;
using Amop.Core.Models;
using Amop.Core.Models.DeviceBulkChange;
using Amop.Core.Models.Telegence;
using Amop.Core.Models.Telegence.Api;
using Amop.Core.Services.Http;
using Newtonsoft.Json;

namespace Amop.Core.Services.Telegence
{
    public class TelegenceAPIClient
    {
        private readonly TelegenceAPIAuthentication _authentication;
        private readonly bool _isProduction;
        private readonly string _proxyServer;
        private readonly IKeysysLogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHttpRequestFactory _httpRequestFactory;
        private const string TELEGENCE_DEVICE_DETAIL_RESPONSE = "TelegenceDeviceDetailResponse";

        public TelegenceAPIClient(IHttpClientFactory httpClientFactory, IHttpRequestFactory httpRequestFactory, TelegenceAPIAuthentication authentication, bool isProduction, string proxyServer, IKeysysLogger logger)
        {
            _authentication = authentication;
            _isProduction = isProduction;
            _proxyServer = proxyServer;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _httpRequestFactory = httpRequestFactory;
        }

        public TelegenceAPIClient(TelegenceAPIAuthentication authentication, bool isProduction, string proxyServer, IKeysysLogger logger)
        {
            _authentication = authentication;
            _isProduction = isProduction;
            _proxyServer = proxyServer;
            _logger = logger;
        }

        public async Task<DeviceChangeResult<List<TelegenceActivationRequest>, TelegenceActivationProxyResponse>> ActivateDevicesAsync(List<TelegenceActivationRequest> telegenceActivationRequest, string activationEndpoint)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<List<TelegenceActivationRequest>, TelegenceActivationProxyResponse>()
                {
                    ActionText = $"POST {baseUri}{activationEndpoint}",
                    RequestObject = telegenceActivationRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(telegenceActivationRequest);

            var payloadModel = CreatePayloadModel(jsonContent, baseUri, activationEndpoint);

            var response = await _httpClientFactory.GetClient().PostWithProxyAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = new TelegenceActivationProxyResponse
            {
                IsSuccessful = response.IsSuccessful,
                TelegenceActivationResponse = response.IsSuccessful ? JsonConvert.DeserializeObject<List<TelegenceActivationResponse>>(response.ResponseMessage) : null
            };

            return new DeviceChangeResult<List<TelegenceActivationRequest>, TelegenceActivationProxyResponse>()
            {
                ActionText = $"POST {baseUri}{activationEndpoint}",
                RequestObject = telegenceActivationRequest,
                HasErrors = !response.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<List<TelegenceActivationRequest>, TelegenceActivationProxyResponse>> ActivateDevicesAsync(List<TelegenceActivationRequest> telegenceActivationRequest, string activationEndpoint, HttpClient httpClient)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<List<TelegenceActivationRequest>, TelegenceActivationProxyResponse>()
                {
                    ActionText = $"POST {baseUri}{activationEndpoint}",
                    RequestObject = telegenceActivationRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(telegenceActivationRequest);

            var payloadModel = CreatePayloadModel(jsonContent, baseUri, activationEndpoint);
            var response = await httpClient.PostWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = ToTelegenceActivationProxyResponse(response);

            return new DeviceChangeResult<List<TelegenceActivationRequest>, TelegenceActivationProxyResponse>()
            {
                ActionText = $"POST {baseUri}{activationEndpoint}",
                RequestObject = telegenceActivationRequest,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        private static TelegenceActivationProxyResponse ToTelegenceActivationProxyResponse(ProxyResultBase response)
        {
            var telegenceResponse = new TelegenceActivationProxyResponse
            {
                IsSuccessful = response.IsSuccessful,
            };
            if (response.IsSuccessful)
            {
                try
                {
                    var telegenceActivationResponses = JsonConvert.DeserializeObject<List<TelegenceActivationResponse>>(response.ResponseMessage);
                    telegenceResponse.TelegenceActivationResponse = telegenceActivationResponses;
                }
                catch (JsonException)
                {
                    // The success response not parsed properly
                    var parsedError = new TelegenceErrorMessage();
                    parsedError.responseMessage = response.ResponseMessage;
                    parsedError.errorDescription = LogCommonStrings.FAILED_TO_PARSE_RESPONSE;

                    telegenceResponse.IsSuccessful = false;
                    telegenceResponse.ErrorMessage = parsedError;
                }
            }
            else
            {
                // Parse the error message from response
                TelegenceErrorMessage parsedError;
                if (!string.IsNullOrWhiteSpace(response.ResponseMessage))
                {
                    try
                    {
                        parsedError = JsonConvert.DeserializeObject<TelegenceErrorMessage>(response.ResponseMessage);
                        // Handle case where the error message have a different format
                        if (string.IsNullOrWhiteSpace(parsedError.responseMessage) && string.IsNullOrWhiteSpace(parsedError.Description))
                        {
                            parsedError.responseMessage = response.ResponseMessage;
                        }
                    }
                    catch (JsonException)
                    {
                        // Have an error message, it is just not in JSON format
                        parsedError = new TelegenceErrorMessage();
                        parsedError.responseMessage = response.ResponseMessage;
                    }
                    telegenceResponse.ErrorMessage = parsedError;
                }
                else
                {
                    telegenceResponse.ErrorMessage = null;
                }
            }

            return telegenceResponse;
        }

        public async Task<DeviceChangeResult<TelegenceSubscriberUpdateRequest, TelegenceDeviceDetailsProxyResponse>> UpdateSubscriberAsync(TelegenceSubscriberUpdateRequest telegenceSubscriberUpdateRequest,
            string subscriberUpdateEndpoint)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;

            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<TelegenceSubscriberUpdateRequest, TelegenceDeviceDetailsProxyResponse>()
                {
                    ActionText = $"PATCH {baseUri}{subscriberUpdateEndpoint}",
                    RequestObject = telegenceSubscriberUpdateRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(telegenceSubscriberUpdateRequest);

            var payloadModel = CreatePayloadModel(jsonContent, baseUri, subscriberUpdateEndpoint);

            var response = await _httpClientFactory.GetClient().PatchWithProxyAsync(_proxyServer, payloadModel, _logger);

            bool isSuccessful = response.IsSuccessful;
            TelegenceDeviceDetailResponse detailResponse = null;
            try
            {
                detailResponse = JsonConvert.DeserializeObject<TelegenceDeviceDetailResponse>(response.ResponseMessage);
            }
            catch (Exception)
            {
                _logger.LogInfo("ERROR", $"Error casting response to TelegenceDeviceDetailResponse. Unexpected format: {response.ResponseMessage}");
                isSuccessful = false;
            }

            var telegenceResponse = new TelegenceDeviceDetailsProxyResponse
            {
                IsSuccessful = isSuccessful,
                RawResponse = response.ResponseMessage,
                TelegenceDeviceDetailResponse = detailResponse
            };

            return new DeviceChangeResult<TelegenceSubscriberUpdateRequest, TelegenceDeviceDetailsProxyResponse>()
            {
                ActionText = $"PATCH {baseUri}{subscriberUpdateEndpoint}",
                RequestObject = telegenceSubscriberUpdateRequest,
                HasErrors = !response.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        //update Telegence: apply / remove SOCCode
        public async Task<DeviceChangeResult<TelegenceConfigurationRequest, TelegenceDeviceDetailsProxyResponse>> UpdateTelegenceMobilityConfigurationAsync(TelegenceConfigurationRequest telegenceConfigurationUpdateRequest,
            string subscriberUpdateEndpoint)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;

            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<TelegenceConfigurationRequest, TelegenceDeviceDetailsProxyResponse>()
                {
                    ActionText = $"PATCH {baseUri}{subscriberUpdateEndpoint}",
                    RequestObject = telegenceConfigurationUpdateRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(telegenceConfigurationUpdateRequest);

            var payloadModel = CreatePayloadModel(jsonContent, baseUri, subscriberUpdateEndpoint);

            var response = await _httpClientFactory.GetClient().PatchWithProxyAsync(_proxyServer, payloadModel, _logger);

            bool isSuccessful = response.IsSuccessful;
            TelegenceDeviceDetailResponse detailResponse = null;
            try
            {
                detailResponse = JsonConvert.DeserializeObject<TelegenceDeviceDetailResponse>(response.ResponseMessage);
            }
            catch (Exception)
            {
                _logger.LogInfo("ERROR", $"Error casting response to TelegenceDeviceDetailResponse. Unexpected format: {response.ResponseMessage}");
                isSuccessful = false;
            }

            var telegenceResponse = new TelegenceDeviceDetailsProxyResponse
            {
                IsSuccessful = isSuccessful,
                RawResponse = response.ResponseMessage,
                TelegenceDeviceDetailResponse = detailResponse
            };

            return new DeviceChangeResult<TelegenceConfigurationRequest, TelegenceDeviceDetailsProxyResponse>()
            {
                ActionText = $"PATCH {baseUri}{subscriberUpdateEndpoint}",
                RequestObject = telegenceConfigurationUpdateRequest,
                HasErrors = !response.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<CarrierRatePlanUpdateRequest, TelegenceDeviceDetailsProxyResponse>> UpdateCarrierRatePlanAsync(CarrierRatePlanUpdateRequest carrierRatePlanUpdateRequest,
            string subscriberUpdateEndpoint)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<CarrierRatePlanUpdateRequest, TelegenceDeviceDetailsProxyResponse>()
                {
                    ActionText = $"PATCH {baseUri}{subscriberUpdateEndpoint}",
                    RequestObject = carrierRatePlanUpdateRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(carrierRatePlanUpdateRequest);

            var payloadModel = CreatePayloadModel(jsonContent, baseUri, subscriberUpdateEndpoint);

            var response = await _httpClientFactory.GetClient().PatchWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);

            bool isSuccessful = response.IsSuccessful;
            TelegenceDeviceDetailResponse detailResponse = null;
            try
            {
                detailResponse = JsonConvert.DeserializeObject<TelegenceDeviceDetailResponse>(response.ResponseMessage);
            }
            catch (Exception)
            {
                _logger.LogInfo("ERROR", $"Error casting response to TelegenceDeviceDetailResponse. Unexpected format: {response.ResponseMessage}");
                isSuccessful = false;
            }

            var telegenceResponse = new TelegenceDeviceDetailsProxyResponse
            {
                IsSuccessful = isSuccessful,
                RawResponse = response.ResponseMessage,
                TelegenceDeviceDetailResponse = detailResponse
            };

            return new DeviceChangeResult<CarrierRatePlanUpdateRequest, TelegenceDeviceDetailsProxyResponse>()
            {
                ActionText = $"PATCH {baseUri}{subscriberUpdateEndpoint}",
                RequestObject = carrierRatePlanUpdateRequest,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<string, TelegenceActivationProxyResponse>> CheckActivationStatus(string activationCheckEndpoint)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<string, TelegenceActivationProxyResponse>()
                {
                    ActionText = $"GET {baseUri}{activationCheckEndpoint}",
                    RequestObject = string.Empty,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var payloadModel = CreatePayloadModel(string.Empty, baseUri, activationCheckEndpoint);
            var response = await _httpClientFactory.GetClient().GetWithProxyAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = new TelegenceActivationProxyResponse
            {
                IsSuccessful = response.IsSuccessful,
                TelegenceActivationResponse = response.IsSuccessful ? JsonConvert.DeserializeObject<List<TelegenceActivationResponse>>(response.ResponseMessage) : null
            };

            return new DeviceChangeResult<string, TelegenceActivationProxyResponse>()
            {
                ActionText = $"GET {baseUri}{activationCheckEndpoint}",
                RequestObject = string.Empty,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<string, TelegenceActivationProxyResponse>> CheckActivationStatus(string activationCheckEndpoint, HttpClient httpClient)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<string, TelegenceActivationProxyResponse>()
                {
                    ActionText = $"GET {baseUri}{activationCheckEndpoint}",
                    RequestObject = string.Empty,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var payloadModel = CreatePayloadModel(string.Empty, baseUri, activationCheckEndpoint);
            var response = await httpClient.GetWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = ToTelegenceActivationProxyResponse(response);

            return new DeviceChangeResult<string, TelegenceActivationProxyResponse>()
            {
                ActionText = $"GET {baseUri}{activationCheckEndpoint}",
                RequestObject = string.Empty,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>> GetDeviceDetails(string deviceDetailsEndpoint, int retryNumber = CommonConstants.NUMBER_OF_TELEGENCE_RETRIES)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>()
                {
                    ActionText = $"GET {baseUri}{deviceDetailsEndpoint}",
                    RequestObject = string.Empty,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var requestUri = new Uri($"{baseUri}{deviceDetailsEndpoint}");
            _logger.LogInfo(CommonConstants.INFO, string.Format(LogCommonStrings.REQUEST_GET_DEVICE_DETAIL, requestUri.AbsoluteUri));
            var response = await RetryPolicyHelper.PollyRetryHttpRequestAsync(_logger, retryNumber).ExecuteAsync(async () =>
            {
                var request = _httpRequestFactory.BuildRequestMessage(_authentication, new HttpMethod(CommonConstants.METHOD_GET), requestUri,
                    new Dictionary<string, string> { { CommonConstants.ACCEPT, CommonConstants.APPLICATION_JSON }, { CommonConstants.APP_ID, _authentication.ClientId }, { CommonConstants.APP_SECRET, _authentication.ClientSecret } });
                return await _httpClientFactory.GetClient().SendAsync(request);
            });

            bool isSucessful = response.IsSuccessStatusCode;
            var responseBody = await response.Content.ReadAsStringAsync();
            TelegenceDeviceDetailResponse detailResponse = null;
            if (isSucessful)
            {
                _logger.LogInfo(CommonConstants.INFO, string.Format(LogCommonStrings.REQUEST_URL_SUCCESS, requestUri.AbsoluteUri));
                try
                {
                    detailResponse = JsonConvert.DeserializeObject<TelegenceDeviceDetailResponse>(responseBody);
                }
                catch (Exception)
                {
                    _logger.LogInfo(CommonConstants.ERROR, string.Format(LogCommonStrings.ERROR_CASTING_RESPONSE_TO_OBJECT, TELEGENCE_DEVICE_DETAIL_RESPONSE, responseBody));
                    isSucessful = false;
                }
            }
            else
            {
                _logger.LogInfo(CommonConstants.ERROR, string.Format(LogCommonStrings.REQUEST_FAILED_RESPONSE_AT_FINAL_RETRIES, requestUri.AbsoluteUri, responseBody));
            }
            var telegenceResponse = new TelegenceDeviceDetailsProxyResponse
            {
                IsSuccessful = isSucessful,
                RawResponse = responseBody,
                TelegenceDeviceDetailResponse = detailResponse
            };

            return new DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>()
            {
                ActionText = $"GET {baseUri}{deviceDetailsEndpoint}",
                RequestObject = string.Empty,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>> GetDeviceDetails(string deviceDetailsEndpoint, HttpClient httpClient)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>()
                {
                    ActionText = $"GET {baseUri}{deviceDetailsEndpoint}",
                    RequestObject = string.Empty,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var payloadModel = CreatePayloadModel(string.Empty, baseUri, deviceDetailsEndpoint);
            var response = await httpClient.GetWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);
            bool isSucessful = response.IsSuccessful;
            TelegenceDeviceDetailResponse detailResponse = null;
            try
            {
                detailResponse = JsonConvert.DeserializeObject<TelegenceDeviceDetailResponse>(response.ResponseMessage);
            }
            catch (Exception)
            {
                _logger.LogInfo("ERROR", $"Error casting response to TelegenceDeviceDetailResponse. Unexpected format: {response.ResponseMessage}");
                isSucessful = false;
            }

            var telegenceResponse = new TelegenceDeviceDetailsProxyResponse
            {
                IsSuccessful = isSucessful,
                RawResponse = response.ResponseMessage,
                TelegenceDeviceDetailResponse = detailResponse
            };

            return new DeviceChangeResult<string, TelegenceDeviceDetailsProxyResponse>()
            {
                ActionText = $"GET {baseUri}{deviceDetailsEndpoint}",
                RequestObject = string.Empty,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<TelegenceIPAdressesRequest, TelegenceIPAdressesProxyResponse>> RequestIPAddresses(string apiEndpoint, TelegenceIPAdressesRequest telegenceIPAdressesRequest, HttpClient httpClient)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<TelegenceIPAdressesRequest, TelegenceIPAdressesProxyResponse>()
                {
                    ActionText = $"POST {baseUri}{apiEndpoint}",
                    RequestObject = telegenceIPAdressesRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(telegenceIPAdressesRequest);

            var payloadModel = CreatePayloadModel(jsonContent, baseUri, apiEndpoint);

            var response = await httpClient.PostWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = new TelegenceIPAdressesProxyResponse
            {
                IsSuccessful = response.IsSuccessful,
                TelegenceIPAdressesResponse = (response.IsSuccessful || response.ResponseMessage != null) ? JsonConvert.DeserializeObject<TelegenceIPAdressesResponse>(response.ResponseMessage) : null
            };

            return new DeviceChangeResult<TelegenceIPAdressesRequest, TelegenceIPAdressesProxyResponse>()
            {
                ActionText = $"POST {baseUri}{apiEndpoint}",
                RequestObject = telegenceIPAdressesRequest,
                HasErrors = !response.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<TelegenceIPProvisionRequest, TelegenceIPProvisionProxyResponse>> SubmitIPProvisioningRequest(string apiEndpoint, TelegenceIPProvisionRequest telegenceIPProvisionRequest, HttpClient httpClient)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<TelegenceIPProvisionRequest, TelegenceIPProvisionProxyResponse>()
                {
                    ActionText = $"POST {baseUri}{apiEndpoint}",
                    RequestObject = telegenceIPProvisionRequest,
                    HasErrors = true,
                    ResponseObject = null
                };
            }

            var jsonContent = JsonConvert.SerializeObject(telegenceIPProvisionRequest);
            var payloadModel = CreatePayloadModel(jsonContent, baseUri, apiEndpoint);
            var response = await httpClient.PostWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = new TelegenceIPProvisionProxyResponse
            {
                IsSuccessful = response.IsSuccessful,
                TelegenceIPProvisionResponse = (response.IsSuccessful || response.ResponseMessage != null) ?
                                                    JsonConvert.DeserializeObject<TelegenceIPProvisionResponse>(response.ResponseMessage) : null
            };

            return new DeviceChangeResult<TelegenceIPProvisionRequest, TelegenceIPProvisionProxyResponse>()
            {
                ActionText = $"POST {baseUri}{apiEndpoint}",
                RequestObject = telegenceIPProvisionRequest,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<DeviceChangeResult<string, TelegenceIPProvisionStatusProxyResponse>> CheckIPProvisioningStatus(string apiEndpoint, string batchId, string subscriberNumber, HttpClient httpClient)
        {
            var baseUri = _isProduction ? _authentication.ProductionURL : _authentication.SandboxURL;
            if (!_authentication.WriteIsEnabled)
            {
                return new DeviceChangeResult<string, TelegenceIPProvisionStatusProxyResponse>()
                {
                    ActionText = $"GET {baseUri}{apiEndpoint}",
                    RequestObject = batchId,
                    HasErrors = true,
                    ResponseObject = null
                };
            }
            var headerParams = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(batchId))
            {
                headerParams.Add("batch-id", batchId);
            }
            else if (!string.IsNullOrEmpty(subscriberNumber))
            {
                headerParams.Add("subscriber-number", subscriberNumber);
            }
            var payloadModel = CreateHeaderOnlyPayloadModel(baseUri, apiEndpoint, headerParams);
            var response = await httpClient.GetWithProxyUsingMessageAsync(_proxyServer, payloadModel, _logger);

            var telegenceResponse = new TelegenceIPProvisionStatusProxyResponse
            {
                IsSuccessful = response.IsSuccessful,
                TelegenceIPProvisionStatusResponses = (response.IsSuccessful && response.ResponseMessage != null) ?
                                                        JsonConvert.DeserializeObject<List<TelegenceIPProvisionStatusResponse>>(response.ResponseMessage) : null,
                Message = (!response.IsSuccessful && response.ResponseMessage != null) ?
                                                        JsonConvert.DeserializeObject<TelegenceIPProvisionBaseResponse>(response.ResponseMessage).Message : null
            };

            return new DeviceChangeResult<string, TelegenceIPProvisionStatusProxyResponse>()
            {
                ActionText = $"GET {baseUri}{apiEndpoint}",
                RequestObject = batchId,
                HasErrors = !telegenceResponse.IsSuccessful,
                ResponseObject = telegenceResponse
            };
        }

        public async Task<TelegenceBillingAccountDetailResponse> GetBillingAccountNumber(string ban, string endpoindUrl)
        {
            string banDetailUrl = endpoindUrl.Replace("{ban}", ban);
            Uri baseUrl = new Uri(_authentication.SandboxURL);
            if (_isProduction)
            {
                baseUrl = new Uri(_authentication.ProductionURL);
            }
            var detailRequestUrl = new Uri($"{baseUrl.AbsoluteUri.TrimEnd('/')}{banDetailUrl}");
            _logger?.LogInfo(CommonConstants.INFO, string.Format(LogCommonStrings.REQUEST_GET_DEVICE_DETAIL, detailRequestUrl.AbsoluteUri));
            try
            {
                var response = await RetryPolicyHelper.PollyRetryHttpRequestResponseBaseAsync(_logger, CommonConstants.NUMBER_OF_TELEGENCE_RETRIES).ExecuteAsync(async () =>
                {
                    var request = _httpRequestFactory.BuildRequestMessage(_authentication, new HttpMethod(CommonConstants.METHOD_GET), detailRequestUrl, BuildHeaderContent(_authentication));
                    return MappingHttpResponseContent(await _httpClientFactory.GetClient().SendAsync(request));
                });
                bool isSucessful = response.IsSuccessful;
                var responseBody = response.ResponseMessage;
                if (isSucessful)
                {
                    _logger?.LogInfo(CommonConstants.INFO, string.Format(LogCommonStrings.REQUEST_URL_SUCCESS, detailRequestUrl.AbsoluteUri));
                    return JsonConvert.DeserializeObject<TelegenceBillingAccountDetailResponse>(responseBody);
                }
                _logger?.LogInfo(CommonConstants.ERROR, string.Format(LogCommonStrings.REQUEST_FAILED_RESPONSE_AT_FINAL_RETRIES, detailRequestUrl.AbsoluteUri, responseBody));
                return new TelegenceBillingAccountDetailResponse();
            }
            catch (Exception e)
            {
                _logger?.LogInfo(CommonConstants.INFO, string.Format(LogCommonStrings.CALLING_API_FAILED, detailRequestUrl.AbsoluteUri));
                _logger?.LogInfo(CommonConstants.EXCEPTION, e.Message);
                return new TelegenceBillingAccountDetailResponse();
            }

        }

        private static Dictionary<string, string> BuildHeaderContent(TelegenceAPIAuthentication telegenceAuth)
        {
            var headerContent = new Dictionary<string, string>();
            headerContent.Add(CommonConstants.ACCEPT, CommonConstants.APPLICATION_JSON);
            headerContent.Add(CommonConstants.APP_ID, telegenceAuth.ClientId);
            headerContent.Add(CommonConstants.APP_SECRET, telegenceAuth.ClientSecret);
            return headerContent;
        }

        private PayloadModel CreatePayloadModel(string json, string baseUri, string endpoint)
        {
            var headerContent = new ExpandoObject() as IDictionary<string, object>;
            headerContent.Add("app-id", _authentication.ClientId);
            headerContent.Add("app-secret", _authentication.ClientSecret);
            var payloadModel = new PayloadModel
            {
                AuthenticationType = AuthenticationType.TELEGENCEAUTH,
                Endpoint = $"/{endpoint.TrimStart('/')}",
                HeaderContent = JsonConvert.SerializeObject(headerContent),
                JsonContent = json,
                Url = baseUri
            };
            return payloadModel;
        }


        private PayloadModel CreateHeaderOnlyPayloadModel(string baseUri, string endpoint, Dictionary<string, string> headerParams)
        {
            var headerContent = new ExpandoObject() as IDictionary<string, object>;
            headerContent.Add("app-id", _authentication.ClientId);
            headerContent.Add("app-secret", _authentication.ClientSecret);
            foreach (var header in headerParams)
            {
                headerContent.Add(header.Key, header.Value);
            }
            var payloadModel = new PayloadModel
            {
                AuthenticationType = AuthenticationType.TELEGENCEAUTH,
                Endpoint = $"/{endpoint.TrimStart('/')}",
                HeaderContent = JsonConvert.SerializeObject(headerContent),
                JsonContent = string.Empty,
                Url = baseUri
            };
            return payloadModel;
        }

        private static Amop.Core.Models.HttpResponseBase MappingHttpResponseContent(HttpResponseMessage httpResponseMessage)
        {
            return new Amop.Core.Models.HttpResponseBase
            {
                HeaderContent = httpResponseMessage.Headers.ToString(),
                IsSuccessful = httpResponseMessage.IsSuccessStatusCode,
                ResponseMessage = httpResponseMessage.Content.ReadAsStringAsync().Result,
                StatusCode = httpResponseMessage.StatusCode.ToString()
            };
        }

        //TODO: Telegence get IP API call

        //TODO: Telegence submit API call

        //TODO: Telegence status API call
    }
}
