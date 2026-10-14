using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Walks a range of identifiers with service 0x22 and records every one
    /// that exists. Each hit is written as one complete JSON line and flushed,
    /// so an interrupted scan still leaves a valid file.
    /// </summary>
    public static class DidScan
    {
        public const int MaxSilent = 50;
        public const int ErrorsBeforeRetarget = 25;

        public enum Outcome { Completed, Cancelled, ServiceRefused, Silent, AdapterRefused }

        public sealed record Hit(int Did, UdsResponse Response, DidDef? Known);

        public sealed record Result(Outcome Outcome, int Hits, int LastDid, byte RefusalNrc)
        {
            /// <summary>Where to pick up again after the ECU went silent.</summary>
            public int ResumeFrom => LastDid - MaxSilent + 1;
        }

        public static Result Run(
            Scanner scanner, Catalog catalog, EcuDef ecu, int from, int to, TextWriter output,
            Action<Hit>? hit = null, Action<int>? progress = null, CancellationToken ct = default)
        {
            int hits = 0, errors = 0, silent = 0, did = from;
            if (!TryTarget(scanner, ecu)) return new Result(Outcome.AdapterRefused, hits, did, 0);

            for (; did <= to; did++)
            {
                if (ct.IsCancellationRequested) return new Result(Outcome.Cancelled, hits, did, 0);

                UdsResponse r;
                try { r = scanner.ReadDid(did); }
                // a reset the adapter would not recover from; a refused setup is the adapter's, and goes up
                catch (AdapterException ex) when (ex.Problem != AdapterProblem.SetupRefused) { return new Result(Outcome.AdapterRefused, hits, did, 0); }
                progress?.Invoke(did);

                // These refuse the service itself, so every other identifier
                // would get the same answer. No point sweeping on.
                if (r.Kind == ResponseKind.Negative && (r.Nrc == 0x11 || r.Nrc == 0x7F))
                    return new Result(Outcome.ServiceRefused, hits, did, r.Nrc);

                // A missing identifier still gets an answer (0x31). Silence
                // means the ECU is gone - usually the car went to sleep; a
                // sleeping bus can also answer every read with an adapter error.
                silent = r.Kind is ResponseKind.NoData or ResponseKind.AdapterError ? silent + 1 : 0;
                if (silent >= MaxSilent) return new Result(Outcome.Silent, hits, did, 0);

                // A burst of errors usually means the ECU dropped the session.
                errors = r.Kind == ResponseKind.AdapterError ? errors + 1 : 0;
                if (errors > 0 && errors % ErrorsBeforeRetarget == 0 && !TryTarget(scanner, ecu))
                    return new Result(Outcome.AdapterRefused, hits, did, 0);

                if (!r.Exists) continue;

                hits++;
                var known = catalog.Find(ecu.Id, did);
                output.WriteLine(new JsonObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("O"),
                    ["ecu"] = ecu.Id,
                    ["did"] = $"0x{did:X4}",
                    ["kind"] = r.KindLabel,
                    ["len"] = r.Data.Length,
                    ["hex"] = Bytes.Hex(r.Data),
                    ["ascii"] = Bytes.Ascii(r.Data),
                    ["catalogued"] = known?.EnglishName
                }.ToJsonString());
                output.Flush();
                hit?.Invoke(new Hit(did, r, known));
            }

            return new Result(Outcome.Completed, hits, to, 0);
        }

        /// <summary>False when the adapter refuses the header or filter for this ECU.</summary>
        static bool TryTarget(Scanner scanner, EcuDef ecu)
        {
            try
            {
                scanner.Target(ecu.Request, ecu.Response);
                return true;
            }
            catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
