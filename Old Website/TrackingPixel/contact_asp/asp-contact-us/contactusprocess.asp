<%
''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''
'  Freeware from Seiretto.com
'    available at  http://asp.thedemosite.co.uk
'
'   DON'T forget to change the mail_to email address below!!!
'''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''
mail_to = "support@smartpixl.com"

Dim error
error = 0
For Each f In Request.Form
  If Request.Form(f) = "" Then 
    error = 1
  End If
Next
If error=1 Then
  response.redirect "error.html"
Else
Dim f, emsg, mail_to, r, o, c, other
fline = "_______________________________________________________________________"& vbNewLine   
hline = vbNewLine & "_____________________________________"& vbNewLine   
emsg = ""

For Each f In Request.Form
   If mid(f,1,1)<>"S"  = True Then 'do not save if input name starts with S
     emsg  = emsg & f & " = " &  Trim(Request.Form(f)) & hline
   End If
Next

Set objNewMail = Server.CreateObject("CDONTS.NewMail")
	objNewMail.From = Request("Email Address")
	objNewMail.Subject = "Smart PiXL Contact Form"
	objNewMail.To = mail_to
	objNewMail.Body = emsg & fline
	objNewMail.Configuration.Fields.Item _
	("http://schemas.microsoft.com/cdo/configuration/sendusing") = 2
	'Name or IP of remote SMTP server
	objNewMail.Configuration.Fields.Item _
	("http://schemas.microsoft.com/cdo/configuration/smtpserver") = "smtp.gmail.com"
	'Server port
	objNewMail.Configuration.Fields.Item _
	("http://schemas.microsoft.com/cdo/configuration/smtpserverport") = 587 
	objNewMail.Configuration.Fields.Update
	objNewMail.Send
	Set objNewMail = Nothing

response.redirect "thankyou.html"
End if


%>