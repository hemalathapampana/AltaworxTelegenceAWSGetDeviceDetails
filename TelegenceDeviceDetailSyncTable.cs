using System;
using System.Data;
using System.Linq;
using Amop.Core.Models.Telegence.Api;

namespace AltaworxTelegenceAWSGetDeviceDetails
{
    public class TelegenceDeviceDetailSyncTable
    {
        public DataTable DataTable { get; }
        private bool _hasColumns;

        public TelegenceDeviceDetailSyncTable()
        {
            DataTable = new DataTable("TelegenceDeviceDetailStaging");
        }

        public bool HasRows()
        {
            return DataTable.Rows.Count > 0;
        }

        public void AddRow(TelegenceDeviceDetailResponse deviceDetail, int serviceProviderId)
        {
            if (!_hasColumns)
            {
                AddColumns();
                _hasColumns = true;
            }
            var telegenceCharactericticHelper = new TelegenceServicegGetCharacteristicHelper(deviceDetail);

            var dr = DataTable.NewRow();
            dr[1] = serviceProviderId;
            dr[2] = deviceDetail.SubscriberNumber;

            if (telegenceCharactericticHelper.activatedDate != null)
            {
                dr[3] = telegenceCharactericticHelper.activatedDate.Value;
            }
            else
            {
                dr[3] = DBNull.Value;
            }

            dr[4] = telegenceCharactericticHelper.singleUserCodeCharacteristic?.Value;
            dr[5] = telegenceCharactericticHelper.singleUserDescCharacteristic?.Value;
            dr[6] = telegenceCharactericticHelper.serviceZipCodeCharacteristic?.Value;

            if (telegenceCharactericticHelper.nextBillCycleDate != null)
            {
                dr[7] = telegenceCharactericticHelper.nextBillCycleDate.Value;
            }
            else
            {
                dr[7] = DBNull.Value;
            }

            dr[8] = telegenceCharactericticHelper.iccidCharacteristic?.Value;
            dr[9] = telegenceCharactericticHelper.imeiCharacteristic?.Value;
            dr[10] = DateTime.UtcNow;
            dr[11] = telegenceCharactericticHelper.deviceMakeCharacteristic?.Value;
            dr[12] = telegenceCharactericticHelper.deviceModelCharacteristic?.Value;
            dr[13] = telegenceCharactericticHelper.imeiTypeCharacteristic?.Value;
            dr[14] = telegenceCharactericticHelper.dataGroupIdCharacteristic?.Value;
            dr[15] = telegenceCharactericticHelper.contactNameCharacteristic?.Value;
            dr[16] = telegenceCharactericticHelper.techTypeNameCharacteristic?.Value;
            dr[17] = telegenceCharactericticHelper.ipAddressCharacteristic?.Value;
            dr[18] = telegenceCharactericticHelper.statusEffectiveDate?.Value;
            DataTable.Rows.Add(dr);
        }


        private void AddColumns()
        {
            DataTable.Columns.Add("Id", typeof(int));
            DataTable.Columns.Add("ServiceProviderId");
            DataTable.Columns.Add("SubscriberNumber");
            DataTable.Columns.Add("SubscriberActivatedDate");
            DataTable.Columns.Add("SingleUserCode");
            DataTable.Columns.Add("SingleUserCodeDescription");
            DataTable.Columns.Add("ServiceZipCode");
            DataTable.Columns.Add("NextBillCycleDate");
            DataTable.Columns.Add("ICCID");
            DataTable.Columns.Add("IMEI");
            DataTable.Columns.Add("CreatedDate");
            DataTable.Columns.Add("DeviceMake");
            DataTable.Columns.Add("DeviceModel");
            DataTable.Columns.Add("IMEIType");
            DataTable.Columns.Add("DataGroupId");
            DataTable.Columns.Add("ContactName");
            DataTable.Columns.Add("DeviceTechnologyType");
            DataTable.Columns.Add("IPAddress");
            DataTable.Columns.Add("statusEffectiveDate");
        }
    }
}
