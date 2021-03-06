﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace PeerCastStation.Core
{
  public static class IPAddressExtension
  {
    static public AddressFamily GetAddressFamily(this NetworkType type)
    {
      switch (type) {
      case NetworkType.IPv4: return AddressFamily.InterNetwork;
      case NetworkType.IPv6: return AddressFamily.InterNetworkV6;
      default: throw new ArgumentException("Not supported network type", "type");
      }
    }

    static public bool IsIPv6UniqueLocal(this IPAddress addr)
    {
      if (addr.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
      var bytes = addr.GetAddressBytes();
      return bytes[0]==0xfc || bytes[0]==0xfd;
    }

    static public bool IsSiteLocal(this IPAddress addr)
    {
      switch (addr.AddressFamily) {
      case System.Net.Sockets.AddressFamily.InterNetwork:
        var addr_bytes = addr.GetAddressBytes();
        return
          addr_bytes[0] == 10 ||
          addr_bytes[0] == 127 ||
          addr_bytes[0] == 169 && addr_bytes[1] == 254 ||
          addr_bytes[0] == 172 && (addr_bytes[1]&0xF0) == 16 ||
          addr_bytes[0] == 192 && addr_bytes[1] == 168;
      case System.Net.Sockets.AddressFamily.InterNetworkV6:
        return
          addr.IsIPv6LinkLocal ||
          addr.IsIPv6SiteLocal ||
          addr.IsIPv6UniqueLocal() ||
          addr.IsIPv6Teredo ||
          addr.Equals(IPAddress.IPv6Loopback);
      default:
        return false;
      }
    }

    static public int GetAddressLocality(this IPAddress addr)
    {
      switch (addr.AddressFamily) {
      case System.Net.Sockets.AddressFamily.InterNetwork:
        if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.None) || addr.Equals(IPAddress.Broadcast)) return -1;
        if (addr.Equals(IPAddress.Loopback)) return 0;
        if (IsSiteLocal(addr)) return 1;
        return 2;
      case System.Net.Sockets.AddressFamily.InterNetworkV6:
        if (addr.Equals(IPAddress.IPv6Any) || addr.Equals(IPAddress.IPv6None)) return -1;
        if (addr.Equals(IPAddress.IPv6Loopback)) return 0;
        if (IsSiteLocal(addr)) return 1;
        return 2;
      default:
        return -1;
      }
    }

  }
}
