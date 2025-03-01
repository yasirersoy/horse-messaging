using System.Threading.Tasks;
using Horse.Messaging.Client;
using Horse.Messaging.Protocol;
using Horse.Messaging.Server.Queues;
using Horse.Messaging.Server.Routing;
using Test.Common;
using Xunit;

namespace Test.Routers
{
    public class RouterTest
    {
        [Fact]
        public async Task Distribute()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 5, Interaction = BindingInteraction.None});
            router.AddBinding(new QueueBinding {Name = "qbind-2", Target = "push-a-cc", Priority = 10, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 20, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-2", Target = "client-2", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            Assert.True(client1.IsConnected);

            HorseClient client2 = new HorseClient();
            client2.ClientId = "client-2";
            await client2.ConnectAsync("horse://localhost:" + port);
            Assert.True(client2.IsConnected);

            int client1Received = 0;
            int client2Received = 0;
            client1.MessageReceived += (c, m) => client1Received++;
            client2.MessageReceived += (c, m) => client2Received++;

            for (int i = 0; i < 4; i++)
            {
                HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
                Assert.Equal(HorseResultCode.Ok, result.Code);
            }

            await Task.Delay(500);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");
            HorseQueue queue2 = server.Rider.Queue.Find("push-a-cc");

            Assert.Equal(4, queue1.Manager.MessageStore.Count());
            Assert.Equal(4, queue2.Manager.MessageStore.Count());

            Assert.Equal(4, client2Received);
            Assert.Equal(4, client1Received);
            server.Stop();
        }

        [Fact]
        public async Task RoundRobin()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.RoundRobin);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 5, Interaction = BindingInteraction.None});
            router.AddBinding(new QueueBinding {Name = "qbind-2", Target = "push-a-cc", Priority = 10, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 20, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-2", Target = "client-2", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            Assert.True(client1.IsConnected);

            HorseClient client2 = new HorseClient();
            client2.ClientId = "client-2";
            await client2.ConnectAsync("horse://localhost:" + port);
            Assert.True(client2.IsConnected);

            int client1Received = 0;
            int client2Received = 0;
            client1.MessageReceived += (c, m) => client1Received++;
            client2.MessageReceived += (c, m) => client2Received++;

            for (int i = 0; i < 5; i++)
            {
                HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
                Assert.Equal(HorseResultCode.Ok, result.Code);
            }

            await Task.Delay(500);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");
            HorseQueue queue2 = server.Rider.Queue.Find("push-a-cc");

            Assert.Equal(1, queue1.Manager.MessageStore.Count());
            Assert.Equal(1, queue2.Manager.MessageStore.Count());

            Assert.Equal(1, client2Received);
            Assert.Equal(2, client1Received);
            server.Stop();
        }

        [Fact]
        public async Task OnlyFirst()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.OnlyFirst);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 5, Interaction = BindingInteraction.None});
            router.AddBinding(new QueueBinding {Name = "qbind-2", Target = "push-a-cc", Priority = 10, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 2, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-2", Target = "client-2", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            Assert.True(client1.IsConnected);

            HorseClient client2 = new HorseClient();
            client2.ClientId = "client-2";
            await client2.ConnectAsync("horse://localhost:" + port);
            Assert.True(client2.IsConnected);

            int client1Received = 0;
            int client2Received = 0;
            client1.MessageReceived += (c, m) => client1Received++;
            client2.MessageReceived += (c, m) => client2Received++;

            for (int i = 0; i < 4; i++)
            {
                HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
                Assert.Equal(HorseResultCode.Ok, result.Code);
            }

            await Task.Delay(500);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");
            HorseQueue queue2 = server.Rider.Queue.Find("push-a-cc");

            Assert.Equal(0, queue1.Manager.MessageStore.Count());
            Assert.Equal(4, queue2.Manager.MessageStore.Count());

            Assert.Equal(0, client1Received);
            Assert.Equal(0, client2Received);
            server.Stop();
        }

        [Fact]
        public async Task MultipleQueue()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new QueueBinding {Name = "qbind-2", Target = "push-a-cc", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
            Assert.Equal(HorseResultCode.Ok, result.Code);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");
            HorseQueue queue2 = server.Rider.Queue.Find("push-a-cc");

            Assert.Equal(1, queue1.Manager.MessageStore.Count());
            Assert.Equal(1, queue2.Manager.MessageStore.Count());
            server.Stop();
        }

        [Fact]
        public async Task MultipleDirect()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-2", Target = "client-2", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            Assert.True(client1.IsConnected);

            HorseClient client2 = new HorseClient();
            client2.ClientId = "client-2";
            await client2.ConnectAsync("horse://localhost:" + port);
            Assert.True(client2.IsConnected);

            bool client1Received = false;
            bool client2Received = false;
            client1.MessageReceived += (c, m) => client1Received = true;
            client2.MessageReceived += (c, m) => client2Received = true;

            HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
            Assert.Equal(HorseResultCode.Ok, result.Code);
            await Task.Delay(500);

            Assert.True(client1Received);
            Assert.True(client2Received);
            server.Stop();
        }

        [Fact]
        public async Task MultipleOfflineDirect()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-2", Target = "client-2", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
            Assert.Equal(HorseResultCode.NotFound, result.Code);
            server.Stop();
        }

        [Fact]
        public async Task SingleQueueSingleDirect()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 5, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            bool client1Received = false;
            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            client1.MessageReceived += (c, m) => client1Received = true;
            Assert.True(client1.IsConnected);

            HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
            Assert.Equal(HorseResultCode.Ok, result.Code);
            await Task.Delay(500);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");

            Assert.Equal(1, queue1.Manager.MessageStore.Count());
            Assert.True(client1Received);
            server.Stop();
        }

        [Fact]
        public async Task MultipleQueueMultipleDirect()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new QueueBinding {Name = "qbind-2", Target = "push-a-cc", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-2", Target = "client-2", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            Assert.True(client1.IsConnected);

            HorseClient client2 = new HorseClient();
            client2.ClientId = "client-2";
            await client2.ConnectAsync("horse://localhost:" + port);
            Assert.True(client2.IsConnected);

            bool client1Received = false;
            bool client2Received = false;
            client1.MessageReceived += (c, m) => client1Received = true;
            client2.MessageReceived += (c, m) => client2Received = true;

            HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
            Assert.Equal(HorseResultCode.Ok, result.Code);
            await Task.Delay(500);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");
            HorseQueue queue2 = server.Rider.Queue.Find("push-a-cc");

            Assert.Equal(1, queue1.Manager.MessageStore.Count());
            Assert.Equal(1, queue2.Manager.MessageStore.Count());

            Assert.True(client1Received);
            Assert.True(client2Received);
            server.Stop();
        }

        [Fact]
        public async Task SingleQueueSingleDirectAckFromQueue()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);
            server.SendAcknowledgeFromMQ = true;

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 0, Interaction = BindingInteraction.Response});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 0, Interaction = BindingInteraction.None});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            bool client1Received = false;
            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            client1.MessageReceived += (c, m) => client1Received = true;
            Assert.True(client1.IsConnected);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");

            HorseResult result = await producer.Router.Publish("router", "Hello, World!", true);
            Assert.Equal(HorseResultCode.Ok, result.Code);

            await Task.Delay(500);
            Assert.Equal(1, queue1.Manager.MessageStore.Count());
            Assert.True(client1Received);
            server.Stop();
        }

        [Fact]
        public async Task SingleQueueSingleDirectResponseFromDirect()
        {
            TestHorseRider server = new TestHorseRider();
            await server.Initialize();
            int port = server.Start(300, 300);

            Router router = new Router(server.Rider, "router", RouteMethod.Distribute);
            router.AddBinding(new QueueBinding {Name = "qbind-1", Target = "push-a", Priority = 0, Interaction = BindingInteraction.None});
            router.AddBinding(new DirectBinding {Name = "dbind-1", Target = "client-1", Priority = 0, Interaction = BindingInteraction.Response});
            server.Rider.Router.Add(router);

            HorseClient producer = new HorseClient();
            await producer.ConnectAsync("horse://localhost:" + port);
            Assert.True(producer.IsConnected);

            HorseClient client1 = new HorseClient();
            client1.ClientId = "client-1";
            await client1.ConnectAsync("horse://localhost:" + port);
            client1.MessageReceived += (c, m) =>
            {
                HorseMessage response = m.CreateResponse(HorseResultCode.Ok);
                response.SetStringContent("Response");
                client1.SendAsync(response);
            };
            Assert.True(client1.IsConnected);

            HorseQueue queue1 = server.Rider.Queue.Find("push-a");

            HorseMessage message = await producer.Router.PublishRequest("router", "Hello, World!");
            Assert.NotNull(message);
            Assert.Equal("Response", message.GetStringContent());
            Assert.Equal(1, queue1.Manager.MessageStore.Count());
            server.Stop();
        }
    }
}