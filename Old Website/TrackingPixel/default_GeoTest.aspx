<%@ Page Language="C#" AutoEventWireup="true" CodeFile="Default.aspx2.cs" Inherits="_Default" %>

<!DOCTYPE html>

<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title></title>
</head>
<body onload="getLocation()">
    <form id="form1" runat="server">
    <div>
    </div>
    </form>
<p id="Geo"></p>

<script>
var x = document.getElementById("Geo");
    
function getLocation() 
{
    if (navigator.geolocation)
    {
        navigator.geolocation.getCurrentPosition(showPosition, showError);
    } 
	else 
	{ 
        x.innerHTML = "Geolocation is not supported by this browser.";
    }
}

function showPosition(position) 
{ 
    x.innerHTML = "Latitude: " + position.coords.latitude + 
    "<br>Longitude: " + position.coords.longitude + 
    "<br>accuracy: " + position.coords.accuracy +
    "<br>altitude: " + position.coords.altitude +
    "<br>altitudeAccuracy: " + position.coords.altitudeAccuracy +
    "<br>heading: " + position.coords.heading +
    "<br>speed: " + position.coords.speed +
    "<br>timestamp: " + position.timestamp;
}

function showError(error) 
{
    switch(error.code) 
	{
        case error.PERMISSION_DENIED:
            x.innerHTML = "User denied the request for Geolocation."
            break;
        case error.POSITION_UNAVAILABLE:
            x.innerHTML = "Location information is unavailable."
            break;
        case error.TIMEOUT:
            x.innerHTML = "The request to get user location timed out."
            break;
        case error.UNKNOWN_ERROR:
            x.innerHTML = "An unknown error occurred."
            break;
	}
}
</script>
</body>
</html>
