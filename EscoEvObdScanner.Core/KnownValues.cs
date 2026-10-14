namespace EscoEvObdScanner
{
    /// <summary>Reads identifiers once and decodes them: every catalogued one, or a chosen list of one ECU.</summary>
    public static class KnownValues
    {
        public sealed record Reading(EcuDef Ecu, DidDef Def, UdsResponse Response)
        {
            public double? Value =>
                Response.Kind == ResponseKind.Positive && !Def.IsAscii ? Expr.Evaluate(Def.Expression, Response.Data) : null;

            public string? Text =>
                Response.Kind == ResponseKind.Positive && Def.IsAscii ? Bytes.Ascii(Response.Data).TrimEnd('.', ' ') : null;

            /// <summary>The expression is a guess or rests on a single report.</summary>
            public bool Uncertain => Def.Confidence is "unverified" or "reported";

            public string Problem => Response.Kind == ResponseKind.Negative
                ? UdsResponse.NrcName(Response.Nrc)
                : Response.KindLabel;
        }

        public static List<Reading> Read(
            Scanner scanner, Catalog catalog, Action<Reading>? each = null, CancellationToken ct = default)
        {
            var readings = new List<Reading>();
            string? currentEcu = null;
            EcuDef? ecu = null;
            int refused = 0;
            bool reached = false;

            foreach (var d in catalog.Dids.OrderBy(x => x.Ecu, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Did))
            {
                if (ct.IsCancellationRequested) break;

                if (!string.Equals(d.Ecu, currentEcu, StringComparison.OrdinalIgnoreCase))
                {
                    ecu = catalog.TryEcu(d.Ecu);
                    currentEcu = d.Ecu;
                    if (ecu != null)
                    {
                        // an ECU the adapter cannot address is skipped, the others are still read
                        try { scanner.Target(ecu.Request, ecu.Response); reached = true; }
                        catch (Exception ex) when (ex is AdapterException { Problem: AdapterProblem.CommandRefused } or ArgumentOutOfRangeException)
                        {
                            ecu = null;
                            // none of the ECUs so far could be addressed: the adapter is gone, not one ECU
                            if (++refused >= Snapshot.MaxUnreachable && !reached)
                                throw new AdapterException(AdapterProblem.NoEcuAddressable,
                                    $"The adapter would not address {refused} ECUs in a row. Disconnect, check the adapter and connect again.", count: refused);
                        }
                    }
                }
                if (ecu == null || d.Did < 0) continue;     // an ECU the adapter would not address, or a malformed identifier

                UdsResponse response;
                // the adapter reset and would not address this ECU again: skip it, as when it could not be addressed at all
                try { response = scanner.ReadDid(d.Did, 1000); }
                catch (AdapterException ex) when (ex.Problem == AdapterProblem.CommandRefused) { ecu = null; continue; }
                var reading = new Reading(ecu, d, response);
                readings.Add(reading);
                each?.Invoke(reading);
            }

            return readings;
        }

        /// <summary>
        /// Reads the given identifiers of one ECU, catalogued or only discovered.
        /// Stops early when the ECU stays silent, as a sleeping one would; each
        /// silent read costs a full timeout.
        /// </summary>
        public static (List<Reading> Readings, bool StoppedSilent) ReadEcu(
            Scanner scanner, EcuDef ecu, IEnumerable<DidDef> dids, Action<Reading>? each = null, CancellationToken ct = default)
        {
            var readings = new List<Reading>();
            int silent = 0;
            scanner.Target(ecu.Request, ecu.Response);
            foreach (var d in dids)
            {
                if (ct.IsCancellationRequested) break;
                if (d.Did < 0) continue;

                var reading = new Reading(ecu, d, scanner.ReadDid(d.Did, 1000));
                readings.Add(reading);
                each?.Invoke(reading);

                silent = reading.Response.Kind == ResponseKind.NoData ? silent + 1 : 0;
                if (silent >= Snapshot.MaxSilent) return (readings, true);
            }
            return (readings, false);
        }
    }
}
