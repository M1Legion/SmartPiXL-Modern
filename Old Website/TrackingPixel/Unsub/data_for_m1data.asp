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
SQL1 = "SELECT Request_to_Know_Received AS KnowCount FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL1

SET RS2 = Server.CreateObject("ADODB.recordset")
RS2.ActiveConnection = Conn1
SQL2 = "SELECT Request_to_Know_Fufilled AS KnowFullfilled FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL2

SET RS3 = Server.CreateObject("ADODB.recordset")
RS3.ActiveConnection = Conn1
SQL3 = "SELECT Request_to_Know_Denied AS KnowDenied FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL3

SET RS4 = Server.CreateObject("ADODB.recordset")
RS4.ActiveConnection = Conn1
SQL4 = "SELECT Request_to_Delete_Received AS DeleteCount FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL4

SET RS5 = Server.CreateObject("ADODB.recordset")
RS5.ActiveConnection = Conn1
SQL5 = "SELECT Request_to_Delete_Fulfilled AS DeleteFullfilled FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL5

SET RS6 = Server.CreateObject("ADODB.recordset")
RS6.ActiveConnection = Conn1
SQL6 = "SELECT Request_to_Delete_Denied AS DeleteDenied FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL6

SET RS7 = Server.CreateObject("ADODB.recordset")
RS7.ActiveConnection = Conn1
SQL7 = "SELECT Request_to_Suppress_Recieved AS SuppressCount FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL7

SET RS8 = Server.CreateObject("ADODB.recordset")
RS8.ActiveConnection = Conn1
SQL8 = "SELECT Request_to_Suppress_Fulfilled AS SuppressFullfilled FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

'response.write SQL8

SET RS9 = Server.CreateObject("ADODB.recordset")
RS9.ActiveConnection = Conn1
SQL9 = "SELECT Request_to_Suppress_Denied AS SuppressDenied FROM [SmartPiXL].[dbo].[OptOut_Counts] order by year desc; "

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

DIM RequestToKnowCount
RequestToKnowCount = RS1("KnowCount")

DIM RequestToKnowFullfilled
RequestToKnowFullfilled = RS2("KnowFullfilled")

DIM RequestToKnowDenied
RequestToKnowDenied = RS3("KnowDenied")

DIM RequestToDeleteCount
RequestToDeleteCount = RS4("DeleteCount")

DIM RequestToDeleteFullfilled
RequestToDeleteFullfilled = RS5("DeleteFullfilled")

DIM RequestToDeleteDenied
RequestToDeleteDenied = RS6("DeleteDenied")

DIM RequestToSuppressCount
RequestToSuppressCount = RS7("SuppressCount")

DIM RequestToSuppressFullfilled
RequestToSuppressFullfilled = RS8("SuppressFullfilled")

DIM RequestToSuppressDenied
RequestToSuppressDenied = RS9("SuppressDenied")

Conn1.close
SET Conn1 = nothing

%>
<table class="tableSmartPixl" width="100%" border="1" cellpadding="5" cellspacing="0">
  <tbody>
    <tr class="row">
      <th class="header-cell">CCPA Reporting Requirements</th>
      <th class="header-cell">Metrics for Calendar Year ending December 31, <% response.write Year(dateadd("yyyy", -1, date)) %></th>
    </tr>
    <tr class="row">
      <td class="cell">Total Volume of Requests to Know Received</td>
      <td class="cell"><% response.write(RequestToKnowCount) %></td>
    </tr>
    <tr class="row">
      <td class="cell">Number of Requests to Know Fulfilled</td>
      <td class="cell"><% response.write(RequestToKnowFullfilled) %></td>
    </tr>
    <tr class="row">
      <td class="cell">Number of Requests to Know Denied</td>
      <td class="cell"><% response.write(RequestToKnowDenied) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Median Number of Days to Respond to Requests to Know</td>
      <td class="cell">3 Days</td>
    </tr>
    <tr class="row">
      <td class="cell">&nbsp;</td>
      <td class="cell">&nbsp;</td>
    </tr>
    <tr class="row">
      <td class="cell">Total Volume of Requests to Delete Received</td>
      <td class="cell"><% response.write(RequestToDeleteCount) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Number of Requests to Delete Fulfilled</td>
      <td class="cell"><% response.write(RequestToDeleteFullfilled) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Number of Requests to Delete Denied</td>
      <td class="cell"><% response.write(RequestToDeleteDenied) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Median Number of Days to Respond to Requests to Delete</td>
      <td class="cell">3 Days</td>
    </tr>
    <tr class="row">
      <td class="cell">&nbsp;</td>
      <td class="cell">&nbsp;</td>
    </tr>
    <tr class="row">
      <td class="cell">Total Volume of Requests to Opt-Out Received</td>
      <td class="cell"><% response.write(RequestToSuppressCount) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Number of Opt-Out Requests Fulfilled</td>
      <td class="cell"><% response.write(RequestToSuppressFullfilled) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Number of Opt-Out Requests Denied</td>
      <td class="cell"><% response.write(RequestToSuppressDenied) %> </td>
    </tr>
    <tr class="row">
      <td class="cell">Median Number of Days to Respond to Requests to Opt-Out</td>
      <td class="cell">3 Days</td>
    </tr>
  </tbody>
</table>
