using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KeyBridge.Services;

public static class NetworkBroadcast
{
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address)
            .Where(address => !IPAddress.IsLoopback(address))
            .Distinct()
            .ToList();
    }

    public static IReadOnlyList<IPAddress> GetIPv4BroadcastAddresses()
    {
        var addresses = new Dictionary<string, IPAddress>
        {
            [IPAddress.Broadcast.ToString()] = IPAddress.Broadcast
        };

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null ||
                    IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                var broadcast = GetBroadcastAddress(unicast.Address, unicast.IPv4Mask);
                addresses[broadcast.ToString()] = broadcast;
            }
        }

        return addresses.Values.ToList();
    }

    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        var broadcastBytes = new byte[addressBytes.Length];

        for (var i = 0; i < broadcastBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }
}
