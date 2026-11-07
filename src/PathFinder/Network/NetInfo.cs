using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PathFinder.Network;

public static class NetInfo
{
    static readonly string[] VirtualAdapterMarkers = ["vEthernet", "Hyper-V", "VirtualBox", "VMware", "WSL"];

    /// <summary>Addresses other laptops can use to reach this one: Wi-Fi first, IPv4 before IPv6.</summary>
    public static List<IPAddress> LanAddresses()
    {
        var found = new List<(IPAddress Ip, bool Wifi)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var label = $"{nic.Name} {nic.Description}";
            if (VirtualAdapterMarkers.Any(m => label.Contains(m, StringComparison.OrdinalIgnoreCase)))
                continue;

            var wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                var usable = ip.AddressFamily switch
                {
                    AddressFamily.InterNetwork => !ip.ToString().StartsWith("169.254."),
                    AddressFamily.InterNetworkV6 => !ip.IsIPv6LinkLocal && !ip.IsIPv6Teredo,
                    _ => false,
                };
                if (usable) found.Add((ip, wifi));
            }
        }
        return found
            .OrderByDescending(a => a.Wifi)
            .ThenBy(a => a.Ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(a => a.Ip)
            .ToList();
    }
}
