﻿using IoTSharp.Data;
using IoTSharp.Extensions;
using IoTSharp.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using MQTTnet.Client.Receiving;
using MQTTnet.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IoTSharp.Services
{
    public class MqttClientService : IHostedService
    {
        private readonly ILogger _logger;
        readonly IMqttClient _mqtt;
        private readonly IMqttClientOptions _clientOptions;
        private ApplicationDbContext _dbContext;
        private IServiceScope _serviceScope;
        public MqttClientService(ILogger<MqttClientService> logger, IServiceScopeFactory scopeFactor, IMqttClient mqtt, IMqttClientOptions clientOptions)
        {
            _logger = logger;
            _mqtt = mqtt;
            _clientOptions = clientOptions;
            mqtt.ApplicationMessageReceivedHandler = new MqttApplicationMessageReceivedHandlerDelegate(args => Mqtt_ApplicationMessageReceived(mqtt, args));
            mqtt.ConnectedHandler  =new  MqttClientConnectedHandlerDelegate (args=> Mqtt_ConnectedAsync(mqtt,args));
            mqtt.DisconnectedHandler =new MqttClientDisconnectedHandlerDelegate   (args=> Mqtt_DisconnectedAsync(mqtt,args));
            _serviceScope = scopeFactor.CreateScope();
            _dbContext = _serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        }

        private async void Mqtt_DisconnectedAsync(object sender, MqttClientDisconnectedEventArgs e)
        {
            _logger.LogInformation($"DISCONNECTED FROM SERVER  ClientWasConnected:{e.ClientWasConnected}, Exception={ e.Exception.Message}");
            try
            {
                await _mqtt.ConnectAsync(_clientOptions);
                _logger.LogInformation("RECONNECT AGAIN");
            }
            catch (Exception exception)
            {
                _logger.LogError("CONNECTING FAILED", exception);
            }
        }

        private async void  Mqtt_ConnectedAsync(object sender, MqttClientConnectedEventArgs e)
        {
            _logger.LogInformation($"CONNECTED  IsSessionPresent:  {e.AuthenticateResult.IsSessionPresent } ResultCode: { e.AuthenticateResult.ResultCode}");
         var  subresult1=await   _mqtt.SubscribeAsync("/devices/telemetry/#");
            var subresult2= await  _mqtt.SubscribeAsync("/devices/attributes/#");
        }

        Dictionary<string, Device> Devices => MqttEventsHandler.Devices;


        private void Mqtt_ApplicationMessageReceived(object sender, MQTTnet.MqttApplicationMessageReceivedEventArgs e)
        {

            _logger.LogInformation($"Received  : {e.ApplicationMessage.Topic}");
            if (e.ApplicationMessage.Topic.ToLower().StartsWith("/devices/telemetry"))
            {
                if (Devices.ContainsKey(e.ClientId))
                {

                    var device = Devices[e.ClientId];

                    Task.Run(async () =>
                    {
                        try
                        {
                            var telemetrys = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(e.ApplicationMessage.ConvertPayloadToString());
                            var result = await _dbContext.SaveAsync<TelemetryLatest, TelemetryData>(telemetrys, device, DataSide.ClientSide);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Can't upload telemetry to device {device.Name}({device.Id}).the payload is {e.ApplicationMessage.ConvertPayloadToString()}");
                        }
                    });
                }
            }
            else if (e.ApplicationMessage.Topic.ToLower().StartsWith("/devices/attributes"))
            {
                if (Devices.ContainsKey(e.ClientId))
                {
                    var device = Devices[e.ClientId];
                    Task.Run(async () =>
                    {
                        try
                        {
                            var attributes = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(e.ApplicationMessage.ConvertPayloadToString());
                            var result = await _dbContext.SaveAsync<AttributeLatest, AttributeData>(attributes, device, DataSide.ClientSide);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Can't upload attributes to device {device.Name}({device.Id}).the payload is \"{e.ApplicationMessage.ConvertPayloadToString()}\"");
                        }
                    });
                }
            }
        }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                try
                {
                    _mqtt.ConnectAsync(_clientOptions);
                    _logger.LogInformation("CONNECTED");
                }
                catch (Exception exception)
                {
                    _logger.LogError("CONNECTING FAILED", exception);
                }
            });
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _mqtt.DisconnectedHandler = null;
            return _mqtt.DisconnectAsync();
        }
    }
}
