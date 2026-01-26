using System;
using System.Web.UI;
using System.Data;
using System.Data.SqlClient;
using System.IO;

public partial class _Default : Page
{
    protected void Page_Load(object sender, EventArgs e)
    {
        System.Collections.Specialized.NameValueCollection vars = new System.Collections.Specialized.NameValueCollection();

        try
        {
            string SqlConnectionString = ";user id=PiXL;Server=127.0.0.1;Database=SmartPiXL;Pwd=9f2A$_!";

            string Referer = string.Empty;
            string[] RefererQuery;
            //string StagingTableName = "ASP_PiXL_Staging" + (DateTime.Now.Minute / 15 + 1).ToString();
			string StagingTableName = "ASP_PiXL_Staging" + (DateTime.Now.Minute / 5 % 4 + 1).ToString();			

            using (DataTable InsertTable = new DataTable())
            {
                InsertTable.CaseSensitive = false;

                //InsertTable.Columns.Add("RecordID"); //This is computed by SQL
                InsertTable.Columns.Add("timestamp");
                //InsertTable.Columns.Add("timestamp2"); //This is computed by SQL
                InsertTable.Columns.Add("REMOTE_ADDR");
                InsertTable.Columns.Add("HTTP_USER_AGENT");
                InsertTable.Columns.Add("HTTP_REFERER");
                InsertTable.Columns.Add("HTTP_REFERER_ROOT");
                InsertTable.Columns.Add("HTTP_REFERER_QUERY");
                InsertTable.Columns.Add("HTTP_X_ORIGINAL_URL");
                //InsertTable.Columns.Add("HTTP_X_ORIGINAL_URL_ROOT"); //This is computed by SQL
                InsertTable.Columns.Add("HTTP_DNT");
                InsertTable.Columns.Add("HTTP_COOKIE");
                InsertTable.Columns.Add("HTTP_CLIENT_IP");
                InsertTable.Columns.Add("HTTP_FORWARDED");
                InsertTable.Columns.Add("HTTP_FROM");
                InsertTable.Columns.Add("HTTP_PROXY_CONNECTION");
                InsertTable.Columns.Add("HTTP_VIA");
                InsertTable.Columns.Add("HTTP_X_MCPROXYFILTER");
                InsertTable.Columns.Add("HTTP_X_TARGET_PROXY");
                InsertTable.Columns.Add("HTTP_X_REQUESTED_WITH");
                InsertTable.Columns.Add("BROWSER_Browser");
                InsertTable.Columns.Add("BROWSER_MobileDeviceModel");
                InsertTable.Columns.Add("BROWSER_Platform");
                InsertTable.Columns.Add("HTTP_ACCEPT_LANGUAGE");

                DataRow row = InsertTable.NewRow();

                row[0] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                vars = Request.ServerVariables;

                row[1] = vars["REMOTE_ADDR"];
                row[2] = vars["HTTP_USER_AGENT"];

                Referer = Request["HTTP_REFERER"];
                row[3] = Referer;

                if (Referer != null)
                {
                    if (Referer.Contains("%"))
                    {
                        Referer = Referer.Replace("%26amp", string.Empty);
                        Referer = Referer.Replace("%3Bamp", string.Empty);
                        Referer = Referer.Replace("%23x3b", string.Empty);
                        Referer = Referer.Replace("%3Bx3b", string.Empty);
                        Referer = Referer.Replace("%23x23", string.Empty);
                        Referer = Referer.Replace("%3Bx23", string.Empty);
                        Referer = Referer.Replace("%3Bx2f", string.Empty);
                        Referer = Referer.Replace("%3Bx3a", string.Empty);
                        Referer = Referer.Replace("%26", string.Empty);
                        Referer = Referer.Replace("%3B", string.Empty);
                    }

                    RefererQuery = Referer.Split('?');

                    if (RefererQuery.Length == 1)
                    {
                        row[4] = RefererQuery[0];

                    }
                    else
                    {
                        row[4] = RefererQuery[0];
                        row[5] = RefererQuery[1];
                    }
                }

                row[6] = vars["HTTP_X_ORIGINAL_URL"];

                row[7] = vars["HTTP_DNT"];

                row[8] = vars["HTTP_COOKIE"];

                row[9] = vars["HTTP_CLIENT_IP"];
                row[10] = vars["HTTP_FORWARDED"];
                row[11] = vars["HTTP_FROM"];
                row[12] = vars["HTTP_PROXY_CONNECTION"];
                row[13] = vars["HTTP_VIA"];
                row[14] = vars["HTTP_X_MCPROXYFILTER"];
                row[15] = vars["HTTP_X_TARGET_PROXY"];
                row[16] = vars["HTTP_X_REQUESTED_WITH"];


                if (Request.Browser != null)
                {
                    row[17] = Request.Browser.Browser;
                    row[18] = Request.Browser.MobileDeviceModel;
                    row[19] = Request.Browser.Platform;
                }

                row[20] = vars["HTTP_ACCEPT_LANGUAGE"];

                InsertTable.Rows.Add(row);

                using (var sbc = new SqlBulkCopy(SqlConnectionString))
                {
                    sbc.DestinationTableName = "dbo." + StagingTableName;
                    sbc.BatchSize = 1;
                    sbc.BulkCopyTimeout = 6000;

                    //RecordID
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(0, 1));     //timestamp
                                                                                    //timestamp2
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(1, 3));     //REMOTE_ADDR
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(2, 4));     //HTTP_USER_AGENT
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(3, 5));     //HTTP_REFERER
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(4, 6));     //HTTP_REFERER_ROOT
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(5, 7));     //HTTP_REFERER_QUERY
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(6, 8));     //HTTP_X_ORIGINAL_URL
                                                                                    //HTTP_X_ORIGINAL_URL_ROOT
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(7, 10));    //HTTP_DNT
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(8, 11));    //HTTP_COOKIE
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(9, 12));    //HTTP_CLIENT_IP
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(10, 13));   //HTTP_FORWARDED
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(11, 14));   //HTTP_FROM
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(12, 15));   //HTTP_PROXY_CONNECTION
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(13, 16));   //HTTP_VIA
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(14, 17));   //HTTP_X_MCPROXYFILTER
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(15, 18));   //HTTP_X_TARGET_PROXY
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(16, 19));   //HTTP_X_REQUESTED_WITH
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(17, 20));   //BROWSER_Browser
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(18, 21));   //BROWSER_MobileDeviceModel
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(19, 22));   //BROWSER_Platform
                    sbc.ColumnMappings.Add(new SqlBulkCopyColumnMapping(20, 23));   //HTTP_ACCEPT_LANGUAGE

                    sbc.WriteToServer(InsertTable);
                }
            }
        }
        catch (Exception exc)
        {
            string Logdirectory = AppContext.BaseDirectory + @"/Log/";

            if (!Directory.Exists(Logdirectory))
                Directory.CreateDirectory(Logdirectory);

            using (FileStream file = new FileStream(Logdirectory + DateTime.Now.ToShortDateString().Replace('/', '_') + ".log", FileMode.Append, FileAccess.Write, FileShare.Write))
            using (StreamWriter SW = new StreamWriter(file))
            {
                SW.WriteLine(exc.ToString());
                SW.WriteLine("REMOTE_ADDR: " + vars["REMOTE_ADDR"]);
                SW.WriteLine("HTTP_USER_AGENT: " + vars["HTTP_USER_AGENT"]);
                SW.WriteLine("HTTP_REFERER: " + vars["HTTP_REFERER"]);
                SW.WriteLine("HTTP_REFERER_ROOT: " + vars["HTTP_REFERER_ROOT"]);
                SW.WriteLine("HTTP_REFERER_QUERY: " + vars["HTTP_REFERER_QUERY"]);
                SW.WriteLine("HTTP_X_ORIGINAL_URL: " + vars["HTTP_X_ORIGINAL_URL"]);
                SW.WriteLine("HTTP_DNT: " + vars["HTTP_DNT"]);
                SW.WriteLine("HTTP_COOKIE: " + vars["HTTP_COOKIE"]);
                SW.WriteLine("HTTP_CLIENT_IP: " + vars["HTTP_CLIENT_IP"]);
                SW.WriteLine("HTTP_FORWARDED: " + vars["HTTP_FORWARDED"]);
                SW.WriteLine("HTTP_FROM: " + vars["HTTP_FROM"]);
                SW.WriteLine("HTTP_PROXY_CONNECTION: " + vars["HTTP_PROXY_CONNECTION"]);
                SW.WriteLine("HTTP_VIA: " + vars["HTTP_VIA"]);
                SW.WriteLine("HTTP_X_MCPROXYFILTER: " + vars["HTTP_X_MCPROXYFILTER"]);
                SW.WriteLine("HTTP_X_TARGET_PROXY: " + vars["HTTP_X_TARGET_PROXY"]);
                SW.WriteLine("HTTP_X_REQUESTED_WITH: " + vars["HTTP_X_REQUESTED_WITH"]);
                SW.WriteLine("BROWSER_Browser: " + vars["BROWSER_Browser"]);
                SW.WriteLine("BROWSER_MobileDeviceModel: " + vars["BROWSER_MobileDeviceModel"]);
                SW.WriteLine("BROWSER_Platform: " + vars["BROWSER_Platform"]);
                SW.WriteLine("HTTP_ACCEPT_LANGUAGE: " + vars["HTTP_ACCEPT_LANGUAGE"]);
            }
        }
    }
}