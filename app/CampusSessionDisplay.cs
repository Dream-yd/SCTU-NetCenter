namespace NetCenter;

internal sealed record CampusAddress(string Address, bool IsStale);

internal static class CampusSessionDisplay
{
    public static CampusAddress Choose(string cached, IEnumerable<string> currentAddresses)
    {
        var addresses = currentAddresses.Where(address => !string.IsNullOrWhiteSpace(address)).ToArray();
        if (!string.IsNullOrWhiteSpace(cached) && addresses.Contains(cached, StringComparer.Ordinal))
            return new CampusAddress(cached, false);
        return new CampusAddress(addresses.FirstOrDefault() ?? "", !string.IsNullOrWhiteSpace(cached));
    }
}
