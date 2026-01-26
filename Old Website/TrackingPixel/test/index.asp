<%

' set oConn = Server.CreateObject("ADODB.connection")
' connStr = "Driver={ODBC Driver 13 for SQL Server};" & _
'            "Server=127.0.0.1;" & _
'            "Database=SmartPiXL;" & _
'            "Uid=PiXL;" & _
'            "Pwd=9f2A$_!;"
' oConn.Open connStr

' set rs=Server.CreateObject("ADODB.recordset")
' sql=" exec M1SP_RequestToDeleteFullfilled "
' rs.Open sql, oConn


SET Conn1 = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _
            "Server=127.0.0.1;" & _
            "Database=SmartPiXL;" & _
            "Uid=PiXL;" & _
            "Pwd=9f2A$_!;"

Conn1.Open connStr

' Set objCommandSec = CreateObject("ADODB.Command")
'  With objCommandSec
'      Set .ActiveConnection = Conn1
'      .CommandType = 4
'      .CommandText = "M1SP_RequestToKnowFullfilled"
'  End With

' Set cmd = CreateObject("ADODB.Command")
' cmd.ActiveConnection = Conn1
' cmd.CommandText = "M1SP_RequestToKnowFullfilled"
' cmd.CommandType = adCmdStoredProc
' cmd.Parameters.Refresh
' cmd.Execute

' Dim count
' count = cmd.Parameters(0)
' Response.Write cmd.Parameters(0)

Set objCommandSec = CreateObject("ADODB.Command")
With objCommandSec
 Set .ActiveConnection = Conn1
 .CommandType = 4
 .CommandText = "M1SP_RequestToKnowFullfilled"
 .Parameters.Append .CreateParameter("@outVar1", 200, 2, 255)
 .Execute
 Response.Write .Parameters(0).Value
End With









' objCommandSec.Parameters.Refresh
'' objCommandSec.Execute
' Dim count
' count = objCommandSec.Parameters(1)

' Set rsGetHireID = objCommandSec.Execute()
' NumOfHireID = rsGetHireID.RecordCount
' Response.Write (NumOfHireID)

' set rs = Server.CreateObject("ADODB.RecordSet")
' rs.Open objCommandSec
















'rs = objCommandSec.Execute
'objCommandSec.Execute

' 'while not rs.eof
'  ''   response.write (1)
'   ''  response.write (rs("1_Q1"))
'    '' rs.MoveNext
' 'wend
' 'response.write (2)






%>