<%

set oConn = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
oConn.Open connStr

Set objCommandSec = CreateObject("ADODB.Command")
With objCommandSec
    Set .ActiveConnection = Conn
    .CommandType = 4
    .CommandText = " EXEC M1SP_RequestToDeleteFullfilled "
End With

'set rs = Server.CreateObject("ADODB.RecordSet")
'rs.open objCommandSec
'rs = objCommandSec.Execute

'while not rs.eof
 ''   response.write (1)
  ''  response.write (rs("1_Q1"))
   '' rs.MoveNext
'wend
'response.write (2)





%>