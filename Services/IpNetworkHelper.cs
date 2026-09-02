using System.Net;
using System.Net.Sockets;

namespace UnifiedGateway.Services;

/// <summary>
/// High-performance IP and CIDR subnet matching helper for Host Network Trust.
/// Supports IPv4 and IPv6 exact IPs, CIDR blocks, and loopback aliases.
/// </summary>
public static class IpNetworkHelper
{
    public static bool IsIpInAllowedCidrs(IPAddress? clientIp, IEnumerable<string>? allowedCidrs)
    {
        if (allowedCidrs == null) return true;

        var cidrList = allowedCidrs.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (cidrList.Count == 0) return true; // Empty whitelist allows all hosts

        if (clientIp == null) return false;

        // Normalize IPv6-mapped IPv4 (e.g., ::ffff:192.168.1.50 -> 192.168.1.50)
        var normalizedIp = clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp;

        foreach (var cidr in cidrList)
        {
            if (MatchesCidr(normalizedIp, cidr))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesCidr(IPAddress clientIp, string cidrRule)
    {
        if (string.IsNullOrWhiteSpace(cidrRule)) return false;

        var rule = cidrRule.Trim();

        // Handle loopback alias "localhost"
        if (string.Equals(rule, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.IsLoopback(clientIp);
        }

        string ipPart;
        int prefixLength;

        if (rule.Contains('/'))
        {
            var parts = rule.Split('/', 2);
            ipPart = parts[0].Trim();
            if (!int.TryParse(parts[1].Trim(), out prefixLength))
            {
                return false;
            }
        }
        else
        {
            ipPart = rule;
            prefixLength = -1; // Exact IP match
        }

        if (!IPAddress.TryParse(ipPart, out var ruleIp))
        {
            return false;
        }

        var normalizedRuleIp = ruleIp.IsIPv4MappedToIPv6 ? ruleIp.MapToIPv4() : ruleIp;

        // If loopback match check (e.g. 127.0.0.1 or ::1 matches loopback)
        if (IPAddress.IsLoopback(clientIp) && IPAddress.IsLoopback(normalizedRuleIp))
        {
            return true;
        }

        // Family must match (IPv4 vs IPv6)
        if (clientIp.AddressFamily != normalizedRuleIp.AddressFamily)
        {
            return false;
        }

        if (prefixLength == -1)
        {
            return clientIp.Equals(normalizedRuleIp);
        }

        if (clientIp.AddressFamily == AddressFamily.InterNetwork)
        {
            if (prefixLength is < 0 or > 32) return false;
            return MatchesIPv4Cidr(clientIp, normalizedRuleIp, prefixLength);
        }

        if (clientIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (prefixLength is < 0 or > 128) return false;
            return MatchesIPv6Cidr(clientIp, normalizedRuleIp, prefixLength);
        }

        return false;
    }

    private static bool MatchesIPv4Cidr(IPAddress clientIp, IPAddress ruleIp, int prefixLength)
    {
        var clientBytes = clientIp.GetAddressBytes();
        var ruleBytes = ruleIp.GetAddressBytes();

        var clientInt = (uint)((clientBytes[0] << 24) | (clientBytes[1] << 16) | (clientBytes[2] << 8) | clientBytes[3]);
        var ruleInt = (uint)((ruleBytes[0] << 24) | (ruleBytes[1] << 16) | (ruleBytes[2] << 8) | ruleBytes[3]);

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

        return (clientInt & mask) == (ruleInt & mask);
    }

    private static bool MatchesIPv6Cidr(IPAddress clientIp, IPAddress ruleIp, int prefixLength)
    {
        var clientBytes = clientIp.GetAddressBytes();
        var ruleBytes = ruleIp.GetAddressBytes();

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (clientBytes[i] != ruleBytes[i]) return false;
        }

        if (remainingBits > 0 && fullBytes < 16)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((clientBytes[fullBytes] & mask) != (ruleBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }
}
