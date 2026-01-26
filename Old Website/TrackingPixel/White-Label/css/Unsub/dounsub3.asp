

<%
dim firstname, lastname, address1, address2, city, state, zip, email, phone, ipaddress, requesttype

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


if(isset($_POST['g-recaptcha-response']) && !empty($_POST['g-recaptcha-response']))
{
	//your site secret key
	$secret = $SECRET_KEY;
	//get verify response data
	$verifyResponse = file_get_contents('https://www.google.com/recaptcha/api/siteverify?secret='.$secret.'&response='.$_POST['g-recaptcha-response']);
	$responseData = json_decode($verifyResponse);
	if($responseData->success)
	{
		//actions once the verification is success
	}
	else
	{
		//action that the verification is failed
	}


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
%>
