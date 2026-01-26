
<!DOCTYPE html>
<html>
<head>
<title>Page Title</title>
</head>
<body>

<form id="form" method="post"  target="_blank" action="contact.asp" name="form" >
<div class="inp_h">
<input class="inp_2" type="text"  name="name" value="Full Name:"  onfocus="this.value=''" />
</div>
<div class="inp_h">
<input class="inp_2" type="text"  name="mail" value="E-mail:"  onfocus="this.value=''" />
</div>
<div><textarea class="inp_3" rows="30" cols="40" name="message" onfocus="this.value=''">Message:</textarea></div>
<div style="padding:12px 0 0 0;">
<a href="#" onclick="document.getElementById('form').reset()">Reset</a>
<br />  
<a href="#" onclick="document.getElementById('form').submit()">Submit</a>
<br />                                                                
</div>
</form>

</body>
</html>

