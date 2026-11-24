using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MonsterPathPredictor.ReplayCertifier
{
    /// <summary>
    /// Thread-safe reflection wrapper around game protobuf types loaded from a user-supplied Protocol directory.
    /// Avoids compile-time dependencies on Unity/game assemblies.
    /// </summary>
    internal sealed class ProtocolDecoder
    {
        private readonly Assembly _gameAsm;
        private readonly ConcurrentDictionary<string, (object Parser, MethodInfo ParseFrom)> _cache = new(StringComparer.Ordinal);

        public ProtocolDecoder(string protocolDir)
        {
            if (string.IsNullOrWhiteSpace(protocolDir))
                throw new DirectoryNotFoundException("Protocol directory not specified.");

            string fullDir = Path.GetFullPath(protocolDir);
            if (!Directory.Exists(fullDir))
                throw new DirectoryNotFoundException("Protocol directory not found: " + fullDir);

            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                if (name.Name == null || name.Name == "System.Runtime" || name.Name == "netstandard" ||
                    name.Name == "mscorlib" || name.Name.StartsWith("System.", StringComparison.Ordinal))
                    return null;
                var p = Path.Combine(fullDir, name.Name + ".dll");
                return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
            };

            string runtimeDll = Path.Combine(fullDir, "AstralParty.Runtime.dll");
            if (!File.Exists(runtimeDll))
                throw new FileNotFoundException("AstralParty.Runtime.dll not found in " + fullDir);

            _gameAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(runtimeDll);
        }

        public dynamic TryDecode(string typeName, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                var entry = _cache.GetOrAdd(typeName, tn =>
                {
                    var t = _gameAsm.GetType(tn, throwOnError: true);
                    var parser = t.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
                    return (parser, parseFrom);
                });
                return entry.ParseFrom.Invoke(entry.Parser, new object[] { payload });
            }
            catch
            {
                return null;
            }
        }
    }
}
