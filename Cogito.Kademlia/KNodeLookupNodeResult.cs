﻿using System;
using System.Collections.Generic;

namespace Cogito.Kademlia
{

    /// <summary>
    /// Represents a result from a lookup operation against a node.
    /// </summary>
    /// <typeparam name="TNodeId"></typeparam>
    public readonly struct KNodeLookupNodeResult<TNodeId>
        where TNodeId : unmanaged
    {

        readonly TNodeId key;
        readonly IEnumerable<KPeerEndpointInfo<TNodeId>> nodes;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="nodes"></param>
        public KNodeLookupNodeResult(in TNodeId key, IEnumerable<KPeerEndpointInfo<TNodeId>> nodes)
        {
            this.key = key;
            this.nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        }

        /// <summary>
        /// Gets the key that resulted in this lookup result.
        /// </summary>
        public TNodeId Key => key;

        /// <summary>
        /// Gets the set of nodes and node endpoints discovered on the way to the key, sorted by distance.
        /// </summary>
        public IEnumerable<KPeerEndpointInfo<TNodeId>> Nodes => nodes;

    }

}