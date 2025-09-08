using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Amop.Core.Models.Telegence.Api;

namespace AltaworxTelegenceAWSGetDeviceDetails
{
    public class TelegenceServicegGetCharacteristicHelper
    {
        private const string SUBSCRIBER_ACTIVATION_DATE = "subscriberActivationDate";
        private const string SINGLE_USER_CODE = "singleUserCode";
        private const string SINGLE_USER_CODE_DESCRIPTION = "singleUserCodeDescription";
        private const string SERVICE_ZIP_CODE = "serviceZipCode";
        private const string NEXT_BILLCYCLE_DATE = "nextBillCycleDate";
        private const string SIM = "sim";
        private const string BL_IMEI = "BLIMEI";
        private const string BL_DEVICE_BRAND = "BLDeviceBrand";
        private const string BL_DEVICE_MODEL = "BLDeviceModel";
        private const string BL_IMEI_TYPE = "BLIMEIType";
        private const string DATA_GROUP_ID_CODE = "dataGroupIDCode1";
        private const string CONTACT_NAME = "contactName";
        private const string BL_DEVICE_TECHNOLOGY_TYPE = "BLDeviceTechnologyType";
        private const string IPADDRESS = "ipAddress";
        private const string STATUSEFFECTIVEDATE = "statusEffectiveDate";

        public DateTime? activatedDate = null;
        public TelegenceServiceCharacteristic singleUserCodeCharacteristic = null;
        public TelegenceServiceCharacteristic singleUserDescCharacteristic = null;
        public TelegenceServiceCharacteristic serviceZipCodeCharacteristic = null;
        public DateTime? nextBillCycleDate = null;
        public TelegenceServiceCharacteristic iccidCharacteristic = null;
        public TelegenceServiceCharacteristic imeiCharacteristic = null;
        public TelegenceServiceCharacteristic deviceMakeCharacteristic = null;
        public TelegenceServiceCharacteristic deviceModelCharacteristic = null;
        public TelegenceServiceCharacteristic imeiTypeCharacteristic = null;
        public TelegenceServiceCharacteristic dataGroupIdCharacteristic = null;
        public TelegenceServiceCharacteristic contactNameCharacteristic = null;
        public TelegenceServiceCharacteristic techTypeNameCharacteristic = null;
        public TelegenceServiceCharacteristic ipAddressCharacteristic = null;
        public TelegenceServiceCharacteristic statusEffectiveDate = null;

        public TelegenceServicegGetCharacteristicHelper(TelegenceDeviceDetailResponse deviceDetail)
        {
            if (deviceDetail.ServiceCharacteristic != null && deviceDetail.ServiceCharacteristic.Count > 0)
            {
                var activatedDateCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == SUBSCRIBER_ACTIVATION_DATE);
                if (activatedDateCharacteristic != null && !string.IsNullOrWhiteSpace(activatedDateCharacteristic.Value))
                {
                    var activatedDateString = activatedDateCharacteristic.Value.Trim('Z');
                    if (DateTime.TryParse(activatedDateString, out var localActivatedDate))
                    {
                        activatedDate = localActivatedDate;
                    }
                }

                singleUserCodeCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == SINGLE_USER_CODE);
                singleUserDescCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == SINGLE_USER_CODE_DESCRIPTION);
                serviceZipCodeCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == SERVICE_ZIP_CODE);
                var nextBillCycleDateCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == NEXT_BILLCYCLE_DATE);
                if (nextBillCycleDateCharacteristic != null && !string.IsNullOrWhiteSpace(nextBillCycleDateCharacteristic.Value))
                {
                    var nextBillCycleDateString = nextBillCycleDateCharacteristic.Value.Trim('Z');
                    if (DateTime.TryParse(nextBillCycleDateString, out var localNextBillCycleDate))
                    {
                        nextBillCycleDate = localNextBillCycleDate;
                    }
                }
                iccidCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == SIM);
                imeiCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == BL_IMEI);
                deviceMakeCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == BL_DEVICE_BRAND);
                deviceModelCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == BL_DEVICE_MODEL);
                imeiTypeCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == BL_IMEI_TYPE);
                dataGroupIdCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == DATA_GROUP_ID_CODE);
                contactNameCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == CONTACT_NAME);
                techTypeNameCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == BL_DEVICE_TECHNOLOGY_TYPE);
                ipAddressCharacteristic = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == IPADDRESS);
                statusEffectiveDate = deviceDetail.ServiceCharacteristic.FirstOrDefault(x => x.Name == STATUSEFFECTIVEDATE);
            }
        }
    }
}
