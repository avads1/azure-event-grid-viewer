using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using viewer.Hubs;
using viewer.Models;
using System.Net.Mail;
using System.Net.Mime;

namespace viewer.Controllers
{
    [Route("api/[controller]")]
    public class UpdatesController : Controller
    {
        #region Data Members

        private bool EventTypeSubcriptionValidation
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "SubscriptionValidation";

        private bool EventTypeNotification
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "Notification";

        private readonly IHubContext<GridEventsHub> _hubContext;

        #endregion

        #region Constructors

        public UpdatesController(IHubContext<GridEventsHub> gridEventsHubContext)
        {
            this._hubContext = gridEventsHubContext;
        }

        #endregion

        #region Public Methods

        [HttpOptions]
        public async Task<IActionResult> Options()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var webhookRequestOrigin = HttpContext.Request.Headers["WebHook-Request-Origin"].FirstOrDefault();
                var webhookRequestCallback = HttpContext.Request.Headers["WebHook-Request-Callback"];
                var webhookRequestRate = HttpContext.Request.Headers["WebHook-Request-Rate"];
                HttpContext.Response.Headers.Add("WebHook-Allowed-Rate", "*");
                HttpContext.Response.Headers.Add("WebHook-Allowed-Origin", webhookRequestOrigin);
            }

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var jsonContent = await reader.ReadToEndAsync();

                // Check the event type.
                // Return the validation code if it's 
                // a subscription validation request. 
                if (EventTypeSubcriptionValidation)
                {
                    return await HandleValidation(jsonContent);
                }
                else if (EventTypeNotification)
                {
                    // Check to see if this is passed in using
                    // the CloudEvents schema
                    if (IsCloudEvent(jsonContent))
                    {
                        return await HandleCloudEvent(jsonContent);
                    }

                    return await HandleGridEvents(jsonContent);
                }

                return BadRequest();                
            }
        }

        #endregion

        #region Private Methods

        private async Task<JsonResult> HandleValidation(string jsonContent)
        {
            var gridEvent =
                JsonConvert.DeserializeObject<List<GridEvent<Dictionary<string, string>>>>(jsonContent)
                    .First();
            SendEmail(jsonContent, "HandleValidation");
            await this._hubContext.Clients.All.SendAsync(
                "gridupdate",
                gridEvent.Id,
                gridEvent.EventType,
                gridEvent.Subject,
                gridEvent.EventTime.ToLongTimeString(),
                jsonContent.ToString());

            // Retrieve the validation code and echo back.
            var validationCode = gridEvent.Data["validationCode"];
            return new JsonResult(new
            {
                validationResponse = validationCode
            });
        }

        private async Task<IActionResult> HandleGridEvents(string jsonContent)
        {
            var events = JArray.Parse(jsonContent);
            SendEmail(jsonContent, "HandleGridEvents");
            foreach (var e in events)
            {
                // Invoke a method on the clients for 
                // an event grid notiification.                        
                var details = JsonConvert.DeserializeObject<GridEvent<dynamic>>(e.ToString());
                await this._hubContext.Clients.All.SendAsync(
                    "gridupdate",
                    details.Id,
                    details.EventType,
                    details.Subject,
                    details.EventTime.ToLongTimeString(),
                    e.ToString());
            }

            return Ok();
        }

        private async Task<IActionResult> HandleCloudEvent(string jsonContent)
        {
            var details = JsonConvert.DeserializeObject<CloudEvent<dynamic>>(jsonContent);
            var eventData = JObject.Parse(jsonContent);
            SendEmail(jsonContent, "HandleCloudEvent");
            await this._hubContext.Clients.All.SendAsync(
                "gridupdate",
                details.Id,
                details.Type,
                details.Subject,
                details.Time,
                eventData.ToString()
            );

            return Ok();
        }

        private static bool IsCloudEvent(string jsonContent)
        {
            // Cloud events are sent one at a time, while Grid events
            // are sent in an array. As a result, the JObject.Parse will 
            // fail for Grid events. 
            try
            {
                // Attempt to read one JSON object. 
                var eventData = JObject.Parse(jsonContent);

                // Check for the spec version property.
                var version = eventData["specversion"].Value<string>();
                if (!string.IsNullOrEmpty(version)) return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return false;
        }

        private static void SendEmail(string jsonData, string callerFunc) {

            var client = new SmtpClient("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("nighthawks.ztna@gmail.com", "ztna@123"),
                EnableSsl = true
            };
            var jArrObj = JArray.Parse(jsonData);
            Dictionary<string, Tuple<string, string>> imageMap = new Dictionary<string, Tuple<string, string>>();
            imageMap.Add("naas-core", new Tuple<string, string>("TalonImageName", "TalonImageTag"));
            imageMap.Add("b2e-conftrans", new Tuple<string, string>("B2EImageName", "B2EImageTag"));
            imageMap.Add("tunnel-server", new Tuple<string, string>("TunnelImageName", "TunnelImageTag"));
            Tuple<string, string> value;
            string reqImageName = (string)jArrObj[0]["data"]["target"]["repository"];
            string reqImageTag = (string)jArrObj[0]["data"]["target"]["tag"];
            if (!imageMap.TryGetValue(reqImageName, out value))
            {
                return;
            }
            string body = "";
            body = body + imageMap[reqImageName].Item1 + ": " + reqImageName + "\n"+imageMap[reqImageName].Item2+": " + reqImageTag + "\n";
            Console.WriteLine("Request body : \n"+body);
            string path = @"versions.yaml";
            if (!System.IO.File.Exists(path))
            {

                using (StreamWriter writer = System.IO.File.CreateText(path))
                {
                    writer.WriteLine(body);
                    writer.Close();

                }
            } else {
                using (StreamWriter writer = new StreamWriter(path,true))
                {
                    writer.WriteLine(body);
                    writer.Close();

                }
            }

            MailMessage mail = new MailMessage("nighthawks.ztna@gmail.com", "nighthawks.ztna@gmail.com");
            client.DeliveryMethod = SmtpDeliveryMethod.Network;
            mail.Subject = "Webhook-event-file "+ callerFunc;
            // Set the read file as the body of the message
            mail.Body = body;

            // Add the file attachment to this email message.
            Attachment data = new Attachment(path, MediaTypeNames.Application.Octet);
            ContentDisposition disposition = data.ContentDisposition;
            disposition.CreationDate = System.IO.File.GetCreationTime(path);
            disposition.ModificationDate = System.IO.File.GetLastWriteTime(path);
            disposition.ReadDate = System.IO.File.GetLastAccessTime(path);
            mail.Attachments.Add(data);
            
            // Send the email            
            client.Send(mail);
            data.Dispose();
            // client.Send("nighthawks.ztna@gmail.com", "nighthawks.ztna@gmail.com", "test", "testbody");
            Console.WriteLine("Sent Email succesfully with data : "+body);
        }

        #endregion
    }
}