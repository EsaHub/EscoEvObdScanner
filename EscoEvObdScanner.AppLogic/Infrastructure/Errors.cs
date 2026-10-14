
namespace EscoEvObdScanner.AppLogic.Infrastructure
{
    /// <summary>
    /// A failure in words for the error bar, in the UI language. The core and
    /// .NET report in English (the command line shows those as they are); the
    /// ones a user meets in the car are put in words here, the rest pass through.
    /// </summary>
    public static class Errors
    {
        public static string Describe(Exception ex) => ex switch
        {
            AdapterException { Problem: AdapterProblem.NotAnswering } a => Loc.I.Format("ErrAdapterNotAnswering", a.Count),
            AdapterException { Problem: AdapterProblem.NoEcuAddressable } a => Loc.I.Format("ErrNoEcuAddressable", a.Count),
            AdapterException { Problem: AdapterProblem.SetupRefused } a => Loc.I.Format("ErrSetupRefused", a.Command ?? ""),
            AdapterException a => Loc.I.Format("ErrAdapterRefused", a.Command ?? ""),
            PortLostException p => Loc.I.Format("ErrPortLost", p.Port),
            // SerialPort.Open: another program (or the command line) has the port
            UnauthorizedAccessException when Port(ex.Message) is string port => Loc.I.Format("ErrPortBusy", port),
            // SerialPort.Open: the adapter is not plugged in, or Windows gave it another number
            IOException when Port(ex.Message) is string port && ex.Message.Contains("does not exist") => Loc.I.Format("ErrPortMissing", port),
            ArgumentException { ParamName: "protocol" } => Loc.I["ErrProtocol"],
            _ => ex.Message
        };

        /// <summary>The COM port named in a .NET serial port message, if any.</summary>
        static string? Port(string message) =>
            System.Text.RegularExpressions.Regex.Match(message, @"COM\d+") is { Success: true } m ? m.Value : null;
    }
}
