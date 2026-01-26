<%

set oConn = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
oConn.Open connStr

SQL = "exec M1SP_RequestToKnowCount "
Set rst = oConn.execute(SQL)

Dim strError

'If rst.BOF And rst.EOF Then
    ' No data
'Else
 ''   Do While (Not rst.EOF)
  ''  	Response.Write rst(0)
   '' 	rst.MoveNext
    '' Loop
'End If

'rst.Close





'Set cmd = Server.CreateObject("ADODB.Command")

'With cmd
'' .ActiveConnection = connStr
'' .Commandtext = "M1SP_RequestToKnowCount"
'' .CommandType = adCmdStoredProc
'' End with

'Response.Write retcount
'Set cmd = Nothing



'set Cmd = Server.CreateObject("ADODB.Command")
'sSQL = "EXEC M1SP_RequestToKnowCount "
'set rs = oConn.execute sSQL


Response.Write ("Hello")

'set rsGetHireID = oConn.Execute("Exec M1SP_RequestToKnowCount")

'Set Conn = Server.CreateObject("ADODB.Connection")
'Conn.Open connStr
'Conn.Execute "Exec M1SP_RequestToKnowCount"


'Set rsGetHireID = Server.CreateObject("ADODB.RecordSet")
'oConn.Open connStr
'rsGetHireID.CursorLocation = 3 'adUseClient
'rsGetHireID.Open "Exec M1SP_RequestToKnowCount", oConn
'NumOfHireID = rsGetHireID.RecordCount


'Set objCommand = Server.CreateObject("ADODB.Command")
'objCommand.ActiveConnection = ConnectionString
'objCommand.CommandText = "dbo.sp_selectNewHireSQL"
'objCommand.CommandType = adCmdStoredProc ' you have to include adovbs.inc file or you can use 4

'Set rsGetHireID = objCommand.Execute()
'NumOfHireID = rsGetHireID.RecordCount
'Response.Write (NumOfHireID)




Response.Write ("World")
'Response.Write (rsGetHireID)











%>