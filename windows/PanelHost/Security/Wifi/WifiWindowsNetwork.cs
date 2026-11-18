using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;

namespace PanelHost.Security.Wifi;

// Read-only facts about the interface that owns one IPv4 address, exactly as the bench listener will judge it.
internal sealed record WifiNetworkFacts(bool Assigned, string InterfaceId, string InterfaceType, string Status, int PrefixLength,
    bool Physical, string Category, bool Private)
{
    public static WifiNetworkFacts NotAssigned { get; } = new(false, "", "", "", 0, false, "", false);

    public bool TypeAllowed => InterfaceType is nameof(NetworkInterfaceType.Ethernet) or nameof(NetworkInterfaceType.Wireless80211);
    public bool Up => Status == nameof(OperationalStatus.Up);

    // First reason the bench would refuse to start on this address, or null when it would accept it.
    public string? WhyNotReady() =>
        !Assigned ? "the address is not assigned to any interface on this PC"
        : !TypeAllowed ? $"the interface type is {InterfaceType}; only Ethernet and Wi-Fi are allowed"
        : !Up ? $"the interface is {Status}, not Up"
        : !Physical ? "Windows does not report the adapter as physical"
        : !Private ? $"the network profile is {Category}, not Private (change it in Windows yourself if that is right; the bench never does)"
        : PrefixLength is < 8 or > 30 ? $"the prefix length /{PrefixLength} is outside the supported 8..30"
        : null;
}

internal static class WifiWindowsNetwork
{
    // Both facts are supplied by Windows for this exact interface index, never inferred from its name.
    public static WifiNetworkFacts Inspect(IPAddress address)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            var unicast = properties.UnicastAddresses.SingleOrDefault(ip => ip.Address.Equals(address));
            if (unicast is null) continue;
            var index = properties.GetIPv4Properties().Index.ToString(CultureInfo.InvariantCulture);
            var physical = ReadSingleBoolean(@"root\cimv2", "Win32_NetworkAdapter", index, "PhysicalAdapter", true);
            var category = ReadSingleValue(@"root\StandardCimv2", "MSFT_NetConnectionProfile", index, "NetworkCategory");
            return new WifiNetworkFacts(true, nic.Id, nic.NetworkInterfaceType.ToString(), nic.OperationalStatus.ToString(),
                unicast.PrefixLength, physical, CategoryName(category), category is 1u);
        }
        return WifiNetworkFacts.NotAssigned;
    }

    public static WifiLanBinding Capture(IPAddress address)
    {
        var facts = Inspect(address);
        if (!facts.Assigned) throw new InvalidOperationException("WiFi bind address is not assigned to a permitted interface.");
        var binding = new WifiLanBinding(facts.InterfaceId, address, facts.PrefixLength, facts.Physical, facts.Private,
            facts.Up && facts.TypeAllowed);
        binding.Validate();
        return binding;
    }

    private static string CategoryName(object? category) => category switch
    {
        0u => "Public",
        1u => "Private",
        2u => "DomainAuthenticated",
        _ => "unknown",
    };

    private static bool ReadSingleBoolean(string scope, string table, string index, string field, object expected) =>
        Equals(ReadSingleValue(scope, table, index, field), expected);

    // The value only when Windows returns exactly one row for the interface index; null on none or on ambiguity.
    private static object? ReadSingleValue(string scope, string table, string index, string field)
    {
        using var search = new ManagementObjectSearcher(new ManagementScope(scope),
            new ObjectQuery($"SELECT {field} FROM {table} WHERE InterfaceIndex={index}"),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2) });
        using var results = search.Get();
        var count = 0; object? value = null;
        foreach (ManagementObject item in results)
            using (item) { count++; value = item[field]; }
        return count == 1 ? value : null;
    }
}
