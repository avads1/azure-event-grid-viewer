using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using viewer.Hubs;
using viewer.Models;
using System.Net.Mail;
using System.Net.Mime;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;

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

        private static void SendEmail(string jsonData, string callerFunc)
        {

            var client = new SmtpClient("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("nighthawks.ztna@gmail.com", "ztna@123"),
                EnableSsl = true
            };
            var jArrObj = JArray.Parse(jsonData);
            Dictionary<string, Tuple<string, string>> imageNamesMap = buildImageNamesMap();
            Tuple<string, string> value;
            string reqImageName = (string)jArrObj[0]["data"]["target"]["repository"];
            string reqImageTag = (string)jArrObj[0]["data"]["target"]["tag"];
            if (!imageNamesMap.TryGetValue(reqImageName, out value))
            {
                return;
            }
            string body = "";
            body = body + imageNamesMap[reqImageName].Item1 + ": " + reqImageName + "\n" + imageNamesMap[reqImageName].Item2 + ": " + reqImageTag + "\n";
            Console.WriteLine("Request body : \n" + body);
            string path = @"versions.yaml";
            if (!System.IO.File.Exists(path))
            {
                writeToVersionsFile(path, body, false);
            }
            else
            {
               // var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().WithNamingConvention(PascalCaseNamingConvention.Instance).Build();
                //var myConfig = deserializer.Deserialize<BuildImageCfgModel>(System.IO.File.ReadAllText(path));
                String content = System.IO.File.ReadAllText(path);
                //TextReader reader = System.IO.File.OpenText(path);
                //var yaml = new YamlStream();
                //yaml.Load(reader);
                //var mapping = (YamlMappingNode)yaml.Documents[0].RootNode;
                string key = imageNamesMap[reqImageName].Item2;
                
                if (content.Contains(key)) {
                    //((YamlScalarNode)mapping.Children[key]).Value = reqImageName;
                    return;
                }
                else {
                    writeToVersionsFile(path, body, true);
                }
            }

            MailMessage mail = new MailMessage("nighthawks.ztna@gmail.com", "nighthawks.ztna@gmail.com");
            client.DeliveryMethod = SmtpDeliveryMethod.Network;
            mail.Subject = "Webhook-event-file " + callerFunc;
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
            Console.WriteLine("Sent Email succesfully with data : " + body);
        }

        private static void writeToVersionsFile(string path, string body, bool append)
        {
            if (append)
            {
                using (StreamWriter writer = new StreamWriter(path, append))
                {
                    writer.WriteLine(body);
                    writer.Close();

                }
            }
            else
            {
                using (StreamWriter writer = System.IO.File.CreateText(path))
                {
                    writer.WriteLine(body);
                    writer.Close();
                }
            }

        }

        private static String readVersionsFile(string path)
        {
            string content = "";
            using (StreamReader sr = System.IO.File.OpenText(path))
            {

                string s = "";
                while ((s = sr.ReadLine()) != null)
                {
                    content = content + s;

                }
                sr.Close();
            }

            return content;
        }

        private static Dictionary<string, Tuple<string, string>> buildImageNamesMap()
        {
            Dictionary<string, Tuple<string, string>> imageNamesMap = new Dictionary<string, Tuple<string, string>>();
            imageNamesMap.Add("naas-core", new Tuple<string, string>("TalonImageName", "TalonImageTag"));
            imageNamesMap.Add("b2e-conftrans", new Tuple<string, string>("B2EImageName", "B2EImageTag"));
            imageNamesMap.Add("tunnel-server", new Tuple<string, string>("TunnelImageName", "TunnelImageTag"));
            return imageNamesMap;
        }

        #endregion
    }
}