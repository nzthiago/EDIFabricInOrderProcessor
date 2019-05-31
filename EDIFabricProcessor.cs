using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using Microsoft.Azure.Management.Logic;
using Microsoft.Azure.Management.Logic.Models;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.Services.AppAuthentication; 
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;
using Microsoft.Rest.Azure;
using EdiFabric.Core.Model.Edi;
using EdiFabric.Core.Model.Edi.X12;
using EdiFabric.Framework.Readers;
using EdiFabric.Templates.X12;

namespace EDIFabricInOrderProcessor
{
    public class EDIFabricProcessor
    {
        private static LogicManagementClient client = null;
        private static List<IntegrationAccountAgreement> agreements = null;
        private static string subscriptionId = Environment.GetEnvironmentVariable("AzureSubscription");
        private static string rgName = Environment.GetEnvironmentVariable("ResourceGroupName");
        private static string iaName = Environment.GetEnvironmentVariable("IntegrationAccountName");

        [FunctionName("EDIFabricProcessor")]
        public async Task Run(
            [ServiceBusTrigger("%x12inboundqueuename%", Connection = "InboundServiceBusConnectionString", IsSessionsEnabled = true)]
            Message inboundMsg,
            [ServiceBus("%x12204inboundqueuename%", Connection="InboundServiceBusConnectionString")]IAsyncCollector<Message> outMessages,
            ILogger log, CancellationToken cancellationToken)
        {
            log.LogInformation("C# Service Bus trigger function processed a request.");
            
            if (string.IsNullOrEmpty(inboundMsg.SessionId))
            {
                throw new Exception($"No Session ID in this message.");
            }
            
            //Read EDI message
            List<IEdiItem> ediItems;
            using (Stream stream = new MemoryStream(inboundMsg.Body))
            {
                using(var reader = new X12Reader(stream, "EdiFabric.Templates.X12"))
                {
                    ediItems = (await reader.ReadToEndAsync()).ToList();
                }
            }

            //Get Logic App Management Client using Azure Token
            if (client == null)
            {
                var provider = new AzureServiceTokenProvider();
                var token = await provider.GetAccessTokenAsync("https://management.azure.com/");
                client = new LogicManagementClient(new TokenCredentials(token)) { SubscriptionId = subscriptionId };
            }

            //Get Agreements from Integration Account
            if (agreements == null)
            {
                agreements = new List<IntegrationAccountAgreement>();
                IPage<IntegrationAccountAgreement> pages = await client.IntegrationAccountAgreements.ListAsync(rgName, iaName, null, cancellationToken);
                agreements.AddRange(pages);
                while(pages.NextPageLink != null)
                {
                    pages = await client.IntegrationAccountAgreements.ListNextAsync(pages.NextPageLink);
                    agreements.AddRange(pages);
                }
            }

            //Look for a matching agreement based on ISA header 
            var isa = ediItems.OfType<ISA>().FirstOrDefault();
            if (isa == null)
            {
                throw new Exception($"No ISA element found.");
            }
            var agreementName = from a in agreements
                                  where a.GuestIdentity.Qualifier == isa.SenderIDQualifier_5.Trim()
                                  &&  a.GuestIdentity.Value == isa.InterchangeSenderID_6.Trim()
                                  &&  a.HostIdentity.Value == isa.ReceiverIDQualifier_7.Trim()
                                  &&  a.HostIdentity.Value == isa.InterchangeReceiverID_8.Trim()
                                  select a.Name.FirstOrDefault();

            if (agreementName == null)
            {
                throw new Exception($"Agreement between sender partner with qualifier {isa.SenderIDQualifier_5} and ID {isa.InterchangeSenderID_6} and receiver partner with qualifier {isa.ReceiverIDQualifier_7} and ID {isa.InterchangeReceiverID_8} not found");
            }

            //Loop through each shipment and send to subsequent session enabled queue but this time using shipmentID in the session to fan out per order
            var shipmentItems = ediItems.OfType<TS204>().ToList();
            foreach (var shipmentItem in shipmentItems)
            {
                //Todo: table lookup of where each customer puts their Shipment ID number as they might be different
                var shipmentID = shipmentItem.B2.ShipmentIdentificationNumber_04;
                var xml = Serialize(shipmentItem);

                Message outMessage = new Message(Encoding.UTF8.GetBytes(xml.ToString()));
                outMessage.SessionId = $"{inboundMsg.SessionId}+{shipmentID}";
                await outMessages.AddAsync(outMessage);
            }
        }

        public static XDocument Serialize(EdiMessage instance)
        {
            if (instance == null)
                throw new ArgumentNullException("instance");

            var serializer = new XmlSerializer(instance.GetType());
            using (var ms = new MemoryStream())
            {
                serializer.Serialize(ms, instance);
                ms.Position = 0;
                return XDocument.Load(ms, LoadOptions.PreserveWhitespace);
            }
        }
    }
}
