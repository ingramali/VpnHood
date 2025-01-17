﻿using System.Text.Json.Serialization;
using VpnHood.Common.Messaging;

namespace VpnHood.Tunneling.Messaging
{
    public class UdpChannelResponse : ResponseBase
    {
        [JsonConstructor]
        public UdpChannelResponse(SessionErrorCode errorCode)
            : base(errorCode)
        {
        }

        public UdpChannelResponse(ResponseBase obj)
            : base(obj)
        {
        }

        public int UdpPort { get; set; }
        public byte[] UdpKey { get; set; } = null!;
    }
}