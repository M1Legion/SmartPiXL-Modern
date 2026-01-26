<%

set oConn = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
oConn.Open connStr


Response.Write ("Hello")

'set rsGetHireID = oConn.Execute("Exec M1SP_RequestToKnowCount")

'Set Conn = Server.CreateObject("ADODB.Connection")
'Conn.Open connStr
'Conn.Execute "Exec M1SP_RequestToKnowCount"


Set rsGetHireID = Server.CreateObject("ADODB.RecordSet")
rsGetHireID.Open connStr
'rsGetHireID.CursorLocation = 3 'adUseClient
'rsGetHireID.Open "Exec sp_selectNewHireSQL", Conn
'NumOfHireID = rsGetHireID.RecordCount

Response.Write ("World")
'Response.Write (rsGetHireID)











%>