<%
dim reresponse, SecretKey, VarString, url
reresponse= Request.form("g-recaptcha-response")
SecretKey = "6LdMZl4UAAAAABInyM56GspJuaWVlYbob6Ra2Hor"

VarString = "?secret=" & SecretKey & _
          "&response=" & reresponse
url="https://www.google.com/recaptcha/api/siteverify" & VarString

Dim objXmlHttp
Set objXmlHttp = Server.CreateObject("Msxml2.ServerXMLHTTP")
objXmlHttp.open "POST", url, False
objXmlHttp.setRequestHeader "Content-Type", "application/x-www-form-urlencoded"
objXmlHttp.send

Dim ResponseString
ResponseString = objXmlHttp.responseText
'Response.Write(ResponseString)
Set objXmlHttp = Nothing

dim firstname, lastname, address1, address2, city, state, zip, email, phone, ipaddress, requesttype
If instr(ResponseString, "success" & chr(34) &": true")>0  then
  'response.write("recaptcha OK")
  firstname = request.form("firstname")
  lastname = request.form("lastname")
  address1 = request.form("address1")
  address2 = request.form("address2")
  city = request.form("city")
  state = request.form("state")
  zip = request.form("zip")
  email = request.form("email")
  email = replace(email,"'","")
  phone = request.form("phone")
  requesttype = request.form("requesttype")
  ipaddress = request.servervariables("remote_addr")

  
  
  set oConn = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _ 
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
oConn.Open connStr

  SQL = "exec SP_PiXL_Unsub '" + firstname + "', '" + lastname + "', '" + address1 + "', '" + address2 + "', '" + city + "', '" + state + "', '" + zip + "', '" + email + "', '" + phone + "', '" + requesttype + "', '" + ipaddress + "'"
  oConn.execute(SQL)
  response.redirect("thankyou.html")
else
  response.redirect("https://smartpixl.com/contact/")
end if


%>