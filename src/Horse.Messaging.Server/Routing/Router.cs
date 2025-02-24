using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Horse.Messaging.Protocol;
using Horse.Messaging.Protocol.Events;
using Horse.Messaging.Server.Clients;
using Horse.Messaging.Server.Events;

namespace Horse.Messaging.Server.Routing
{
    /// <summary>
    /// Horse MQ Router object.
    /// A router, routes messages to its' bindings
    /// </summary>
    public class Router : IRouter
    {
        #region Properties

        /// <summary>
        /// The server that router is defined
        /// </summary>
        public HorseRider Rider { get; }

        /// <summary>
        /// Route name.
        /// Must be unique.
        /// Can't include " ", "*" or ";"
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// If true, messages are routed to bindings.
        /// If false, messages are not routed.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Route method. Defines how messages will be routed.
        /// </summary>
        public RouteMethod Method { get; }

        /// <summary>
        /// Bindings for the router
        /// </summary>
        public Binding[] Bindings { get; private set; } = new Binding[0];

        /// <summary>
        /// Used for round robin routing.
        /// The index value of the binding received last message.
        /// </summary>
        private int _lastRoutedIndex = -1;

        /// <summary>
        /// Event Manage for HorseEventType.MessagePublishedToRouter
        /// </summary>
        public EventManager PublishEvent { get; }

        private readonly object _rrlock = new object();

        #endregion

        /// <summary>
        /// Creates new router on the server
        /// </summary>
        public Router(HorseRider rider, string name, RouteMethod method)
        {
            Rider = rider;
            IsEnabled = true;
            Name = name;
            Method = method;
            PublishEvent = new EventManager(rider, HorseEventType.RouterPublish, name);
        }

        #region Add - Remove

        /// <summary>
        /// Returns all bindings of router
        /// </summary>
        public Binding[] GetBindings()
        {
            return Bindings;
        }

        /// <summary>
        /// Adds new binding to router
        /// </summary>
        public bool AddBinding<TBinding>(TBinding binding) where TBinding : Binding, new()
        {
            try
            {
                if (Bindings.Any(x => x.Name.Equals(binding.Name)))
                    return false;

                List<Binding> list = Bindings.ToList();
                list.Add(binding);

                binding.Router = this;
                Bindings = list.OrderByDescending(x => x.Priority).ToArray();
                Rider.Router.BindingAddEvent.Trigger(Name, new KeyValuePair<string, string>("Binding-Name", binding.Name));
                UpdateRouterConfiguration();
                return true;
            }
            catch (Exception e)
            {
                Rider.SendError("ADD_ROUTER_BINDING", e, $"Router:{Name}, Binding:{binding?.Name}");
                return false;
            }
        }

        /// <summary>
        /// Adds new binding.
        /// Binding type must have parameterless constructor.
        /// Otherwise an exception is thrown.
        /// </summary>
        /// <exception cref="InvalidOperationException">Binding type must have parameterless constructor</exception>
        public bool AddBinding(Binding binding)
        {
            Type type = binding.GetType();
            ConstructorInfo[] ctors = type.GetConstructors();
            bool hasParameterlessCtor = ctors.Any(x => x.GetParameters().Length == 0);
            if (!hasParameterlessCtor)
                throw new InvalidOperationException($"Binding type {type.FullName} must have parameterless constructor");

            try
            {
                if (Bindings.Any(x => x.Name.Equals(binding.Name)))
                    return false;

                List<Binding> list = Bindings.ToList();
                list.Add(binding);

                binding.Router = this;
                Bindings = list.OrderByDescending(x => x.Priority).ToArray();
                Rider.Router.BindingAddEvent.Trigger(Name, new KeyValuePair<string, string>("Binding-Name", binding.Name));
                UpdateRouterConfiguration();
                return true;
            }
            catch (Exception e)
            {
                Rider.SendError("ADD_ROUTER_BINDING", e, $"Router:{Name}, Binding:{binding?.Name}");
                return false;
            }
        }

        /// <summary>
        /// Removes a binding from the route
        /// </summary>
        public void RemoveBinding(string bindingName)
        {
            try
            {
                if (!Bindings.Any(x => x.Name.Equals(bindingName)))
                    return;

                List<Binding> list = Bindings.ToList();
                Binding binding = list.FirstOrDefault(x => x.Name == bindingName);
                if (binding == null)
                    return;

                list.Remove(binding);

                binding.Router = null;
                Bindings = list.OrderByDescending(x => x.Priority).ToArray();
                Rider.Router.BindingRemoveEvent.Trigger(Name, new KeyValuePair<string, string>("Binding-Name", binding.Name));
                UpdateRouterConfiguration();
            }
            catch (Exception e)
            {
                Rider.SendError("REMOVE_ROUTER_BINDING", e, $"Router:{Name}, Binding:{bindingName}");
            }
        }

