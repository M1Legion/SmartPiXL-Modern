<%

set oConn = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
oConn.Open connStr

set rs=Server.CreateObject("ADODB.recordset")
sql=" exec M1SP_RequestToDeleteFullfilled "
rs.Open sql, oConn










%>