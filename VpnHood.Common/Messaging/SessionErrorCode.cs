﻿using System.Text.Json.Serialization;

namespace VpnHood.Common.Messaging
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SessionErrorCode
    {
        Ok,
        GeneralError,
        SessionClosed,
        SessionSuppressedBy,
        AccessExpired,
        AccessTrafficOverflow,
        Maintenance,
        RedirectHost,
        UnsupportedClient,
        UnsupportedServer
    }
}