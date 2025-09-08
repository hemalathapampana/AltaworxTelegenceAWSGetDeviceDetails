using System.Data;

namespace AltaworxTelegenceAWSGetDeviceDetails
{
    public class TelegenceDeviceFeatureSyncTable
    {
        public DataTable DataTable { get; }
        private bool _hasColumns;

        public TelegenceDeviceFeatureSyncTable()
        {
            DataTable = new DataTable("TelegenceDeviceMobilityFeature_Staging");
        }

        public bool HasRows()
        {
            return DataTable.Rows.Count > 0;
        }

        public void AddRow(string subscriberNumber, string offeringCode)
        {
            if (!_hasColumns)
            {
                AddColumns();
                _hasColumns = true;
            }

            var dr = DataTable.NewRow();
            dr[0] = subscriberNumber;
            dr[1] = !string.IsNullOrEmpty(offeringCode) && offeringCode.Length > 50 ? offeringCode.Substring(0, 50) : offeringCode;

            DataTable.Rows.Add(dr);
        }

        private void AddColumns()
        {
            DataTable.Columns.Add("SubscriberNumber");
            DataTable.Columns.Add("OfferingCode");
        }
    }
}
