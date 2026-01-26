<html>
<body>
<%
SET Conn1 = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _ 
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
Conn1.Open connStr

'Conn1.CommandTimeout = 600
'Server.ScriptTimeout = 600

DIM RS1, RS2, RS3, RS4, RS5, RS6, RS7, RS8, RS9

SET RS1 = Server.CreateObject("ADODB.recordset")
RS1.ActiveConnection = Conn1
SQL1 = "SELECT COUNT(*) AS KnowCount "
SQL1 = SQL1 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL1 = SQL1 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL1 = SQL1 & "AND RequestType = '1'"

'response.write SQL1

SET RS2 = Server.CreateObject("ADODB.recordset")
RS2.ActiveConnection = Conn1
SQL2 = "SELECT COUNT(*) AS KnowFullfilled "
SQL2 = SQL2 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL2 = SQL2 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL2 = SQL2 & "AND RequestType = '1' "
SQL2 = SQL2 & "AND Fullfilled = '1'"

'response.write SQL2

SET RS3 = Server.CreateObject("ADODB.recordset")
RS3.ActiveConnection = Conn1
SQL3 = "SELECT COUNT(*) AS KnowDenied "
SQL3 = SQL3 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL3 = SQL3 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL3 = SQL3 & "AND RequestType = '1' "
SQL3 = SQL3 & "AND Fullfilled = '0'"

'response.write SQL3

SET RS4 = Server.CreateObject("ADODB.recordset")
RS4.ActiveConnection = Conn1
SQL4 = "SELECT COUNT(*) AS DeleteCount "
SQL4 = SQL4 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL4 = SQL4 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL4 = SQL4 & "AND RequestType = '2' "

'response.write SQL4

SET RS5 = Server.CreateObject("ADODB.recordset")
RS5.ActiveConnection = Conn1
SQL5 = "SELECT COUNT(*) AS DeleteFullfilled "
SQL5 = SQL5 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL5 = SQL5 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL5 = SQL5 & "AND RequestType = '2' "
SQL5 = SQL5 & "AND Fullfilled = '1'"

'response.write SQL5



SET RS6 = Server.CreateObject("ADODB.recordset")
RS6.ActiveConnection = Conn1
SQL6 = "SELECT COUNT(*) AS DeleteDenied "
SQL6 = SQL6 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL6 = SQL6 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL6 = SQL6 & "AND RequestType = '2' "
SQL6 = SQL6 & "AND Fullfilled = '0'"

'response.write SQL6


SET RS7 = Server.CreateObject("ADODB.recordset")
RS7.ActiveConnection = Conn1
SQL7 = "SELECT COUNT(*) AS SuppressCount "
SQL7 = SQL7 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL7 = SQL7 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL7 = SQL7 & "AND RequestType = '3'"

'response.write SQL7


SET RS8 = Server.CreateObject("ADODB.recordset")
RS8.ActiveConnection = Conn1
SQL8 = "SELECT COUNT(*) AS SuppressFullfilled "
SQL8 = SQL8 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL8 = SQL8 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL8 = SQL8 & "AND RequestType = '3' "
SQL8 = SQL8 & "AND Fullfilled = '1'"

'response.write SQL8


SET RS9 = Server.CreateObject("ADODB.recordset")
RS9.ActiveConnection = Conn1
SQL9 = "SELECT COUNT(*) AS SuppressDenied "
SQL9 = SQL9 & "FROM [SmartPiXL].[dbo].[LC_IP_Logger_GlobalOptOut] "
SQL9 = SQL9 & "WHERE datediff(year, timestamp, getdate()) > 0 "
SQL9 = SQL9 & "AND RequestType = '3' "
SQL9 = SQL9 & "AND Fullfilled = '0'"

'response.write SQL9


RS1.Open SQL1
RS2.Open SQL2
RS3.Open SQL3
RS4.Open SQL4
RS5.Open SQL5
RS6.Open SQL6
RS7.Open SQL7
RS8.Open SQL8
RS9.Open SQL9
%>

<%
DIM RequestToKnowCount
RequestToKnowCount = RS1("KnowCount")

'response.write(RequestToKnowCount)
response.write("<p>Request To Know Total Count: " & RequestToKnowCount & "</p>" & vbcrlf)
%>

<%
DIM RequestToKnowFullfilled
RequestToKnowFullfilled = RS2("KnowFullfilled")

'response.write(RequestToKnowFullfilled)
response.write("<p>Request To Know Fullfilled Count: " & RequestToKnowFullfilled & "</p>" & vbcrlf)
%>

<%
DIM RequestToKnowDenied
RequestToKnowDenied = RS3("KnowDenied")

'response.write(RequestToKnowDenied)
response.write("<p>Request To Know Denied Count: " & RequestToKnowDenied & "</p>" & vbcrlf)
%>

<%
DIM RequestToDeleteCount
RequestToDeleteCount = RS4("DeleteCount")

'response.write(RequestToDeleteCount)
response.write("<p>Request To Delete Total Count: " & RequestToDeleteCount & "</p>" & vbcrlf)
%>

<%
DIM RequestToDeleteFullfilled
RequestToDeleteFullfilled = RS5("DeleteFullfilled")

'response.write(RequestToDeleteFullfilled)
response.write("<p>Request To Delete Fullfilled Count: " & RequestToDeleteFullfilled & "</p>" & vbcrlf)
%>
<%
DIM RequestToDeleteDenied
RequestToDeleteDenied = RS6("DeleteDenied")

'response.write(RequestToDeleteDenied)
response.write("<p>Request To Delete Denied Count: " & RequestToDeleteDenied & "</p>" & vbcrlf)
%>

<%
DIM RequestToSuppressCount
RequestToSuppressCount = RS7("SuppressCount")

'response.write(RequestToSuppressCount)
response.write("<p>Request To Suppress Total Count: " & RequestToSuppressCount & "</p>" & vbcrlf)
%>

<%
DIM RequestToSuppressFullfilled
RequestToSuppressFullfilled = RS8("SuppressFullfilled")

'response.write(RequestToSuppressFullfilled)
response.write("<p>Request To Suppress Fullfilled Count: " & RequestToSuppressFullfilled & "</p>" & vbcrlf)
%>

<%
DIM RequestToSuppressDenied
RequestToSuppressDenied = RS9("SuppressDenied")

'response.write(RequestToSuppressDenied)
response.write("<p>Request To Suppress Denied Count: " & RequestToSuppressDenied & "</p>" & vbcrlf)
%>



<%
Conn1.close
SET Conn1 = nothing
%>

Year Minus 1: 
<%
response.write Year(dateadd("yyyy", -1, date))
%>

</br>
</body>
</html>