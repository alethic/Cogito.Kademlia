﻿namespace Cogito.Kademlia
{

    /// <summary>
    /// Describes a peer and its associated endpoints.
    /// </summary>
    /// <typeparam name="TKNodeId"></typeparam>
    public struct KPeerEndpointInfo<TKNodeId>
        where TKNodeId : unmanaged
    {

        readonly TKNodeId id;
        readonly IKEndpointSet<TKNodeId> endpoints;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="endpoints"></param>
        public KPeerEndpointInfo(in TKNodeId id, IKEndpointSet<TKNodeId> endpoints)
        {
            this.id = id;
            this.endpoints = endpoints;
        }

        /// <summary>
        /// Gets the node ID of the peer.
        /// </summary>
        public TKNodeId Id => id;

        /// <summary>
        /// Gets the set of known endpoints of the peer.
        /// </summary>
        public IKEndpointSet<TKNodeId> Endpoints => endpoints;

    }

}
