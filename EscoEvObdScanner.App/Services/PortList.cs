using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.App.Services
{
    public static class PortList
    {
        /// <summary>
        /// COM ports with the device name Windows shows in Device Manager, so
        /// the OBD adapter can be told apart from e.g. a built-in management port.
        /// </summary>
        public static List<PortInfo> Get()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // a slow or stuck WMI service must not hold the window back: plain names after two seconds
                using var searcher = new ManagementObjectSearcher(
                    new ManagementScope(),
                    new ObjectQuery("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"),
                    new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true, Rewindable = false });
                foreach (var o in searcher.Get())
                {
                    var name = o["Name"]?.ToString() ?? "";
                    var m = Regex.Match(name, @"\((COM\d+)\)");
                    if (m.Success) names[m.Groups[1].Value] = name.Replace(m.Value, "").Trim();
                }
            }
            catch
            {
                // WMI unavailable - plain port names still work
            }

            return SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new PortInfo(p, names.TryGetValue(p, out var d) ? d : ""))
                .ToList();
        }
    }
}
