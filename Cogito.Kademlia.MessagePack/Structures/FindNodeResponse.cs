﻿using MessagePack;

namespace Cogito.Kademlia.MessagePack.Structures
{

    [MessagePackObject]
    public class FindNodeResponse : ResponseBody
    {

        [Key(8)]
        public Peer[] Peers { get; set; }

    }

}