        /// <summary>
        /// Removes a binding from the route
        /// </summary>
        public void RemoveBinding(Binding binding)
        {
            try
            {
                if (!Bindings.Contains(binding))
                    return;

                List<Binding> list = Bindings.ToList();
                if (binding == null)
                    return;

                list.Remove(binding);
                Bindings = list.OrderByDescending(x => x.Priority).ToArray();
                Rider.Router.BindingRemoveEvent.Trigger(Name, new KeyValuePair<string, string>("Binding-Name", binding.Name));
            }
            catch (Exception e)
            {
                Rider.SendError("REMOVE_ROUTER_BINDING", e, $"Router:{Name}, Binding:{binding?.Name}");
            }
        }

        #endregion

        #region Publish

        /// <summary>
        /// Pushes a message to router
        /// </summary>
        public async Task<RouterPublishResult> Publish(MessagingClient sender, HorseMessage message)
        {
            try
            {
                if (!IsEnabled)
                    return RouterPublishResult.Disabled;

                if (Bindings.Length == 0)
                    return RouterPublishResult.NoBindings;

                switch (Method)
                {
                    case RouteMethod.Distribute:
                        return await Distribute(sender, message);

                    case RouteMethod.OnlyFirst:
                        return await OnlyFirst(sender, message);

                    case RouteMethod.RoundRobin:
                        return await RoundRobin(sender, message);

                    default:
                        return RouterPublishResult.Disabled;
                }
            }
            catch (Exception e)
            {
                Rider.SendError("PUBLISH", e, $"Router:{Name}, Binding:{Name}");
                return RouterPublishResult.NoBindings;
            }
        }

        /// <summary>
        /// Sends the message to only first binding
        /// </summary>
        private async Task<RouterPublishResult> OnlyFirst(MessagingClient sender, HorseMessage message)
        {
            int index = 0;
            bool sent;
            RouterPublishResult result = RouterPublishResult.NoReceivers;

            do
            {
                if (index >= Bindings.Length)
                    return RouterPublishResult.NoReceivers;

                Binding binding = Bindings[index];
                sent = await binding.Send(sender, message);

                if (sent)
                    result = binding.Interaction != BindingInteraction.None
                        ? RouterPublishResult.OkAndWillBeRespond
                        : RouterPublishResult.OkWillNotRespond;

                index++;
            } while (!sent);

            return result;
        }

        /// <summary>
        /// Distributes the message to all bindings
        /// </summary>
        private async Task<RouterPublishResult> Distribute(MessagingClient sender, HorseMessage message)
        {
            RouterPublishResult result = RouterPublishResult.NoReceivers;
            foreach (Binding binding in Bindings)
            {
                bool oldWaitResponse = message.WaitResponse;
                bool sent = await binding.Send(sender, message);
                message.WaitResponse = oldWaitResponse;
                if (sent)
                {
                    if (binding.Interaction != BindingInteraction.None)
                        result = RouterPublishResult.OkAndWillBeRespond;

                    else if (result == RouterPublishResult.NoReceivers)
                        result = RouterPublishResult.OkWillNotRespond;
                }
            }

            return result;
        }

        /// <summary>
        /// Sends the message to only one binding within round robin algorithm
        /// </summary>
        private async Task<RouterPublishResult> RoundRobin(MessagingClient sender, HorseMessage message)
        {
            int len = Bindings.Length;
            for (int i = 0; i < len; i++)
            {
                int index;
                lock (_rrlock)
                {
                    _lastRoutedIndex++;
                    if (_lastRoutedIndex >= Bindings.Length)
                        _lastRoutedIndex = 0;

                    index = _lastRoutedIndex;
                }

                Binding binding = Bindings[index];

                bool waitResponse = message.WaitResponse;
                bool sent = await binding.Send(sender, message);
                message.WaitResponse = waitResponse;
                if (sent)
                    return binding.Interaction != BindingInteraction.None
                        ? RouterPublishResult.OkAndWillBeRespond
                        : RouterPublishResult.OkWillNotRespond;
            }

            return RouterPublishResult.NoReceivers;
        }

        #endregion

        private void UpdateRouterConfiguration()
        {
            IOptionsConfigurator<RouterConfiguration> options = Rider.Router.OptionsConfigurator;

            if (options == null)
                return;

            RouterConfiguration configuration = RouterConfiguration.Create(this);

            options.Remove(x => x.Name == Name);
            options.Add(configuration);
            options.Save();
        }
    }
}