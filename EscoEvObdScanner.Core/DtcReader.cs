using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Reads stored faults with service 0x19, plus their freeze frames and
    /// extended data where the ECU offers them.
    ///
    /// Strictly read-only. Service 0x14, which would erase all of this, is not
    /// implemented anywhere in this project.
    /// </summary>
    public static class DtcReader
    {
        public sealed record Fault(Dtc Dtc, string? Snapshot, string? Extended);

        public sealed record EcuFaults(EcuDef Ecu, UdsResponse Response, IReadOnlyList<Fault> Faults)
        {
            public bool Answered => Response.Kind == ResponseKind.Positive;

            public string Problem => Response.Kind == ResponseKind.Negative
                ? UdsResponse.NrcName(Response.Nrc)
                : Response.KindLabel;
        }

        /// <summary>The car's clock (platform.fault_time.clock in the car file) read now, as a reference for dating freeze frames.</summary>
        public static (long Value, DateTime Utc)? ReadClock(Scanner scanner, Catalog catalog)
        {
            if (catalog.Platform.FaultTime?.Clock is not { } clock || clock.Dids is not [int did, ..]
                || catalog.TryEcu(clock.Ecu) is not { } ecu) return null;
            scanner.Target(ecu.Request, ecu.Response);
            var r = scanner.ReadDid(did, 1000);
            return r.Kind == ResponseKind.Positive && r.Data.Length is > 0 and <= 8
                ? (r.Data.Aggregate(0L, (v, b) => v * 256 + b), DateTime.UtcNow) : null;
        }

        public static EcuFaults Read(Scanner scanner, EcuDef ecu, bool deep, CancellationToken ct = default)
        {
            scanner.Target(ecu.Request, ecu.Response);

            var listed = scanner.Request(DtcRequests.List(), 0x19);
            if (listed.Kind != ResponseKind.Positive)
                return new EcuFaults(ecu, listed, Array.Empty<Fault>());

            var faults = new List<Fault>();
            foreach (var d in Dtc.ParseList(listed.Data))
            {
                string? snapshot = null, extended = null;
                // two slow requests per fault: an ECU with many faults must not hold a cancel for minutes
                ct.ThrowIfCancellationRequested();
                if (deep)
                {
                    var snap = scanner.Request(DtcRequests.Snapshot(d.Raw), 0x19);
                    if (snap.Kind == ResponseKind.Positive && snap.Data.Length > 0) snapshot = Bytes.Hex(snap.Data);

                    var ext = scanner.Request(DtcRequests.Extended(d.Raw), 0x19);
                    if (ext.Kind == ResponseKind.Positive && ext.Data.Length > 0) extended = Bytes.Hex(ext.Data);
                }
                faults.Add(new Fault(d, snapshot, extended));
            }

            return new EcuFaults(ecu, listed, faults);
        }

        /// <param name="clock">The car's clock read with this list and when, so freeze-frame clocks can be dated later.</param>
        public static string ToJson(EcuDef ecu, Fault f, (long Value, DateTime Utc)? clock = null)
        {
            var record = new JsonObject
            {
                ["ts"] = DateTime.UtcNow.ToString("O"),
                ["ecu"] = ecu.Id,
                ["dtc"] = f.Dtc.Code,
                ["failure_type"] = $"0x{f.Dtc.FailureType:X2}",
                ["raw"] = f.Dtc.RawHex,
                ["status"] = $"0x{f.Dtc.Status:X2}",
                ["status_text"] = f.Dtc.StatusText,
                ["severity"] = f.Dtc.Severity
            };
            if (f.Snapshot != null) record["snapshot"] = f.Snapshot;
            if (f.Extended != null) record["extended"] = f.Extended;
            if (clock is { } c)
            {
                record["clock_ref"] = c.Value;
                record["clock_ref_ts"] = c.Utc.ToString("O");
            }
            return record.ToJsonString();
        }
    }
}
