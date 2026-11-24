using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public sealed class StubFinish { public int Winner { get; set; } }
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

            Console.WriteLine("ReplayCertifier self-tests PASSED (23/23).");
            return 0;
        }
    }
}
