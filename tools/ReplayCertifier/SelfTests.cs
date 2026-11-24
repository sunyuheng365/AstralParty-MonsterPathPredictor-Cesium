using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    public static class SelfTests
    {
        private sealed class StubDecoder : IProtocolDecoder
        {
            private readonly Dictionary<int, object> _byToken = new();
            public int Register(object obj) { int tok = _byToken.Count + 1; _byToken[tok] = obj; return tok; }
            public dynamic TryDecode(string typeName, byte[] payload)
            {
                if (payload == null || payload.Length < 4) return null;
                int tok = BinaryPrimitives.ReadInt32BigEndian(payload);
                return _byToken.TryGetValue(tok, out var obj) ? obj : null;
            }
        }

        public sealed class StubHero
        {
            public int HeroId { get; set; }
            public int MonsterType { get; set; }
            public int NodeId { get; set; }
            public int BackNodeId { get; set; } = -1;
            public int[] FrontNodeIds { get; set; } = Array.Empty<int>();
            public int Hp { get; set; } = 10;
            public int Gold { get; set; } = 10;
            public int CrimeNum { get; set; }
            public bool NotSelect { get; set; }
            public Dictionary<int, object> Buffs { get; set; }
        }

        public sealed class StubUnit { public long Id { get; set; } public int Slot { get; set; } public int ChangeSlot { get; set; } = -1; public StubHero Hero { get; set; } }
        public sealed class StubRoom { public int MapId { get; set; } = 82016; public int MapIndex { get; set; } public int Round { get; set; } = 1; public List<StubUnit> Players { get; set; } = new(); public List<StubUnit> Monsters { get; set; } = new(); }
        public sealed class StubRunning { public StubRoom Room { get; set; } }
        public sealed class StubFinish { public int Winner { get; set; } public string ReplayId { get; set; } }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<string, HttpResponseMessage> _responder;
            public StubHttpHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(_responder(request.RequestUri!.ToString()));
        }
        public sealed class StubThrowDice { public long PlayerId { get; set; } }
        public sealed class StubMove { public long PlayerId { get; set; } public int[] NodeIds { get; set; } }
        public sealed class StubGold { public int ChangeGold { get; set; } public int CurrGold { get; set; } }
        public sealed class StubPlaceInner { public int NodeId { get; set; } public int BackNodeId { get; set; } = -1; public int[] FrontNodeIds { get; set; } = Array.Empty<int>(); }
        public sealed class StubPlace { public StubPlaceInner Place { get; set; } }
        public sealed class StubNotSelect { public bool NotSelect { get; set; } }
        public sealed class StubEffect { public long PlayerId { get; set; } public object Hp { get; set; } public StubGold Gold { get; set; } public object CrimeNum { get; set; } public StubNotSelect NotSelect { get; set; } public object Disappear { get; set; } public object Buff { get; set; } public StubPlace Place { get; set; } }
        public sealed class StubCause { public long Id { get; set; } }
        public sealed class StubBuffSource { public int Id { get; set; } }
        public sealed class StubBuffInfo { public int BuffId { get; set; } public int Progress { get; set; } public StubBuffSource Source { get; set; } }
        public sealed class StubSnap { public long PlayerId { get; set; } public StubRoom Room { get; set; } }
        public sealed class StubAttr { public long PlayerId { get; set; } public StubCause Cause { get; set; } public List<StubEffect> EffectDatas { get; set; } = new(); }
        public sealed class StubMoveEffectBucket { public List<StubAttr> Effects { get; set; } = new(); }
        public sealed class StubSkillMoveEffect { public long PlayerId { get; set; } public Dictionary<int, StubMoveEffectBucket> EffectData { get; set; } = new(); }

        private static byte[] MakeFrame(short cmdId, int token)
        {
            var buf = new byte[10];
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(0, 2), cmdId);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(2, 4), 4);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(6, 4), token);
            return buf;
        }

        private static byte[] ConcatFrames(params byte[][] frames) => frames.SelectMany(f => f).ToArray();

        private static BoardCatalog BuildTestCatalog(int mapId, string nodesJson)
        {
            var catalog = new BoardCatalog();
            string tmpMap = Path.Combine(Path.GetTempPath(), "certifier-st-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmpMap, $"{{\"id\":{mapId},\"name\":\"Test\",\"nodes\":[{nodesJson}]}}");
                catalog.LoadJsonFile(tmpMap);
            }
            finally { try { if (File.Exists(tmpMap)) File.Delete(tmpMap); } catch { } }
            return catalog;
        }

        private static void PopulatePassingBaseline(CertificationSummary s)
        {
            s.Buckets["MidWalkFork"].Total = 200;
            s.Buckets["MidWalkFork"].DetTotal = 200;
            s.Buckets["MidWalkFork"].DetHits = 200;
            s.Buckets["ThiefFork"].Total = 20;
            s.Buckets["ThiefFork"].DetTotal = 20;
            s.Buckets["ThiefFork"].DetHits = 20;
        }

        public static int RunAll()
        {
            var t = new GateThresholds();

            // Test 1: 1/1 aggro, 0/0 thief, 0 bins -> MUST FAIL with InsufficientSample
            var emptyLike = new CertificationSummary { ReplaysAttempted = 1, ReplaysVerified = 1 };
            emptyLike.Buckets["MidWalkFork"].Total = 1;
            emptyLike.Buckets["MidWalkFork"].DetTotal = 1;
            emptyLike.Buckets["MidWalkFork"].DetHits = 1;
            var e1 = ReportWriter.EvaluateGates(emptyLike, t);
            if (e1.Passed || e1.AggroGate != GateStatus.InsufficientSample ||
                e1.ThiefGate != GateStatus.InsufficientSample || e1.CalibrationGate != GateStatus.InsufficientSample)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 1): empty-sample summary falsely passed.");
                return 1;
            }

            // Test 2: MissingPrediction > 0 -> CoverageGate MUST FAIL
            var missingPred = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10, MissingPredictions = 1 };
            PopulatePassingBaseline(missingPred);
            missingPred.ProbBins["50%"] = new ProbBin { Opp = 40, SumPred = 20.0, Chosen = 20 };
            var e2 = ReportWriter.EvaluateGates(missingPred, t);
            if (e2.Passed || e2.CoverageGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 2): MissingPredictions did not fail coverage gate.");
                return 1;
            }

            // Test 3: 50/50 directional Red bias MUST FAIL
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(23, new[] { 22, 55, 24 }),
                new LandNodeSnapshot(22, new[] { 23 }),
                new LandNodeSnapshot(55, new[] { 23 }),
                new LandNodeSnapshot(24, new[] { 23 })
            });
            var exits = new List<ForkExitPrediction>
            {
                new() { NextLandId = 22, ConditionalProbability = 0.5 },
                new() { NextLandId = 55, ConditionalProbability = 0.5 }
            };
            var biased = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(biased);
            for (int i = 0; i < 40; i++) ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, actualNext: 22, biased.ProbBins);
            var e3 = ReportWriter.EvaluateGates(biased, t);
            if (e3.Passed || e3.CalibrationGate != GateStatus.Fail || biased.ProbBins["50%"].Opp != 40 || biased.ProbBins["50%"].Chosen != 40)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 3): 50/50 Red bias went undetected.");
                return 1;
            }

            // Test 4: FrontAuditStats JSON serialization non-empty
            var fa = new FrontAuditStats { SpawnF1 = 5, SpawnF1Actual = 5, StopMulti = 3, StopMultiRedBlue = 3 };
            string json = System.Text.Json.JsonSerializer.Serialize(fa);
            if (!json.Contains("\"SpawnF1\":5", StringComparison.Ordinal) || !json.Contains("\"StopMultiRedBlue\":3", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 4): FrontAuditStats serialized empty: " + json);
                return 1;
            }

            // Test 5: BoardCatalog out-of-bounds mapIndex MUST return false
            var cat5 = BuildTestCatalog(82016, "{\"id\":1,\"neighbors\":[2]},{\"id\":2,\"neighbors\":[1]}");
            if (!cat5.TryGetGraph(82016, 0, out _) || cat5.TryGetGraph(82016, 1, out _) || cat5.TryGetGraph(82016, -1, out _))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 5): BoardCatalog out-of-bounds mapIndex fell back.");
                return 1;
            }

            // Test 6: Out-of-support 50/50 MUST fail GATE 0
            var oos = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(oos);
            for (int i = 0; i < 20; i++)
            {
                if (!ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, actualNext: 22, oos.ProbBins)) oos.OutOfSupportPredictions++;
                if (!ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, actualNext: 24, oos.ProbBins)) oos.OutOfSupportPredictions++;
            }
            var e6 = ReportWriter.EvaluateGates(oos, t);
            if (e6.Passed || e6.CoverageGate != GateStatus.Fail || oos.OutOfSupportPredictions != 20)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 6): Out-of-support 50/50 went undetected.");
                return 1;
            }

            // Test 7: 3-way symmetric 30/60/0 bias MUST fail GATE 3
            var board3 = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2, 3, 4, 5 }),
                new LandNodeSnapshot(2, new[] { 1 }),
                new LandNodeSnapshot(3, new[] { 1 }),
                new LandNodeSnapshot(4, new[] { 1 }),
                new LandNodeSnapshot(5, new[] { 1 })
            });
            var exits3 = new List<ForkExitPrediction>
            {
                new() { NextLandId = 2, ConditionalProbability = 1.0 / 3.0 },
                new() { NextLandId = 3, ConditionalProbability = 1.0 / 3.0 },
                new() { NextLandId = 4, ConditionalProbability = 1.0 / 3.0 }
            };
            var biased3 = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(biased3);
            for (int i = 0; i < 30; i++) ReplayEvaluator.RecordMultiBranchCalibration(board3, 1, exits3, actualNext: 2, biased3.ProbBins);
            for (int i = 0; i < 60; i++) ReplayEvaluator.RecordMultiBranchCalibration(board3, 1, exits3, actualNext: 3, biased3.ProbBins);
            var e7 = ReportWriter.EvaluateGates(biased3, t);
            if (e7.Passed || e7.CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 7): 3-way 30/60/0 bias went undetected.");
                return 1;
            }

            // Test 8: Nested & flat cache discovery + nested backfill path resolution
            string tmpCache = Path.Combine(Path.GetTempPath(), "certifier-cache-" + Guid.NewGuid().ToString("N"));
            try
            {
                string nestedDir = Path.Combine(tmpCache, "1788434495879811");
                string nestedFile = Path.Combine(nestedDir, "1788434495879811");
                Directory.CreateDirectory(nestedDir);
                File.WriteAllBytes(nestedFile, new byte[1200]);
                File.WriteAllBytes(Path.Combine(tmpCache, "1788434495879812"), new byte[1200]);
                var discovered = Program.DiscoverCachedReplayIds(tmpCache);
                string resolvedNested = Program.ResolveCachedReplayPath(tmpCache, "1788434495879811");
                string saveNested = Program.ResolveSaveCachePath(tmpCache, "1788434495879811", resolvedNested);
                string saveDirOnly = Program.ResolveSaveCachePath(tmpCache, "1788434495879811", null);
                if (!discovered.Contains("1788434495879811") || !discovered.Contains("1788434495879812") ||
                    resolvedNested != nestedFile || saveNested != nestedFile || saveDirOnly != nestedFile)
                {
                    Console.Error.WriteLine("SELF-TEST FAILED (Test 8): Nested/flat cache discovery or backfill path failed.");
                    return 1;
                }
            }
            finally { try { if (Directory.Exists(tmpCache)) Directory.Delete(tmpCache, true); } catch { } }

            // Test 9: Zero-denominator accuracy formats as "N/A" and exports JSON null (not 0.0)
            if (ReportWriter.FormatAccuracy(0, 0) != "N/A" || ReportWriter.FormatAccuracy(1, 1, 3) != "100.000%")
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 9a): FormatAccuracy mismatch.");
                return 1;
            }
            string tmpJson = Path.Combine(Path.GetTempPath(), "certifier-zero-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ReportWriter.PrintAndExport(new CertificationSummary(), cat5, 0.1, t, tmpJson, null);
                string exported = File.ReadAllText(tmpJson);
                using var doc = System.Text.Json.JsonDocument.Parse(exported);
                if (doc.RootElement.GetProperty("aggressiveDeterministic").GetProperty("accuracy").ValueKind != System.Text.Json.JsonValueKind.Null ||
                    doc.RootElement.GetProperty("thiefDeterministic").GetProperty("accuracy").ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    Console.Error.WriteLine("SELF-TEST FAILED (Test 9b): Zero-denominator JSON accuracy was not null.");
                    return 1;
                }
            }
            finally { try { if (File.Exists(tmpJson)) File.Delete(tmpJson); } catch { } }

            // Test 10: IllegalMoveEdges > 0 AND inconsistent Bucket Total MUST fail GATE 0
            var illegal = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10, IllegalMoveEdges = 1 };
            PopulatePassingBaseline(illegal);
            illegal.ProbBins["50%"] = new ProbBin { Opp = 40, SumPred = 20.0, Chosen = 20 };
            if (ReportWriter.EvaluateGates(illegal, t).CoverageGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 10a): IllegalMoveEdges did not fail GATE 0.");
                return 1;
            }
            var badSum = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(badSum);
            badSum.Buckets["ThiefFork"].Total = 25; // DetTotal=20 + ProbTotal=0 != 25
            badSum.ProbBins["50%"] = new ProbBin { Opp = 40, SumPred = 20.0, Chosen = 20 };
            if (ReportWriter.EvaluateGates(badSum, t).CoverageGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 10b): Bucket Total != DetTotal + ProbTotal did not fail GATE 0.");
                return 1;
            }

            // Test 11: Production EvaluateBytes stream lifecycle enforcement ([1003,1016] ok; [1016,1003], [1003,1016,1003], [1003] fail)
            var forkCatalog = BuildTestCatalog(82016,
                "{\"id\":1,\"neighbors\":[2]},{\"id\":2,\"neighbors\":[3,4,1]},{\"id\":3,\"neighbors\":[2]},{\"id\":4,\"neighbors\":[2]},{\"id\":5,\"neighbors\":[]}");
            var dec11 = new StubDecoder();
            int tokRun11 = dec11.Register(new StubRunning { Room = new StubRoom { MapId = 82016, MapIndex = 0, Round = 1 } });
            int tokFin11 = dec11.Register(new StubFinish());
            var ok11 = ReplayEvaluator.EvaluateBytes("ok", ConcatFrames(MakeFrame(1003, tokRun11), MakeFrame(1016, tokFin11)), dec11, forkCatalog);
            var rev11 = ReplayEvaluator.EvaluateBytes("rev", ConcatFrames(MakeFrame(1016, tokFin11), MakeFrame(1003, tokRun11)), dec11, forkCatalog);
            var dup11 = ReplayEvaluator.EvaluateBytes("dup", ConcatFrames(MakeFrame(1003, tokRun11), MakeFrame(1016, tokFin11), MakeFrame(1003, tokRun11)), dec11, forkCatalog);
            var trunc11 = ReplayEvaluator.EvaluateBytes("trunc", MakeFrame(1003, tokRun11), dec11, forkCatalog);
            if (ok11.ReplaysVerified != 1 || ok11.TruncatedOrCorrupt != 0 ||
                rev11.ReplaysVerified != 0 || rev11.TruncatedOrCorrupt != 1 ||
                dup11.ReplaysVerified != 0 || dup11.TruncatedOrCorrupt != 1 ||
                trunc11.ReplaysVerified != 0 || trunc11.TruncatedOrCorrupt != 1)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 11): Production EvaluateBytes lifecycle ordering check failed.");
                return 1;
            }

            // Test 12: Multi-subgroup equal-prob exits (1/3,1/3,1/6,1/6) with 40/60/20/0 AND 32/32/16/40 MUST fail GATE 3
            var board4 = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2, 3, 4, 5, 6 }),
                new LandNodeSnapshot(2, new[] { 1 }),
                new LandNodeSnapshot(3, new[] { 1 }),
                new LandNodeSnapshot(4, new[] { 1 }),
                new LandNodeSnapshot(5, new[] { 1 }),
                new LandNodeSnapshot(6, new[] { 1 })
            });
            var exits4 = new List<ForkExitPrediction>
            {
                new() { NextLandId = 2, ConditionalProbability = 1.0 / 3.0 },
                new() { NextLandId = 3, ConditionalProbability = 1.0 / 3.0 },
                new() { NextLandId = 4, ConditionalProbability = 1.0 / 6.0 },
                new() { NextLandId = 5, ConditionalProbability = 1.0 / 6.0 }
            };
            var subBiasA = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(subBiasA);
            for (int i = 0; i < 40; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 2, subBiasA.ProbBins);
            for (int i = 0; i < 60; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 3, subBiasA.ProbBins);
            for (int i = 0; i < 20; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 4, subBiasA.ProbBins);
            if (ReportWriter.EvaluateGates(subBiasA, t).CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 12a): 1/3,1/3,1/6,1/6 (40/60/20/0) subgroup bias went undetected.");
                return 1;
            }
            var subBiasB = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(subBiasB);
            for (int i = 0; i < 32; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 2, subBiasB.ProbBins);
            for (int i = 0; i < 32; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 3, subBiasB.ProbBins);
            for (int i = 0; i < 16; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 4, subBiasB.ProbBins);
            for (int i = 0; i < 40; i++) ReplayEvaluator.RecordMultiBranchCalibration(board4, 1, exits4, 5, subBiasB.ProbBins);
            if (ReportWriter.EvaluateGates(subBiasB, t).CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 12b): 1/3,1/3,1/6,1/6 (32/32/16/40) last-exit bias went undetected.");
                return 1;
            }

            // Test 13: Thief multi-branch out-of-support move (predicts 3/4 50/50, actually U-turns to 1) MUST fail GATE 0
            var dec13 = new StubDecoder();
            int tokRun13 = dec13.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Monsters = new List<StubUnit> { new StubUnit { Id = 100, Hero = new StubHero { HeroId = 1001, MonsterType = 1, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                }
            });
            int tokRoll13 = dec13.Register(new StubThrowDice { PlayerId = 100 });
            int tokMove13 = dec13.Register(new StubMove { PlayerId = 100, NodeIds = new[] { 1, 2, 1 } });
            int tokFin13 = dec13.Register(new StubFinish());
            var res13 = ReplayEvaluator.EvaluateBytes("thief-oos",
                ConcatFrames(MakeFrame(1003, tokRun13), MakeFrame(5022, tokRoll13), MakeFrame(5028, tokMove13), MakeFrame(1016, tokFin13)),
                dec13, forkCatalog);
            if (res13.OutOfSupportPredictions != 1 || res13.Buckets["ThiefFork"].ProbTotal != 1 || res13.Buckets["ThiefFork"].Total != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 13): Thief out-of-support missed (oos={res13.OutOfSupportPredictions}, prob={res13.Buckets["ThiefFork"].ProbTotal}).");
                return 1;
            }

            // Test 14: End-to-end EvaluateBytes protocol state invariants:
            //   14a: Unreached 1096 effect on tile 3 must be discarded on next 5022 (never drain player gold).
            //   14b: Off-turn monster 5028 must NOT mutate active monster's stepped/startedOnFork state.
            //   14c: Normal 5028 [2, 5] after 1096.Place 1->2 MUST NOT swallow illegal edge 2->5.
            //   14d: Player illegal non-adjacent 5028 (1 -> 5) MUST increment IllegalMoveEdges.
            var dec14 = new StubDecoder();
            int tokRun14 = dec14.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 4, Gold = 10 } } },
                    Monsters = new List<StubUnit>
                    {
                        new StubUnit { Id = 200, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 3 } } },
                        new StubUnit { Id = 201, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } }
                    }
                }
            });
            int tokRollP1 = dec14.Register(new StubThrowDice { PlayerId = 1 });
            int tokMoveP1Illegal = dec14.Register(new StubMove { PlayerId = 1, NodeIds = new[] { 4, 5 } }); // 4->5 non-neighbor
            int tokRoll200 = dec14.Register(new StubThrowDice { PlayerId = 200 });
            // Off-turn monster 201 displaced 1->2 via 1096.Place, then illegally jumps 2->5 on 5028; MUST NOT clobber 200's ForkStart!
            int tokPlace201 = dec14.Register(new StubSkillMoveEffect
            {
                PlayerId = 201,
                EffectData = new Dictionary<int, StubMoveEffectBucket>
                {
                    [1] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = 1, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 201, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 3 } } } } } } } },
                    [3] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = 201, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 1, Gold = new StubGold { ChangeGold = -10, CurrGold = 0 } } } } } }
                }
            });
            int tokMove201Illegal = dec14.Register(new StubMove { PlayerId = 201, NodeIds = new[] { 2, 5 } }); // illegal after landing
            int tokMove200Start = dec14.Register(new StubMove { PlayerId = 200, NodeIds = new[] { 2, 3 } }); // ForkStart Front=[3] -> 3
            int tokFin14 = dec14.Register(new StubFinish());
            var res14 = ReplayEvaluator.EvaluateBytes("state-inv",
                ConcatFrames(
                    MakeFrame(1003, tokRun14),
                    MakeFrame(5022, tokRollP1), MakeFrame(5028, tokMoveP1Illegal),
                    MakeFrame(5022, tokRoll200), MakeFrame(1096, tokPlace201), MakeFrame(5028, tokMove201Illegal), MakeFrame(5028, tokMove200Start),
                    MakeFrame(1016, tokFin14)),
                dec14, forkCatalog);
            if (res14.IllegalMoveEdges != 2 || res14.Buckets["SpawnFork"].DetHits != 1 || res14.Buckets["SpawnFork"].DetTotal != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 14): state invariant failed (illegal={res14.IllegalMoveEdges}, spawnFork={res14.Buckets["SpawnFork"].DetHits}/{res14.Buckets["SpawnFork"].DetTotal}, midWalk={res14.Buckets["MidWalkFork"].Total}).");
                return 1;
            }

            // Test 15: Trailing --corpus without value MUST fail TryParseArgs instead of silently falling back to --cache-dir
            if (Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c", "--corpus" }, out _, out _) ||
                Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--corpus", "--cache-dir", "c" }, out _, out _) ||
                !Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c" }, out _, out _))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 15): CLI option validation failed for missing --corpus value.");
                return 1;
            }

            // Test 16: Unreached 1096 effect discarded on next 5022 (must NOT mark P1 NotSelect=true and erase deterministic chase)
            var dec16 = new StubDecoder();
            int tokRun16 = dec16.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 3, Gold = 10 } } },
                    Monsters = new List<StubUnit> { new StubUnit { Id = 300, Hero = new StubHero { HeroId = 1001, MonsterType = 1, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                }
            });
            int tokRollP16 = dec16.Register(new StubThrowDice { PlayerId = 1 });
            // Pending effect on unreached tile 4 would set P1 NotSelect=true if wrongly flushed on next 5022
            int tokUnreached16 = dec16.Register(new StubSkillMoveEffect
            {
                PlayerId = 1,
                EffectData = new Dictionary<int, StubMoveEffectBucket>
                {
                    [4] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = 1, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 1, NotSelect = new StubNotSelect { NotSelect = true } } } } } }
                }
            });
            int tokRoll300 = dec16.Register(new StubThrowDice { PlayerId = 300 });
            int tokMove300 = dec16.Register(new StubMove { PlayerId = 300, NodeIds = new[] { 1, 2, 3 } }); // Chases targetable P1 to tile 3
            int tokFin16 = dec16.Register(new StubFinish());
            var res16 = ReplayEvaluator.EvaluateBytes("unreached-1096",
                ConcatFrames(MakeFrame(1003, tokRun16), MakeFrame(5022, tokRollP16), MakeFrame(1096, tokUnreached16), MakeFrame(5022, tokRoll300), MakeFrame(5028, tokMove300), MakeFrame(1016, tokFin16)),
                dec16, forkCatalog);
            if (res16.Buckets["ThiefFork"].DetHits != 1 || res16.Buckets["ThiefFork"].DetTotal != 1 || res16.Buckets["ThiefFork"].ProbTotal != 0 || res16.ReplaysVerified != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 16): Unreached 1096 effect was wrongly applied (det={res16.Buckets["ThiefFork"].DetHits}/{res16.Buckets["ThiefFork"].DetTotal}, prob={res16.Buckets["ThiefFork"].ProbTotal}).");
                return 1;
            }

            // Test 17: Sherry (30601) 1040.Place onto 3-neighbor fork with Front=[Red(3)] -> 5022 -> 5028 moving Blue(4)
            //          MUST pass SkillMoveKind.ShirleyThrow to MonsterPathEngine (3-way multi-branch, 0 Det mismatch, 0 OOS).
            var dec17 = new StubDecoder();
            int tokRun17 = dec17.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 306, NodeId = 1 } } },
                    Monsters = new List<StubUnit> { new StubUnit { Id = 400, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                }
            });
            int tokPlace17 = dec17.Register(new StubAttr
            {
                PlayerId = 1,
                Cause = new StubCause { Id = 30601 },
                EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 400, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 3 } } } } }
            });
            int tokRoll17 = dec17.Register(new StubThrowDice { PlayerId = 400 });
            int tokMove17 = dec17.Register(new StubMove { PlayerId = 400, NodeIds = new[] { 2, 4 } });
            int tokFin17 = dec17.Register(new StubFinish());
            var res17 = ReplayEvaluator.EvaluateBytes("sherry-1040",
                ConcatFrames(MakeFrame(1003, tokRun17), MakeFrame(1040, tokPlace17), MakeFrame(5022, tokRoll17), MakeFrame(5028, tokMove17), MakeFrame(1016, tokFin17)),
                dec17, forkCatalog);
            if (res17.ReplaysVerified != 1 || res17.Buckets["ForkStart"].ProbTotal != 1 || res17.Buckets["ForkStart"].DetTotal != 0 || res17.OutOfSupportPredictions != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 17): Sherry SkillMoveKind not propagated (prob={res17.Buckets["ForkStart"].ProbTotal}, det={res17.Buckets["ForkStart"].DetTotal}, oos={res17.OutOfSupportPredictions}).");
                return 1;
            }

            // Test 18: Off-turn 1096 deferred effect for actor B queued during actor A's turn MUST be discarded on 5022(B)
            var dec18 = new StubDecoder();
            int tokRun18 = dec18.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 3 } } },
                    Monsters = new List<StubUnit> { new StubUnit { Id = 410, Hero = new StubHero { HeroId = 1001, MonsterType = 1, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                }
            });
            int tokRollA18 = dec18.Register(new StubThrowDice { PlayerId = 1 });
            int tokOffTurn1096 = dec18.Register(new StubSkillMoveEffect
            {
                PlayerId = 410,
                EffectData = new Dictionary<int, StubMoveEffectBucket>
                {
                    [2] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = 410, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 1, NotSelect = new StubNotSelect { NotSelect = true } } } } } }
                }
            });
            int tokRollB18 = dec18.Register(new StubThrowDice { PlayerId = 410 });
            int tokMoveB18 = dec18.Register(new StubMove { PlayerId = 410, NodeIds = new[] { 1, 2, 3 } });
            int tokFin18 = dec18.Register(new StubFinish());
            var res18 = ReplayEvaluator.EvaluateBytes("offturn-1096",
                ConcatFrames(MakeFrame(1003, tokRun18), MakeFrame(5022, tokRollA18), MakeFrame(1096, tokOffTurn1096), MakeFrame(5022, tokRollB18), MakeFrame(5028, tokMoveB18), MakeFrame(1016, tokFin18)),
                dec18, forkCatalog);
            if (res18.Buckets["ThiefFork"].DetHits != 1 || res18.Buckets["ThiefFork"].DetTotal != 1 || res18.Buckets["ThiefFork"].ProbTotal != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 18): Off-turn 1096 effect bled across 5022(B) (det={res18.Buckets["ThiefFork"].DetTotal}, prob={res18.Buckets["ThiefFork"].ProbTotal}).");
                return 1;
            }

            // Test 19: (a) 1040.Place(1->4) + merged 5028[1,4,2] (non-adjacent echo + legal 4->2) -> 0 illegal edges;
            //          (b) Player 1096.Place(1->4) + 1113 snapshot at 4 + echo 5028[1,4] -> 0 illegal edges;
            //          (c) Friendly MonsterType=30 illegal edge 1->5 -> IllegalMoveEdges == 1!
            var dec19 = new StubDecoder();
            int tokRun19 = dec19.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 1 } } },
                    Monsters = new List<StubUnit>
                    {
                        new StubUnit { Id = 420, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1 } },
                        new StubUnit { Id = 421, Hero = new StubHero { HeroId = 1099, MonsterType = 30, NodeId = 1 } }
                    }
                }
            });
            int tokPlaceM19 = dec19.Register(new StubAttr { PlayerId = 1, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 420, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 4, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } } } });
            int tokMergedM19 = dec19.Register(new StubMove { PlayerId = 420, NodeIds = new[] { 1, 4, 2 } });
            int tokPlaceP19 = dec19.Register(new StubSkillMoveEffect
            {
                PlayerId = 1,
                EffectData = new Dictionary<int, StubMoveEffectBucket>
                {
                    [1] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = 1, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 1, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 4, BackNodeId = -1 } } } } } } }
                }
            });
            int tokSnap19 = dec19.Register(new StubSnap
            {
                PlayerId = 1,
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 4 } } },
                    Monsters = new List<StubUnit>
                    {
                        new StubUnit { Id = 420, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 2 } },
                        new StubUnit { Id = 421, Hero = new StubHero { HeroId = 1099, MonsterType = 30, NodeId = 1 } }
                    }
                }
            });
            int tokEchoP19 = dec19.Register(new StubMove { PlayerId = 1, NodeIds = new[] { 1, 4 } });
            int tokAllyIllegal19 = dec19.Register(new StubMove { PlayerId = 421, NodeIds = new[] { 1, 5 } });
            int tokFin19 = dec19.Register(new StubFinish());
            var res19 = ReplayEvaluator.EvaluateBytes("disp-echo-ally",
                ConcatFrames(MakeFrame(1003, tokRun19), MakeFrame(1040, tokPlaceM19), MakeFrame(5028, tokMergedM19),
                    MakeFrame(1096, tokPlaceP19), MakeFrame(1113, tokSnap19), MakeFrame(5028, tokEchoP19), MakeFrame(5028, tokAllyIllegal19), MakeFrame(1016, tokFin19)),
                dec19, forkCatalog);
            if (res19.ReplaysVerified != 1 || res19.IllegalMoveEdges != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 19): Expected exactly 1 illegal edge (ally 1->5), got illegal={res19.IllegalMoveEdges}, verified={res19.ReplaysVerified}.");
                return 1;
            }

            // Test 20: Buff 10013.Progress=1 with Hero.CrimeNum=0 MUST make Guard(1068) chase P1 deterministically onto Blue(4)
            var dec20 = new StubDecoder();
            int tokRun20 = dec20.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit>
                    {
                        new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 4, CrimeNum = 0, Buffs = new Dictionary<int, object> { [1] = new StubBuffInfo { BuffId = 10013, Progress = 1 } } } }
                    },
                    Monsters = new List<StubUnit> { new StubUnit { Id = 430, Hero = new StubHero { HeroId = 1068, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                }
            });
            int tokRoll20 = dec20.Register(new StubThrowDice { PlayerId = 430 });
            int tokMove20 = dec20.Register(new StubMove { PlayerId = 430, NodeIds = new[] { 1, 2, 4 } });
            int tokFin20 = dec20.Register(new StubFinish());
            var res20 = ReplayEvaluator.EvaluateBytes("crime-buff",
                ConcatFrames(MakeFrame(1003, tokRun20), MakeFrame(5022, tokRoll20), MakeFrame(5028, tokMove20), MakeFrame(1016, tokFin20)),
                dec20, forkCatalog);
            if (res20.Buckets["MidWalkFork"].DetHits != 1 || res20.Buckets["MidWalkFork"].DetTotal != 1 || res20.Buckets["MidWalkFork"].ProbTotal != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 20): Buff 10013.Progress crime tracking failed (det={res20.Buckets["MidWalkFork"].DetTotal}, prob={res20.Buckets["MidWalkFork"].ProbTotal}).");
                return 1;
            }

            // Test 21: (a) [1/2, 1/4, 1/4] MUST NOT mix into "50%" bin and 0.5004/0.4996 MUST NOT merge into "50%";
            //          (b) Out-of-support 30 A / 30 B / 40 C MUST NOT censor calibration denominator (Opp=100, Chosen=30, GATE 3 FAIL).
            var binMix = new Dictionary<string, ProbBin>();
            ReplayEvaluator.RecordMultiBranchCalibration(board3, 1, new List<ForkExitPrediction>
            {
                new() { NextLandId = 2, ConditionalProbability = 0.5 },
                new() { NextLandId = 3, ConditionalProbability = 0.25 },
                new() { NextLandId = 4, ConditionalProbability = 0.25 }
            }, actualNext: 2, binMix);
            ReplayEvaluator.RecordMultiBranchCalibration(board, 23, new List<ForkExitPrediction>
            {
                new() { NextLandId = 22, ConditionalProbability = 0.5004 },
                new() { NextLandId = 55, ConditionalProbability = 0.4996 }
            }, actualNext: 22, binMix);
            if (binMix.ContainsKey("50%") || !binMix.ContainsKey("50%(k=3)") || !binMix.ContainsKey("50.04%") || !binMix.ContainsKey("49.96%"))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 21a): Non-2-way or near-50% exits polluted dedicated 50% bin: " + string.Join(",", binMix.Keys));
                return 1;
            }
            var uncensored = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(uncensored);
            for (int i = 0; i < 30; i++) ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, 22, uncensored.ProbBins);
            for (int i = 0; i < 30; i++) ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, 55, uncensored.ProbBins);
            for (int i = 0; i < 40; i++) if (!ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, 24, uncensored.ProbBins)) uncensored.OutOfSupportPredictions++;
            var e21 = ReportWriter.EvaluateGates(uncensored, t);
            if (uncensored.ProbBins["50%"].Opp != 100 || uncensored.ProbBins["50%"].Chosen != 30 || e21.CalibrationGate != GateStatus.Fail || e21.CoverageGate != GateStatus.Fail)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 21b): Out-of-support censored calibration (opp={uncensored.ProbBins["50%"].Opp}, chosen={uncensored.ProbBins["50%"].Chosen}, calibGate={e21.CalibrationGate}).");
                return 1;
            }

            // Test 22: Truncated replay prefix hits MUST NOT leak into verified corpus stats on merge
            var dec22 = new StubDecoder();
            int tokRun22 = dec22.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 3 } } },
                    Monsters = new List<StubUnit> { new StubUnit { Id = 440, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                }
            });
            int tokRoll22 = dec22.Register(new StubThrowDice { PlayerId = 440 });
            int tokMove22 = dec22.Register(new StubMove { PlayerId = 440, NodeIds = new[] { 1, 2, 3 } });
            var truncWithHit = ReplayEvaluator.EvaluateBytes("trunc-hit",
                ConcatFrames(MakeFrame(1003, tokRun22), MakeFrame(5022, tokRoll22), MakeFrame(5028, tokMove22)), // Missing 1016!
                dec22, forkCatalog);
            var merged22 = new CertificationSummary();
            merged22.MergeFrom(ok11);
            merged22.MergeFrom(truncWithHit);
            if (merged22.ReplaysVerified != 1 || merged22.TruncatedOrCorrupt != 1 || merged22.Buckets["MidWalkFork"].DetTotal != 0 || merged22.Buckets["MidWalkFork"].DetHits != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 22): Truncated replay leaked prefix hits (verified={merged22.ReplaysVerified}, det={merged22.Buckets["MidWalkFork"].DetTotal}).");
                return 1;
            }

            // Test 23: Non-finite CLI thresholds (--max-abs-z NaN/Infinity, out-of-range rates) AND Int32.MaxValue frame length overflow
            if (Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c", "--max-abs-z", "NaN" }, out _, out _) ||
                Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c", "--max-abs-z", "Infinity" }, out _, out _) ||
                Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c", "--min-det-accuracy", "1.5" }, out _, out _))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 23a): Non-finite or out-of-range CLI threshold was accepted.");
                return 1;
            }
            var nanGate = ReportWriter.EvaluateGates(biased, new GateThresholds { MaxAbsZ = double.NaN });
            if (nanGate.Passed || nanGate.CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 23b): NaN MaxAbsZ bypassed calibration gate.");
                return 1;
            }
            var overflowFrame = new byte[10];
            BinaryPrimitives.WriteInt16BigEndian(overflowFrame.AsSpan(0, 2), 1003);
            BinaryPrimitives.WriteInt32BigEndian(overflowFrame.AsSpan(2, 4), int.MaxValue);
            var overflowRes = ReplayEvaluator.EvaluateBytes("overflow", overflowFrame, dec11, forkCatalog);
            if (overflowRes.TruncatedOrCorrupt != 1 || overflowRes.ParseErrors != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 23c): Int32.MaxValue frame length misclassified (corrupt={overflowRes.TruncatedOrCorrupt}, err={overflowRes.ParseErrors}).");
                return 1;
            }

            // Test 24 (Round 5 #1): Mismatched 1016.ReplayId fails as TruncatedOrCorrupt, and identical content under
            //          two different requested IDs is deduplicated (never double-counted) in DeduplicateAndMerge.
            var dec24 = new StubDecoder();
            int tokRun24 = dec24.Register(new StubRunning { Room = new StubRoom { MapId = 82016 } });
            int tokFinMatch24 = dec24.Register(new StubFinish { ReplayId = "1788434495879801" });
            int tokFinNoId24 = dec24.Register(new StubFinish());
            byte[] replayWithId24 = ConcatFrames(MakeFrame(1003, tokRun24), MakeFrame(1016, tokFinMatch24));
            var r24Ok = ReplayEvaluator.EvaluateBytes("1788434495879801", replayWithId24, dec24, forkCatalog);
            var r24Spoof = ReplayEvaluator.EvaluateBytes("1788434495879802", replayWithId24, dec24, forkCatalog);
            if (r24Ok.ReplaysVerified != 1 || r24Spoof.ReplaysVerified != 0 || r24Spoof.TruncatedOrCorrupt != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 24a): Mismatched 1016.ReplayId was not rejected (ok={r24Ok.ReplaysVerified}, spoofCorrupt={r24Spoof.TruncatedOrCorrupt}).");
                return 1;
            }
            byte[] legacyNoId24 = ConcatFrames(MakeFrame(1003, tokRun24), MakeFrame(1016, tokFinNoId24));
            var r24DupA = ReplayEvaluator.EvaluateBytes("1788434495879803", legacyNoId24, dec24, forkCatalog);
            var r24DupB = ReplayEvaluator.EvaluateBytes("1788434495879804", legacyNoId24, dec24, forkCatalog);
            var merged24 = Program.DeduplicateAndMerge(new[] { r24DupA, r24DupB });
            if (merged24.ReplaysVerified != 1 || merged24.TruncatedOrCorrupt != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 24b): Duplicate legacy replay was double-counted (verified={merged24.ReplaysVerified}, corrupt={merged24.TruncatedOrCorrupt}).");
                return 1;
            }

            // Test 25 (Round 5 #2): 0.50004 / 0.49996 repeated 30x always picking exit 1 MUST NOT merge into "50.00%" (z=0);
            //          precision must escalate to 3 decimals ("50.004%" / "49.996%") and FAIL GATE 3!
            var tightExits = new List<ForkExitPrediction>
            {
                new() { NextLandId = 22, ConditionalProbability = 0.50004 },
                new() { NextLandId = 55, ConditionalProbability = 0.49996 }
            };
            var tightBias = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(tightBias);
            for (int i = 0; i < 30; i++) ReplayEvaluator.RecordMultiBranchCalibration(board, 23, tightExits, actualNext: 22, tightBias.ProbBins);
            var e25 = ReportWriter.EvaluateGates(tightBias, t);
            if (!tightBias.ProbBins.ContainsKey("50.004%") || !tightBias.ProbBins.ContainsKey("49.996%") ||
                tightBias.ProbBins.ContainsKey("50.00%") || e25.CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 25): 0.50004/0.49996 collided and hid bias: " + string.Join(",", tightBias.ProbBins.Keys));
                return 1;
            }

            // Test 26 (Round 5 #3 & #4):
            //   26a: HTTP 200 7-byte corrupt body from all CDNs sets ReceivedCorruptPayload=true (NOT DownloadFailed).
            //   26b: HTTP 200 1001-byte corrupt body on primary CDN falls back to valid backup CDN.
            //   26c: Truncated stream with missing map MUST return TruncatedOrCorrupt=1 (NOT MissingMap=1).
            byte[] validSmall = ConcatFrames(MakeFrame(1003, tokRun11), MakeFrame(1016, tokFin11));
            using (var c26a = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[7]) })))
            {
                var (b26a, corrupt26a) = Program.DownloadReplayFromUrlsAsync(c26a, new[] { "http://cdn1/1", "http://cdn2/1" },
                    b => ReplayEvaluator.HasValidFrameStream(b, dec11), null, CancellationToken.None).GetAwaiter().GetResult();
                if (b26a != null || !corrupt26a)
                {
                    Console.Error.WriteLine($"SELF-TEST FAILED (Test 26a): 7-byte HTTP 200 corrupt body did not set ReceivedCorruptPayload (corrupt={corrupt26a}).");
                    return 1;
                }
            }
            using (var c26b = new HttpClient(new StubHttpHandler(url => url.Contains("cdn1")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1001]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validSmall) })))
            {
                var (b26b, corrupt26b) = Program.DownloadReplayFromUrlsAsync(c26b, new[] { "http://cdn1/1", "http://cdn2/1" },
                    b => ReplayEvaluator.HasValidFrameStream(b, dec11), null, CancellationToken.None).GetAwaiter().GetResult();
                if (b26b == null || corrupt26b)
                {
                    Console.Error.WriteLine("SELF-TEST FAILED (Test 26b): 1001-byte corrupt primary did not fall back to valid backup CDN.");
                    return 1;
                }
            }
            int tokUnknownMap26 = dec11.Register(new StubRunning { Room = new StubRoom { MapId = 82999, MapIndex = 0 } });
            byte[] truncUnknownMap = MakeFrame(1003, tokUnknownMap26).Concat(new byte[7]).ToArray();
            var res26c = ReplayEvaluator.EvaluateBytes("trunc-miss", truncUnknownMap, dec11, forkCatalog);
            if (res26c.TruncatedOrCorrupt != 1 || res26c.MissingMap != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 26c): Truncated missing-map replay misclassified (corrupt={res26c.TruncatedOrCorrupt}, missing={res26c.MissingMap}).");
                return 1;
            }

            // Test 27 (Round 5 #5): Unknown mover 5028 [1, 5] (non-adjacent) MUST flag IllegalMoveEdges=1 AND TruncatedOrCorrupt=1
            var dec27 = new StubDecoder();
            int tokRun27 = dec27.Register(new StubRunning { Room = new StubRoom { MapId = 82016 } });
            int tokMoveUnk27 = dec27.Register(new StubMove { PlayerId = 999999, NodeIds = new[] { 1, 5 } });
            int tokFin27 = dec27.Register(new StubFinish());
            var res27 = ReplayEvaluator.EvaluateBytes("unk-mover",
                ConcatFrames(MakeFrame(1003, tokRun27), MakeFrame(5028, tokMoveUnk27), MakeFrame(1016, tokFin27)),
                dec27, forkCatalog);
            if (res27.IllegalMoveEdges != 1 || res27.TruncatedOrCorrupt != 1 || res27.ReplaysVerified != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 27): Unknown mover illegal move bypassed checks (illegal={res27.IllegalMoveEdges}, corrupt={res27.TruncatedOrCorrupt}, verified={res27.ReplaysVerified}).");
                return 1;
            }

            // Test 28 (Round 5 #6): Split [1,2]+[2,3,4] and merged [1,2,3,4] after 1096 deferred NotSelect on landing tile 2
            //          MUST yield identical multi-branch 50/50 predictions at Fork 3 (not 100% -> 4 on merged frame).
            var cat28 = BuildTestCatalog(82016,
                "{\"id\":1,\"neighbors\":[2]},{\"id\":2,\"neighbors\":[3,1]},{\"id\":3,\"neighbors\":[4,5,2]},{\"id\":4,\"neighbors\":[3]},{\"id\":5,\"neighbors\":[3]}");
            CertificationSummary RunCase28(bool mergedMove)
            {
                var d = new StubDecoder();
                int r = d.Register(new StubRunning
                {
                    Room = new StubRoom
                    {
                        MapId = 82016,
                        Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 4 } } },
                        Monsters = new List<StubUnit> { new StubUnit { Id = 500, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } } }
                    }
                });
                int roll = d.Register(new StubThrowDice { PlayerId = 500 });
                int sm = d.Register(new StubSkillMoveEffect
                {
                    PlayerId = 500,
                    EffectData = new Dictionary<int, StubMoveEffectBucket>
                    {
                        [2] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = 500, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 1, NotSelect = new StubNotSelect { NotSelect = true } } } } } }
                    }
                });
                int pl = d.Register(new StubAttr
                {
                    PlayerId = 1,
                    EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 500, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 3 } } } } }
                });
                int fin = d.Register(new StubFinish());
                if (mergedMove)
                {
                    int mv = d.Register(new StubMove { PlayerId = 500, NodeIds = new[] { 1, 2, 3, 4 } });
                    return ReplayEvaluator.EvaluateBytes("m28", ConcatFrames(MakeFrame(1003, r), MakeFrame(5022, roll), MakeFrame(1096, sm), MakeFrame(1040, pl), MakeFrame(5028, mv), MakeFrame(1016, fin)), d, cat28);
                }
                int mv1 = d.Register(new StubMove { PlayerId = 500, NodeIds = new[] { 1, 2 } });
                int mv2 = d.Register(new StubMove { PlayerId = 500, NodeIds = new[] { 2, 3, 4 } });
                return ReplayEvaluator.EvaluateBytes("s28", ConcatFrames(MakeFrame(1003, r), MakeFrame(5022, roll), MakeFrame(1096, sm), MakeFrame(1040, pl), MakeFrame(5028, mv1), MakeFrame(5028, mv2), MakeFrame(1016, fin)), d, cat28);
            }
            var split28 = RunCase28(false);
            var merged28 = RunCase28(true);
            if (split28.Buckets["MidWalkFork"].ProbTotal != 1 || merged28.Buckets["MidWalkFork"].ProbTotal != 1 ||
                split28.Buckets["MidWalkFork"].DetTotal != 0 || merged28.Buckets["MidWalkFork"].DetTotal != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 28): Merged vs split 1096 landing effect diverged (splitProb={split28.Buckets["MidWalkFork"].ProbTotal}, mergedProb={merged28.Buckets["MidWalkFork"].ProbTotal}).");
                return 1;
            }

            // Test 29 (Round 5 #7 & #8):
            //   29a: Duplicate same-tile Lingling (12801) 1040.Place MUST preserve PushFrom=3 so monster exits to 4 (not back to 3).
            //   29b: Normal Place changing tile 1->2 with FrontNodeIds=[] MUST NOT inherit stale tile-1 Front=[4]; must follow Red(3).
            var dec29 = new StubDecoder();
            int tokRun29 = dec29.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 128, NodeId = 1 } } },
                    Monsters = new List<StubUnit>
                    {
                        new StubUnit { Id = 510, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 3, BackNodeId = -1, FrontNodeIds = new[] { 2 } } },
                        new StubUnit { Id = 511, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 4 } } }
                    }
                }
            });
            int tokLing1 = dec29.Register(new StubAttr
            {
                PlayerId = 1, Cause = new StubCause { Id = 12801 },
                EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 510, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 3, FrontNodeIds = Array.Empty<int>() } } } }
            });
            int tokLingDup = dec29.Register(new StubAttr
            {
                PlayerId = 1, Cause = new StubCause { Id = 12801 },
                EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 510, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 3, FrontNodeIds = Array.Empty<int>() } } } }
            });
            int tokRoll510 = dec29.Register(new StubThrowDice { PlayerId = 510 });
            int tokMove510 = dec29.Register(new StubMove { PlayerId = 510, NodeIds = new[] { 2, 4 } }); // Must predict 4 (not 3)!
            int tokEmptyFrontPlace = dec29.Register(new StubAttr
            {
                PlayerId = 1,
                EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 511, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 1, FrontNodeIds = Array.Empty<int>() } } } }
            });
            int tokRoll511 = dec29.Register(new StubThrowDice { PlayerId = 511 });
            int tokMove511 = dec29.Register(new StubMove { PlayerId = 511, NodeIds = new[] { 2, 3 } }); // Must predict Red(3), NOT stale 4!
            int tokFin29 = dec29.Register(new StubFinish());
            var res29 = ReplayEvaluator.EvaluateBytes("ling-front",
                ConcatFrames(MakeFrame(1003, tokRun29), MakeFrame(1040, tokLing1), MakeFrame(1040, tokLingDup),
                    MakeFrame(5022, tokRoll510), MakeFrame(5028, tokMove510),
                    MakeFrame(1040, tokEmptyFrontPlace), MakeFrame(5022, tokRoll511), MakeFrame(5028, tokMove511), MakeFrame(1016, tokFin29)),
                dec29, forkCatalog);
            if (res29.Buckets["ForkStart"].DetHits != 2 || res29.Buckets["ForkStart"].DetTotal != 2)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 29): Duplicate Lingling Place or empty FrontNodeIds failed (hits={res29.Buckets["ForkStart"].DetHits}/{res29.Buckets["ForkStart"].DetTotal}).");
                return 1;
            }

            // Test 30 (Round 5 #12, #13, #14, #15):
            //   30a: Negative MinCalibratedBins=-1 fails EvaluateGates even with empty ProbBins (#12).
            //   30b: Bucket OutOfSupport exports consistent outOfSupportCount=1 & mismatchCount=1 in JSON (#13).
            //   30c: Same --json-out & --md-out path AND --save-cache without --cache-dir rejected by TryParseArgs (#14, #15).
            var negGate = ReportWriter.EvaluateGates(new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 },
                new GateThresholds { MinCalibratedBins = -1 });
            if (negGate.Passed || negGate.CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 30a): Negative MinCalibratedBins bypassed EvaluateGates.");
                return 1;
            }
            string tmpJson30 = Path.Combine(Path.GetTempPath(), "certifier-oos-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ReportWriter.PrintAndExport(res13, forkCatalog, 0.1, t, tmpJson30, null);
                using var doc30 = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tmpJson30));
                var thiefElem = doc30.RootElement.GetProperty("buckets").EnumerateArray().First(b => b.GetProperty("Key").GetString() == "ThiefFork");
                if (thiefElem.GetProperty("outOfSupportCount").GetInt64() != 1 || thiefElem.GetProperty("mismatchCount").GetInt64() != 1)
                {
                    Console.Error.WriteLine("SELF-TEST FAILED (Test 30b): Out-of-support JSON mismatchCount/outOfSupportCount inconsistent.");
                    return 1;
                }
            }
            finally { try { if (File.Exists(tmpJson30)) File.Delete(tmpJson30); } catch { } }
            if (Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--corpus", "ids.txt", "--save-cache" }, out _, out _) ||
                Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c", "--json-out", "rep.out", "--md-out", "./rep.out" }, out _, out _))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 30c): --save-cache without --cache-dir or colliding output paths was accepted.");
                return 1;
            }

            // Test 31 (Round 6 #1): 1096.Place moving the mover itself (1-2, 3-4, Place 2->3):
            //          split [1,2]+[2,3,4] and merged [1,2,3,4] MUST both produce IllegalMoveEdges=0 for BOTH player and monster!
            var cat31 = BuildTestCatalog(82016,
                "{\"id\":1,\"neighbors\":[2]},{\"id\":2,\"neighbors\":[1]},{\"id\":3,\"neighbors\":[4]},{\"id\":4,\"neighbors\":[3]}");
            int RunSelfTeleport31(bool isMonster, bool merged)
            {
                var d = new StubDecoder();
                long actor = isMonster ? 600 : 1;
                var room = new StubRoom { MapId = 82016 };
                if (isMonster) room.Monsters.Add(new StubUnit { Id = 600, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1 } });
                else room.Players.Add(new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 1 } });
                int r = d.Register(new StubRunning { Room = room });
                int roll = d.Register(new StubThrowDice { PlayerId = actor });
                int sm = d.Register(new StubSkillMoveEffect
                {
                    PlayerId = actor,
                    EffectData = new Dictionary<int, StubMoveEffectBucket>
                    {
                        [2] = new StubMoveEffectBucket { Effects = new List<StubAttr> { new StubAttr { PlayerId = actor, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = actor, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 3, BackNodeId = -1 } } } } } } }
                    }
                });
                int fin = d.Register(new StubFinish());
                if (merged)
                {
                    int mv = d.Register(new StubMove { PlayerId = actor, NodeIds = new[] { 1, 2, 3, 4 } });
                    var res = ReplayEvaluator.EvaluateBytes("m31", ConcatFrames(MakeFrame(1003, r), MakeFrame(5022, roll), MakeFrame(1096, sm), MakeFrame(5028, mv), MakeFrame(1016, fin)), d, cat31);
                    return (int)res.IllegalMoveEdges;
                }
                int mv1 = d.Register(new StubMove { PlayerId = actor, NodeIds = new[] { 1, 2 } });
                int mv2 = d.Register(new StubMove { PlayerId = actor, NodeIds = new[] { 2, 3, 4 } });
                var resS = ReplayEvaluator.EvaluateBytes("s31", ConcatFrames(MakeFrame(1003, r), MakeFrame(5022, roll), MakeFrame(1096, sm), MakeFrame(5028, mv1), MakeFrame(5028, mv2), MakeFrame(1016, fin)), d, cat31);
                return (int)resS.IllegalMoveEdges;
            }
            if (RunSelfTeleport31(false, false) != 0 || RunSelfTeleport31(false, true) != 0 ||
                RunSelfTeleport31(true, false) != 0 || RunSelfTeleport31(true, true) != 0)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 31): Merged vs split self-teleport 1096.Place diverged.");
                return 1;
            }

            // Test 32 (Round 6 #2): Directed graph 0->1, 1->{2,3}, player at 2, monster actually moves [0,1,3]
            //          MUST be scored as DetTotal=1, DetHits=0 (NOT skipped as non-fork!).
            var cat32 = BuildTestCatalog(82016,
                "{\"id\":0,\"neighbors\":[1]},{\"id\":1,\"neighbors\":[2,3]},{\"id\":2,\"neighbors\":[]},{\"id\":3,\"neighbors\":[]}");
            var dec32 = new StubDecoder();
            int tokRun32 = dec32.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 101, NodeId = 2 } } },
                    Monsters = new List<StubUnit> { new StubUnit { Id = 610, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 0, BackNodeId = -1, FrontNodeIds = new[] { 1 } } } }
                }
            });
            int tokRoll32 = dec32.Register(new StubThrowDice { PlayerId = 610 });
            int tokMove32 = dec32.Register(new StubMove { PlayerId = 610, NodeIds = new[] { 0, 1, 3 } });
            int tokFin32 = dec32.Register(new StubFinish());
            var res32 = ReplayEvaluator.EvaluateBytes("dir-fork",
                ConcatFrames(MakeFrame(1003, tokRun32), MakeFrame(5022, tokRoll32), MakeFrame(5028, tokMove32), MakeFrame(1016, tokFin32)),
                dec32, cat32);
            if (res32.Buckets["MidWalkFork"].DetTotal != 1 || res32.Buckets["MidWalkFork"].DetHits != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 32): Directed fork was skipped (det={res32.Buckets["MidWalkFork"].DetHits}/{res32.Buckets["MidWalkFork"].DetTotal}).");
                return 1;
            }

            // Test 33 (Round 6 #3): Context-independent canonical calibration key:
            //          15x [0.25, 0.75] + 15x [0.25, 0.2502, 0.4998] all picking 0.25 MUST merge into "25%" (n=30) and FAIL GATE 3!
            var splitBias33 = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            PopulatePassingBaseline(splitBias33);
            for (int i = 0; i < 15; i++)
            {
                ReplayEvaluator.RecordMultiBranchCalibration(board, 23, new List<ForkExitPrediction>
                {
                    new() { NextLandId = 22, ConditionalProbability = 0.25 },
                    new() { NextLandId = 55, ConditionalProbability = 0.75 }
                }, actualNext: 22, splitBias33.ProbBins);
                ReplayEvaluator.RecordMultiBranchCalibration(board3, 1, new List<ForkExitPrediction>
                {
                    new() { NextLandId = 2, ConditionalProbability = 0.25 },
                    new() { NextLandId = 3, ConditionalProbability = 0.2502 },
                    new() { NextLandId = 4, ConditionalProbability = 0.4998 }
                }, actualNext: 2, splitBias33.ProbBins);
            }
            for (int i = 0; i < 15; i++) ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, actualNext: 22, splitBias33.ProbBins);
            for (int i = 0; i < 15; i++) ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, actualNext: 55, splitBias33.ProbBins);
            var e33 = ReportWriter.EvaluateGates(splitBias33, t);
            if (!splitBias33.ProbBins.ContainsKey("25%") || splitBias33.ProbBins["25%"].Opp != 30 || e33.CalibrationGate != GateStatus.Fail)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 33): Context-dependent bin split hid 25% bias (keys={string.Join(",", splitBias33.ProbBins.Keys)}, gate={e33.CalibrationGate}).");
                return 1;
            }

            // Test 34 (Round 6 #4): Corrupt mid-stream 5028 between valid 1003 and 1016 MUST fail HasValidFrameStream
            //          (so primary CDN falls back to backup CDN) AND fail non-PVE / missing-map as TruncatedOrCorrupt=1.
            byte[] badMid5028 = MakeFrame(5028, 999999); // unregistered token => TryDecode returns null
            byte[] corruptMidStream = ConcatFrames(MakeFrame(1003, tokRun11), badMid5028, MakeFrame(1016, tokFin11));
            if (ReplayEvaluator.HasValidFrameStream(corruptMidStream, dec11))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 34a): HasValidFrameStream accepted undecodable mid-stream 5028.");
                return 1;
            }
            int tokNonPve34 = dec11.Register(new StubRunning { Room = new StubRoom { MapId = 81001 } });
            var resNonPveBad = ReplayEvaluator.EvaluateBytes("npve-bad", ConcatFrames(MakeFrame(1003, tokNonPve34), badMid5028, MakeFrame(1016, tokFin11)), dec11, forkCatalog);
            if (resNonPveBad.TruncatedOrCorrupt != 1 || resNonPveBad.SkippedNonPve != 0)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 34b): Corrupt mid-stream 5028 hid under SkippedNonPve.");
                return 1;
            }

            // Test 35 (Round 6 #5): Duplicate non-PVE replays under different IDs MUST be deduplicated (never dilute DownloadFailRatio).
            byte[] validNonPve35 = ConcatFrames(MakeFrame(1003, tokNonPve34), MakeFrame(1016, tokFin11));
            var npveA = ReplayEvaluator.EvaluateBytes("1788434495879901", validNonPve35, dec11, forkCatalog);
            var npveB = ReplayEvaluator.EvaluateBytes("1788434495879902", validNonPve35, dec11, forkCatalog);
            var merged35 = Program.DeduplicateAndMerge(new[] { npveA, npveB });
            if (merged35.SkippedNonPve != 1 || merged35.TruncatedOrCorrupt != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 35): Duplicate non-PVE replay bypassed dedup (nonPve={merged35.SkippedNonPve}, corrupt={merged35.TruncatedOrCorrupt}).");
                return 1;
            }

            // Test 36 (Round 6 #6 & #22): Output overwriting --corpus/--maps rejected by TryParseArgs; missing --maps file throws.
            if (Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--corpus", "ids.txt", "--json-out", "ids.txt" }, out _, out _) ||
                Program.TryParseArgs(new[] { "--protocol-dir", "p", "--maps", "m.json", "--cache-dir", "c", "--md-out", "m.json" }, out _, out _))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 36a): Output overwriting input file was accepted.");
                return 1;
            }
            bool threwMissingMap = false;
            try { new BoardCatalog().LoadJsonFile(Path.Combine(Path.GetTempPath(), "definitely-missing-map-" + Guid.NewGuid().ToString("N") + ".json")); }
            catch (FileNotFoundException) { threwMissingMap = true; }
            if (!threwMissingMap)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 36b): Missing map file was silently skipped by BoardCatalog.LoadJsonFile.");
                return 1;
            }

            // Test 37 (Round 6 #7, #8, #9, #10):
            //   37a: IllegalMoveEdges>0 needs cache recovery and is NOT cacheable; clean MissingMap adopted for diagnosis but NOT cacheable.
            //   37b: Empty 5028 with unknown mover MUST fail as TruncatedOrCorrupt=1 (not ReplaysVerified=1).
            var illegalSum = new CertificationSummary { ReplaysAttempted = 1, ReplaysVerified = 1, IllegalMoveEdges = 1 };
            var missingMapSum = new CertificationSummary { ReplaysAttempted = 1, MissingMap = 1 };
            if (!Program.NeedsCacheRecovery(illegalSum) || Program.IsCacheableAndClean(illegalSum) ||
                !Program.ShouldAdoptRecoveryResult(missingMapSum) || Program.IsCacheableAndClean(missingMapSum))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 37a): Cache recovery / publish policy failed.");
                return 1;
            }
            var dec37 = new StubDecoder();
            int tokRun37 = dec37.Register(new StubRunning { Room = new StubRoom { MapId = 82016 } });
            int tokEmptyUnk37 = dec37.Register(new StubMove { PlayerId = 888888, NodeIds = Array.Empty<int>() });
            int tokFin37 = dec37.Register(new StubFinish());
            var res37 = ReplayEvaluator.EvaluateBytes("empty-unk",
                ConcatFrames(MakeFrame(1003, tokRun37), MakeFrame(5028, tokEmptyUnk37), MakeFrame(1016, tokFin37)),
                dec37, forkCatalog);
            if (res37.TruncatedOrCorrupt != 1 || res37.ReplaysVerified != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 37b): Empty 5028 unknown mover bypassed check (corrupt={res37.TruncatedOrCorrupt}, verified={res37.ReplaysVerified}).");
                return 1;
            }

            // Test 38 (Round 6 #11, #20, #21):
            //   38a: Sherry Place(->2), then ordinary Place(2->1), then ordinary Place(1->2, Front=[3]) MUST NOT reactivate ShirleyThrow.
            //   38b: Single corrupt replay (Attempted=1, Verified=0, Corrupt=1) MUST report CoverageGate == Fail (not InsufficientSample).
            //   38c: Spawn Front=[Blue(4)] when RedBlue=3 and actual move=3 MUST count SpawnF1Actual=0, SpawnF1RedBlue=1.
            var dec38 = new StubDecoder();
            int tokRun38 = dec38.Register(new StubRunning
            {
                Room = new StubRoom
                {
                    MapId = 82016,
                    Players = new List<StubUnit> { new StubUnit { Id = 1, Slot = 0, Hero = new StubHero { HeroId = 306, NodeId = 1 } } },
                    Monsters = new List<StubUnit>
                    {
                        new StubUnit { Id = 620, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 1, BackNodeId = -1, FrontNodeIds = new[] { 2 } } },
                        new StubUnit { Id = 621, Hero = new StubHero { HeroId = 1002, MonsterType = 11, NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 4 } } }
                    }
                }
            });
            int tokSherry38 = dec38.Register(new StubAttr { PlayerId = 1, Cause = new StubCause { Id = 30601 }, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 620, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 3 } } } } } });
            int tokLeave38 = dec38.Register(new StubAttr { PlayerId = 1, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 620, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 1, BackNodeId = 2, FrontNodeIds = new[] { 2 } } } } } });
            int tokReturn38 = dec38.Register(new StubAttr { PlayerId = 1, EffectDatas = new List<StubEffect> { new StubEffect { PlayerId = 620, Place = new StubPlace { Place = new StubPlaceInner { NodeId = 2, BackNodeId = 1, FrontNodeIds = new[] { 3 } } } } } });
            int tokRoll620 = dec38.Register(new StubThrowDice { PlayerId = 620 });
            int tokMove620 = dec38.Register(new StubMove { PlayerId = 620, NodeIds = new[] { 2, 3 } }); // Must be 100%-Det (Front=[3]), NOT ShirleyThrow!
            int tokRoll621 = dec38.Register(new StubThrowDice { PlayerId = 621 });
            int tokMove621 = dec38.Register(new StubMove { PlayerId = 621, NodeIds = new[] { 2, 3 } }); // Spawn Front=[4], RedBlue=3, actual=3!
            int tokFin38 = dec38.Register(new StubFinish());
            var res38 = ReplayEvaluator.EvaluateBytes("stale-tag-audit",
                ConcatFrames(MakeFrame(1003, tokRun38), MakeFrame(1040, tokSherry38), MakeFrame(1040, tokLeave38), MakeFrame(1040, tokReturn38),
                    MakeFrame(5022, tokRoll620), MakeFrame(5028, tokMove620),
                    MakeFrame(5022, tokRoll621), MakeFrame(5028, tokMove621), MakeFrame(1016, tokFin38)),
                dec38, forkCatalog);
            if (res38.Buckets["ForkStart"].DetHits != 1 || res38.Buckets["ForkStart"].DetTotal != 1 || res38.Buckets["ForkStart"].ProbTotal != 0)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 38a): Old ShirleyThrow tag reactivated after F->B->F (det={res38.Buckets["ForkStart"].DetTotal}, prob={res38.Buckets["ForkStart"].ProbTotal}).");
                return 1;
            }
            var onlyCorruptGate = ReportWriter.EvaluateGates(new CertificationSummary { ReplaysAttempted = 1, ReplaysVerified = 0, TruncatedOrCorrupt = 1 }, t);
            if (onlyCorruptGate.CoverageGate != GateStatus.Fail)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 38b): All-corrupt corpus reported {onlyCorruptGate.CoverageGate} instead of Fail.");
                return 1;
            }
            if (res38.FrontAudit.SpawnF1 != 1 || res38.FrontAudit.SpawnF1Actual != 0 || res38.FrontAudit.SpawnF1RedBlue != 1)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 38c): SpawnF1RedBlue did not compare nextTile==rbNext (f1={res38.FrontAudit.SpawnF1}, actual={res38.FrontAudit.SpawnF1Actual}, rb={res38.FrontAudit.SpawnF1RedBlue}).");
                return 1;
            }

            Console.WriteLine("ReplayCertifier self-tests PASSED (38/38).");
            return 0;
        }
    }
}
