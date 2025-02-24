using System;
using Horse.Messaging.Client.Internal;

namespace Horse.Messaging.Client.Channels
{
    internal class ChannelSubscriberRegistration
    {
        public string Name { get; set; }

        public Type SubscriberType { get; set; }
        public Type MessageType { get; set; }

        internal ExecuterBase Executer { get; set; }
    }
}