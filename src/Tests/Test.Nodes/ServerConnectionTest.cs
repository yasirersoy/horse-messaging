using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Horse.Messaging.Client;
using Horse.Messaging.Protocol;
using Test.Common;
using Xunit;

namespace Test.Nodes
{
    public class ServerConnectionTest
    {
        /// <summary>
        /// Connects to Horse Server and sends info message
        /// </summary>
        [Fact]
        public async Task ConnectWithInfo()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start();

            HorseClient client = new HorseClient();
            client.SetClientName("Test-" + port);
            await client.ConnectAsync("horse://localhost:" + port + "/path");

            Thread.Sleep(50);

            Assert.True(client.IsConnected);
            Assert.Equal(1, server.ClientConnected);
            server.Stop();
        }

        /// <summary>
        /// Connects to Horse Server and does not send info message
        /// </summary>
        [Fact]
        public async Task ConnectWithoutInfo()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start();

            List<TcpClient> clients = new List<TcpClient>();

            for (int i = 0; i < 50; i++)
            {
                TcpClient client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                clients.Add(client);
                Thread.Sleep(20);
                ThreadPool.UnsafeQueueUserWorkItem(async c =>
                {
                    byte[] buffer = new byte[128];
                    NetworkStream ns = client.GetStream();
                    try
                    {
                        while (c.Connected)
                        {
                            int read = await ns.ReadAsync(buffer);
                            if (read == 0)
                            {
                                c.Close();
                                c.Dispose();
                                break;
                            }
                        }
                    }
                    catch
                    {
                        c.Close();
                        c.Dispose();
                    }
                }, client, false);

                Assert.Equal(0, server.ClientConnected);
            }

            int connectedClients = clients.Count(x => x.Connected);
            Assert.Equal(connectedClients, clients.Count);

            await Task.Delay(10000);

            connectedClients = clients.Count(x => x.Connected);
            Assert.Equal(0, server.ClientConnected);
            Assert.Equal(0, connectedClients);
            server.Stop();
        }

        /// <summary>
        /// Connects to Horse Server and stays alive with PING and PONG messages
        /// </summary>
        [Fact]
        public async Task KeepAliveWithPingPong()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start();

            HorseClient client = new HorseClient();
            client.SetClientName("Test-" + port);
            client.Connect("horse://localhost:" + port + "/path");

            Thread.Sleep(25000);

            Assert.True(client.IsConnected);
            Assert.Equal(1, server.ClientConnected);
            server.Stop();
        }

        /// <summary>
        /// Connects to Horse Server and stays alive until PING time out (does not send PONG message)
        /// </summary>
        [Fact]
        public async Task DisconnectDueToPingTimeout()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start();

            TcpClient client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);

            NetworkStream stream = client.GetStream();
            stream.Write(PredefinedMessages.PROTOCOL_BYTES_V3);
            HorseMessage msg = new HorseMessage();
            msg.Type = MessageType.Server;
            msg.ContentType = KnownContentTypes.Hello;
            msg.SetStringContent("GET /\r\nName: Test-" + port);
            msg.CalculateLengths();
            HorseProtocolWriter.Write(msg, stream);
            await Task.Delay(1000);
            Assert.Equal(1, server.ClientConnected);

            ThreadPool.UnsafeQueueUserWorkItem(async s =>
            {
                byte[] buffer = new byte[128];
                while (client.Connected)
                {
                    int r = await s.ReadAsync(buffer);
                    if (r == 0)
                    {
                        client.Dispose();
                        break;
                    }
                }
            }, stream, false);

            await Task.Delay(15000);

            Assert.False(client.Connected);
            Assert.Equal(1, server.ClientDisconnected);
            server.Stop();
        }

        /// <summary>
        /// Connects to Horse Server and stays alive a short duration and disconnects again with concurrent clients
        /// </summary>
        [Theory]
        [InlineData(10, 20, 100, 500)]
        [InlineData(50, 50, 100, 500)]
        public async Task ConnectDisconnectStress(int concurrentClients, int connectionCount, int minAliveMs, int maxAliveMs)
        {
            Random rnd = new Random();
            int connected = 0;
            int disconnected = 0;

            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start();

            for (int i = 0; i < concurrentClients; i++)
            {
                Thread thread = new Thread(async () =>
                {
                    for (int j = 0; j < connectionCount; j++)
                    {
                        try
                        {
                            HorseClient client = new HorseClient();
                            client.Connect("horse://localhost:" + port);
                            Assert.True(client.IsConnected);
                            Interlocked.Increment(ref connected);
                            await Task.Delay(rnd.Next(minAliveMs, maxAliveMs));
                            client.Disconnect();
                            Interlocked.Increment(ref disconnected);
                            await Task.Delay(50);
                            Assert.True(client.IsConnected);
                        }
                        catch
                        {
                        }
                    }
                });
                thread.Start();
            }

            TimeSpan total = TimeSpan.FromMilliseconds(maxAliveMs * connectionCount);
            TimeSpan elapsed = TimeSpan.Zero;
            while (elapsed < total)
            {
                elapsed += TimeSpan.FromMilliseconds(100);
                await Task.Delay(100);
            }

            await Task.Delay(maxAliveMs);
            await Task.Delay(3000);
            Assert.Equal(connected, concurrentClients * connectionCount);
            Assert.Equal(disconnected, concurrentClients * connectionCount);
            server.Stop();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("*client*")]
        public async Task GetOnlineClients(string filter)
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start();

            HorseClient client = new HorseClient();
            client.SetClientType("client-test");
            client.SetClientName("client-test");
            await client.ConnectAsync("horse://localhost:" + port);

            var result = await client.Connection.GetConnectedClients(filter);
            Assert.Equal(HorseResultCode.Ok, result.Result.Code);
            Assert.NotNull(result.Model);
            var c = result.Model.FirstOrDefault();
            Assert.NotNull(c);
            Assert.Equal("client-test", c.Type);
            server.Stop();
        }
    }
}