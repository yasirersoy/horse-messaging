﻿using System.ComponentModel;

namespace Horse.Messaging.Server.Queues.Delivery
{
    /// <summary>
    /// Putting message back to the queue decision
    /// </summary>
    public enum PutBackDecision
    {
        /// <summary>
        /// Message will not keep and put back to the queue
        /// </summary>
        [Description("no")]
        No,
        
        /// <summary>
        /// Message will be put back as priority message.
        /// It will be re-consumed before regular messages.
        /// </summary>
        [Description("priority")]
        Priority,

        /// <summary>
        /// Message will be put back to the end of the queue.
        /// It will be consumed at last.
        /// </summary>
        [Description("regular")]
        Regular
    }
}