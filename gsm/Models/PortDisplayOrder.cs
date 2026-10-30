namespace gsm.Models;

public static class PortDisplayOrder
{
    public static List<T> Sort<T>(IEnumerable<T> items, IEnumerable<SimPort> ports, Func<T, string?> getPortName)
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var port in ports)
        {
            positions.TryAdd(port.PortName, position++);
        }

        return items.OrderBy(item =>
        {
            var portName = getPortName(item);
            return portName is not null && positions.TryGetValue(portName, out var index)
                ? index
                : int.MaxValue;
        }).ToList();
    }
}
