<%
dim reresponse, SecretKey, VarString, url
reresponse= Request.form("g-recaptcha-response")
SecretKey = "6LfHcmgbAAAAAKt-82Kqsr400zab54w-Q6bJ1gMx"

VarString = "?secret=" & SecretKey & _
          "&response=" & reresponse
url="https://www.google.com/recaptcha/api/siteverify" & VarString

Dim objXmlHttp
Set objXmlHttp = Server.CreateObject("MSXML2.XMLHTTP.6.0")
objXmlHttp.open "POST", url, False
objXmlHttp.setRequestHeader "Content-Type", "application/x-www-form-urlencoded"
objXmlHttp.send

Dim ResponseString
ResponseString = objXmlHttp.responseText
'Response.Write(ResponseString)
Set objXmlHttp = Nothing

dim firstname, lastname, address1, address2, city, state, zip, email, phone, ipaddress, requesttype, source
If instr(ResponseString, "success" & chr(34) &": true")>0  then
  'response.write("recaptcha OK")
  firstname = request.form("firstname")
  firstname = replace(firstname,"'","")

  lastname = request.form("lastname")
  lastname = replace(lastname,"'","")

  address1 = request.form("address1")
  address1 = replace(address1,"'","")

  address2 = request.form("address2")
  address2 = replace(address2,"'","")

  city = request.form("city")
  city = replace(city,"'","")

  state = request.form("state")
  state = replace(state,"'","")

  zip = request.form("zip")
  zip = replace(zip,"'","")

  email = request.form("email")
  email = replace(email,"'","")

  phone = request.form("phone")
  phone = replace(phone,"'","")

  requesttype = request.form("requesttype")
  
  ipaddress = request.servervariables("remote_addr")
  
  source = "smartpixl.com"
  
  UserAgent = request.servervariables("HTTP_USER_AGENT")

  
  
  set oConn = Server.CreateObject("ADODB.connection")
connStr = "Driver={ODBC Driver 13 for SQL Server};" & _ 
           "Server=127.0.0.1;" & _
           "Database=SmartPiXL;" & _
           "Uid=PiXL;" & _
           "Pwd=9f2A$_!;"
oConn.Open connStr

  SQL = "exec SP_PiXL_Unsub '" + firstname + "', '" + lastname + "', '" + address1 + "', '" + address2 + "', '" + city + "', '" + state + "', '" + zip + "', '" + email + "', '" + phone + "', '" + requesttype + "', '" + ipaddress + "', '" + source + "', '" + UserAgent + "'"
  oConn.execute(SQL)
  response.redirect("https://smartpixl.com/Unsub/limit-the-use-of-my-sensitive-personal-information_thankyou.html")
else
  response.redirect("https://smartpixl.com/Unsub/limit-the-use-of-my-sensitive-personal-information.html")
end if


%>