using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    /// <summary>
    /// Loads official PVE board graphs with exact raw unsorted neighbor priority (Red > Blue > None).
    /// Supports both multi-map JSON catalogs and standalone single-map JSON files (e.g., Library 82016).
    /// </summary>
    internal sealed class BoardCatalog
    {
        private readonly Dictionary<int, List<(int SceneId, BoardGraph Graph)>> _byMap = new();
        private readonly Dictionary<int, string> _names = new();

        public int MapCount => _byMap.Count;

        public string GetMapName(int mapId) => _names.TryGetValue(mapId, out var n) ? n : mapId.ToString();

        public bool TryGetGraph(int mapId, int mapIndex, out BoardGraph graph)
        {
            graph = null;
            if (!_byMap.TryGetValue(mapId, out var scenes) || scenes.Count == 0) return false;
            int idx = (mapIndex >= 0 && mapIndex < scenes.Count) ? mapIndex : 0;
            graph = scenes[idx].Graph;
            return true;
        }

        public void LoadJsonFile(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("maps", out var mapsEl) && mapsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var mapEl in mapsEl.EnumerateArray())
                {
                    int mapId = mapEl.GetProperty("id").GetInt32();
                    string name = mapEl.GetProperty("name").GetString() ?? mapId.ToString();
                    _names[mapId] = name;

                    var scenes = new List<(int SceneId, BoardGraph Graph)>();
                    foreach (var prop in mapEl.GetProperty("layouts").EnumerateObject())
                    {
                        if (!int.TryParse(prop.Name, out int sceneId)) continue;
                        scenes.Add((sceneId, ParseNodes(prop.Value.GetProperty("nodes"))));
                    }
                    scenes.Sort((a, b) => a.SceneId.CompareTo(b.SceneId));
                    _byMap[mapId] = scenes;
                }
                return;
            }

            if (root.TryGetProperty("id", out var idEl) && root.TryGetProperty("nodes", out var nodesEl))
            {
                int mapId = idEl.GetInt32();
                string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? mapId.ToString()) : mapId.ToString();
                int sceneId = root.TryGetProperty("scene", out var sEl) ? sEl.GetInt32() : 0;
                _names[mapId] = name;
                _byMap[mapId] = new List<(int, BoardGraph)> { (sceneId, ParseNodes(nodesEl)) };
            }
        }

        private static BoardGraph ParseNodes(JsonElement nodesEl)
        {
            var nodes = new List<LandNodeSnapshot>();
            foreach (var nEl in nodesEl.EnumerateArray())
            {
                int id = nEl.GetProperty("id").GetInt32();
                var nbs = nEl.GetProperty("neighbors").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                nodes.Add(new LandNodeSnapshot(id, nbs));
            }
            return new BoardGraph(nodes);
        }
    }
}
