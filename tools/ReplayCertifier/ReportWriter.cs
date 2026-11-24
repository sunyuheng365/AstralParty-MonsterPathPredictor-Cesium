using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal static class ReportWriter
    {
        public static bool PrintAndExport(
            CertificationSummary s,
            BoardCatalog catalog,
            double elapsedSec,
            double minDetAcc,
            double minThiefAcc,
            double maxAbsZ,
            string jsonOut,
            string mdOut)
        {
            string[] aggroKeys = { "SpawnFork", "ForkStart", "PostCombatFork", "MidWalkFork", "PostNormalCombatMidFork" };
            long aggroDetTotal = aggroKeys.Sum(k => s.Buckets[k].DetTotal);
            long aggroDetHits = aggroKeys.Sum(k => s.Buckets[k].DetHits);
            long aggroProbTotal = aggroKeys.Sum(k => s.Buckets[k].ProbTotal);
            double aggroAcc = aggroDetTotal > 0 ? (double)aggroDetHits / aggroDetTotal : 0.0;
            var (wLow, wHigh) = CertificationSummary.Wilson95(aggroDetHits, aggroDetTotal);

            var thiefB = s.Buckets["ThiefFork"];
            double thiefAcc = thiefB.DetTotal > 0 ? (double)thiefB.DetHits / thiefB.DetTotal : 1.0;

            bool gateAggro = aggroDetTotal > 0 && aggroAcc >= minDetAcc;
            bool gateThief = thiefB.DetTotal == 0 || thiefAcc >= minThiefAcc;
            bool gateCalib = true;
            double worstZ = 0.0;

            var calibRows = new List<object>();
            foreach (var kv in s.ProbBins.OrderByDescending(k => k.Value.Opp))
            {
                var pb = kv.Value;
                if (pb.Opp < 20) continue;
                double pred = pb.SumPred / pb.Opp;
                double obs = (double)pb.Chosen / pb.Opp;
                double se = Math.Sqrt(pred * (1.0 - pred) / pb.Opp);
                double z = se > 0 ? (obs - pred) / se : 0.0;
                if (pb.Opp >= 30 && Math.Abs(z) > worstZ) worstZ = Math.Abs(z);
                if (pb.Opp >= 30 && Math.Abs(z) > maxAbsZ) gateCalib = false;
                calibRows.Add(new { bin = kv.Key, n = pb.Opp, predicted = pred, observed = obs, diff = obs - pred, z });
            }

            bool passed = gateAggro && gateThief && gateCalib;

            Console.WriteLine();
            Console.WriteLine("=========================================================================");
            Console.WriteLine($"REPLAY CERTIFICATION {(passed ? "PASSED" : "FAILED")} | {s.ReplaysVerified:N0} PVE replays in {elapsedSec:F1}s");
            Console.WriteLine($"  Attempted={s.ReplaysAttempted:N0}, Verified={s.ReplaysVerified:N0}, NonPVE={s.SkippedNonPve}, MissingMap={s.MissingMap}, DownloadFail={s.DownloadFailed}, Err={s.ParseErrors}");
            Console.WriteLine("=========================================================================");

            foreach (var b in s.Buckets.Values)
            {
                double acc = b.DetTotal > 0 ? 100.0 * b.DetHits / b.DetTotal : 100.0;
                Console.WriteLine($"  {b.Name,-44} Total={b.Total,7:N0} | Det={b.DetHits,7:N0}/{b.DetTotal,-7:N0} ({acc,7:F3}%) | Prob={b.ProbTotal,5:N0}");
            }
            Console.WriteLine("-------------------------------------------------------------------------");
            Console.WriteLine($"  [GATE 1] Aggressive 100%-Det : {aggroDetHits:N0}/{aggroDetTotal:N0} = {aggroAcc * 100:F4}% (95% Wilson [{wLow * 100:F4}%, {wHigh * 100:F4}%], req >= {minDetAcc * 100:F2}%) => {(gateAggro ? "PASS" : "FAIL")}");
            Console.WriteLine($"  [GATE 2] Thief 100%-Det      : {thiefB.DetHits:N0}/{thiefB.DetTotal:N0} = {thiefAcc * 100:F4}% (req >= {minThiefAcc * 100:F2}%) => {(gateThief ? "PASS" : "FAIL")}");
            Console.WriteLine($"  [GATE 3] Prob Calibration    : {aggroProbTotal:N0} multi-branch forks, max |z|={worstZ:F2} (req <= {maxAbsZ:F1}) => {(gateCalib ? "PASS" : "FAIL")}");
            Console.WriteLine("-------------------------------------------------------------------------");

            Console.WriteLine("\n=== PER-MAP BREAKDOWN ===");
            foreach (var kv in s.ByMap.OrderBy(k => k.Key))
            {
                var ms = kv.Value;
                double acc = ms.DetTotal > 0 ? 100.0 * ms.DetHits / ms.DetTotal : 100.0;
                Console.WriteLine($"  {kv.Key} ({catalog.GetMapName(kv.Key),-8}): replays={ms.Replays,4:N0} | Det={ms.DetHits,7:N0}/{ms.DetTotal,-7:N0} ({acc,7:F3}%) | Prob={ms.ProbTotal,5:N0} | Thief={ms.ThiefDetHits}/{ms.ThiefDetTotal}");
            }

            Console.WriteLine("\n=== PROBABILISTIC BRANCH CALIBRATION (n >= 20) ===");
            foreach (var kv in s.ProbBins.OrderByDescending(k => k.Value.Opp))
            {
                var pb = kv.Value;
                if (pb.Opp < 20) continue;
                double p = pb.SumPred / pb.Opp, obs = (double)pb.Chosen / pb.Opp;
                double z = Math.Sqrt(p * (1 - p) / pb.Opp) is double se && se > 0 ? (obs - p) / se : 0;
                Console.WriteLine($"  Bin {kv.Key,-8}: n={pb.Opp,6:N0} | Pred={p * 100,6:F2}% | Obs={obs * 100,6:F2}% | Diff={(obs - p) * 100:+0.00;-0.00;0.00}% | z={z:+0.00;-0.00;0.00}");
            }

            var fa = s.FrontAudit;
            Console.WriteLine("\n=== FORK-START CURRENT-FACING AUDIT ===");
            Console.WriteLine($"  Spawn     Front==1: {fa.SpawnF1Actual:N0}/{fa.SpawnF1:N0} followed Front[0] (vs {fa.SpawnF1RedBlue:N0} RedBlue)");
            Console.WriteLine($"  Stopped   Front==1: {fa.StopF1Actual:N0}/{fa.StopF1:N0} followed Front[0] (vs {fa.StopF1RedBlue:N0} RedBlue)");
            Console.WriteLine($"  Stopped   Front>=2: {fa.StopMultiRedBlue:N0}/{fa.StopMulti:N0} followed RedBlue within FrontNodeIds");
            Console.WriteLine($"  PostCmbt  Front>=2: {fa.PostCombatMultiRedBlue:N0}/{fa.PostCombatMulti:N0} followed RedBlue within FrontNodeIds");

            Console.WriteLine("\n=== SAMPLE MISMATCHES ===");
            foreach (var b in s.Buckets.Values)
            {
                if (b.Mismatches.Count == 0) continue;
                Console.WriteLine($"--- {b.Name} (mismatches={b.DetTotal - b.DetHits}, showing up to {b.Mismatches.Count}) ---");
                foreach (var m in b.Mismatches.Take(12)) Console.WriteLine("  " + m);
            }

            if (!string.IsNullOrWhiteSpace(jsonOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
                var payload = new
                {
                    generatedAtUtc = DateTime.UtcNow.ToString("O"),
                    passed,
                    elapsedSeconds = Math.Round(elapsedSec, 2),
                    replays = new { s.ReplaysAttempted, s.ReplaysVerified, s.SkippedNonPve, s.MissingMap, s.DownloadFailed, s.ParseErrors },
                    gates = new { gateAggro, gateThief, gateCalib, minDetAcc, minThiefAcc, maxAbsZ, worstAbsZ = worstZ },
                    aggressiveDeterministic = new { hits = aggroDetHits, total = aggroDetTotal, accuracy = aggroAcc, wilson95Low = wLow, wilson95High = wHigh },
                    thiefDeterministic = new { hits = thiefB.DetHits, total = thiefB.DetTotal, accuracy = thiefAcc },
                    buckets = s.Buckets.Values.Select(b => new { b.Key, b.Name, b.Total, b.DetHits, b.DetTotal, b.ProbTotal, mismatchCount = b.DetTotal - b.DetHits, samples = b.Mismatches.Take(15) }),
                    byMap = s.ByMap.OrderBy(k => k.Key).Select(k => new { mapId = k.Key, mapName = catalog.GetMapName(k.Key), k.Value.Replays, k.Value.DetHits, k.Value.DetTotal, k.Value.ProbTotal, k.Value.ThiefDetHits, k.Value.ThiefDetTotal }),
                    calibration = calibRows,
                    frontAudit = fa
                };
                File.WriteAllText(jsonOut, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                Console.WriteLine($"\nWrote JSON report: {jsonOut}");
            }

            if (!string.IsNullOrWhiteSpace(mdOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mdOut))!);
                var sb = new StringBuilder();
                sb.AppendLine($"# Replay Certification Report ({(passed ? "PASSED" : "FAILED")})");
                sb.AppendLine();
                sb.AppendLine($"- **Verified PVE Replays**: {s.ReplaysVerified:N0} ({s.ByMap.Count} maps, {elapsedSec:F1}s)");
                sb.AppendLine($"- **Aggressive 100%-Deterministic Accuracy**: **{aggroDetHits:N0} / {aggroDetTotal:N0} = {aggroAcc * 100:F4}%** (95% Wilson `[{wLow * 100:F4}%, {wHigh * 100:F4}%]`)");
                sb.AppendLine($"- **Thief 100%-Deterministic Accuracy**: **{thiefB.DetHits:N0} / {thiefB.DetTotal:N0} = {thiefAcc * 100:F4}%**");
                sb.AppendLine($"- **Probabilistic Multi-Branch Calibration**: {aggroProbTotal:N0} forks, max `|z| = {worstZ:F2}`");
                sb.AppendLine();
                sb.AppendLine("| Bucket | Total | Det Hits / Total | Accuracy | Multi-Branch |");
                sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
                foreach (var b in s.Buckets.Values)
                {
                    double acc = b.DetTotal > 0 ? 100.0 * b.DetHits / b.DetTotal : 100.0;
                    sb.AppendLine($"| {b.Name} | {b.Total:N0} | {b.DetHits:N0} / {b.DetTotal:N0} | {acc:F4}% | {b.ProbTotal:N0} |");
                }
                sb.AppendLine();
                sb.AppendLine("| Map ID | Map Name | Replays | Det Hits / Total | Accuracy | Multi-Branch |");
                sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
                foreach (var kv in s.ByMap.OrderBy(k => k.Key))
                {
                    var ms = kv.Value;
                    double acc = ms.DetTotal > 0 ? 100.0 * ms.DetHits / ms.DetTotal : 100.0;
                    sb.AppendLine($"| {kv.Key} | {catalog.GetMapName(kv.Key)} | {ms.Replays:N0} | {ms.DetHits:N0} / {ms.DetTotal:N0} | {acc:F4}% | {ms.ProbTotal:N0} |");
                }
                File.WriteAllText(mdOut, sb.ToString(), Encoding.UTF8);
                Console.WriteLine($"Wrote Markdown report: {mdOut}");
            }

            return passed;
        }
    }
}
