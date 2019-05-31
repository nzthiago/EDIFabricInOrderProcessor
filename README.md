# EDIFabricInOrderProcessor
An Azure Function to decode and debatch X12 Shipment Orders. It will process them in order per sender from Service Bus, and send to a second Service Bus queue in order including the Shipment ID number in the session so those can be processed in parallel but still maintaining order for each sender + shipment ID. 

Basic getting started:
- Create two Azure Service Bus queues with Sessions enabled
- Create an Integration Account
  - Create two partners
  - Create an agreement between the partners
- An Azure Storage Account or use the local emulator if using Visual Studio in Windows 
  
Configure/create a local.settings.json file on the root of the project and fill out the following:
```json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "YOUR_STORAGE_ACCOUNT_CONNECTION_STRING",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet",
        "x12inboundqueuename": "YOUR_FIRST_QUEUE_NAME",
        "x12204inboundqueuename": "YOUR_SECOND_QUEUE_NAME",
        "AzureSubscription": "YOUR_AZURE_SUBSCRIPTION_ID",
        "ResourceGroupName": "YOUR_INTEGRATION_ACCOUNT_RESOURCE_GROUP",
        "IntegrationAccountName": "YOUR_INTEGRATION_ACCOUNT_NAME",
        "InboundServiceBusConnectionString": "YOUR_SERVICE_BUS_CONNECTION_STRING"
    }
}
```

Start the function locally using VS Code or Visual Studio, then send X12 204 messages to it using the sender ID as the Session ID.
The code will:
- Check for an agreement in the Azure Integration Account between sender and receiver in the ISA segment
- Extract the Shipment ID Number from each 204 segment
- Serialize each 204 segment into XML
- Use the original Session ID (Sender ID) plus the Shipment ID Number of each order and send them in order to a second Service Bus

You can then create a processor in Logic Apps or Functions or other to process the orders from that second queue, and fan out a processor per sender + shipment ID.

Test changing the Session ID for X12 messages going into the first queue, and then change the B204 values for the shipments as well, to see the new sessions show up on the second queue.
