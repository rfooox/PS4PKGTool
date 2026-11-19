#nullable enable
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace PS4PKGTool.Utilities.TrophyMetadata
{
    /// <summary>Persistent, validated Content ID to NP Communication ID mapping.</summary>
    public sealed class NpCommunicationIdCache
    {
        private static readonly object Sync = new();
        private readonly string _cachePath;
        private readonly NpCommunicationIdResolver _resolver = new();

        public NpCommunicationIdCache(string cachePath)
        {
            if (string.IsNullOrWhiteSpace(cachePath))
                throw new ArgumentException("必须指定缓存路径。", nameof(cachePath));
            _cachePath = Path.GetFullPath(cachePath);
        }

        public bool TryGet(string contentId, out string? npCommunicationId)
        {
            string key = NormalizeContentId(contentId);
            lock (Sync)
            {
                Dictionary<string, string> values = ReadValues();
                if (values.TryGetValue(key, out string? value) && _resolver.IsValid(value))
                {
                    npCommunicationId = value;
                    return true;
                }
            }

            npCommunicationId = null;
            return false;
        }

        public int Count
        {
            get
            {
                lock (Sync)
                    return ReadValues().Count;
            }
        }

        public void Clear()
        {
            lock (Sync)
            {
                if (File.Exists(_cachePath)) File.Delete(_cachePath);
            }
        }

        public void Set(string contentId, string npCommunicationId)
        {
            string key = NormalizeContentId(contentId);
            string value = npCommunicationId.Trim().ToUpperInvariant();
            if (!_resolver.IsValid(value))
                throw new ArgumentException("NP Communication ID 必须匹配 NPWRxxxxx_00。", nameof(npCommunicationId));

            lock (Sync)
            {
                Dictionary<string, string> values = ReadValues();
                values[key] = value;

                string? directory = Path.GetDirectoryName(_cachePath);
                if (directory == null)
                    throw new InvalidOperationException("缓存路径没有上级目录。");
                Directory.CreateDirectory(directory);

                string temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(values, Formatting.Indented));
                    File.Move(temporaryPath, _cachePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
        }

        private Dictionary<string, string> ReadValues()
        {
            if (!File.Exists(_cachePath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string json = File.ReadAllText(_cachePath);
                var stored = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return stored == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                // A damaged cache must never block trophy loading. A later Set replaces it atomically.
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeContentId(string contentId)
        {
            if (string.IsNullOrWhiteSpace(contentId))
                throw new ArgumentException("必须指定安装包 Content ID。", nameof(contentId));
            return contentId.Trim().ToUpperInvariant();
        }
    }
}
