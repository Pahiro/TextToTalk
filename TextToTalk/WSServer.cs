﻿using System;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using WebSocketSharp.Server;

namespace TextToTalk
{
    public class WsServer
    {
        private readonly WebSocketServer server;
        private readonly ServerBehavior behavior;

        public bool Active { get; private set; }

        public WsServer(int port)
        {
            if (port == 0)
            {
                port = 50665;
            }
            this.server = new WebSocketServer($"ws://localhost:{port}");
            this.behavior = new ServerBehavior();
            this.server.AddWebSocketService("/Messages", () => this.behavior);
        }

        public void Broadcast(string message)
        {
            if (!Active) throw new InvalidOperationException("Server is not active!");

            var ipcMessage = new IpcMessage(IpcMessageType.Say, message);
            this.behavior.SendMessage(JsonConvert.SerializeObject(ipcMessage));
        }

        public void Cancel()
        {
            if (!Active) throw new InvalidOperationException("Server is not active!");

            var ipcMessage = new IpcMessage(IpcMessageType.Cancel, string.Empty);
            this.behavior.SendMessage(JsonConvert.SerializeObject(ipcMessage));
        }

        public void Start()
        {
            if (Active) return;
            Active = true;
            this.server.Start();
        }

        public void Stop()
        {
            if (!Active) return;
            Active = false;
            this.server.Stop();
        }

        private class ServerBehavior : WebSocketBehavior
        {
            public void SendMessage(string message)
            {
                Send(message);
            }
            
            // Enable re-use of a websocket if the client disconnects
            protected override void OnClose(WebSocketSharp.CloseEventArgs e)
            {
                base.OnClose(e);

                var targetType = typeof(WebSocketBehavior);
                var base_websocket = targetType.GetField("_websocket", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                base_websocket.SetValue(this, null);
            }
        }

        [Serializable]
        private class IpcMessage
        {
            public string Type { get; set; }
            public string Payload { get; set; }

            public IpcMessage(IpcMessageType type, string payload)
            {
                Type = type.ToString();
                Payload = payload;
            }
        }

        private enum IpcMessageType
        {
            Say,
            Cancel,
        }
    }
}
