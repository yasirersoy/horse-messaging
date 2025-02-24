using System;
using Horse.Messaging.Client.Annotations;
using Horse.Messaging.Protocol;

namespace Horse.Messaging.Client.Queues.Annotations
{
    /// <summary>
    /// Used to add delivery handler key header to message.
    /// It's useful if queue is not exist and will be created with first push,
    /// Server delivery handler builder can use that value
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class QueueManagerAttribute : MessageHeaderAttribute
    {
        /// <summary>
        /// Creates new Delivery Handler Attribute
        /// </summary>
        public QueueManagerAttribute(string value) : base(HorseHeaders.QUEUE_MANAGER, value)
        {
        }
    }
}