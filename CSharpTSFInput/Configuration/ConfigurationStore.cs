using System;
using System.IO;
// MiniYamlReader lives in the shared engine-agnostic Core (super-layer); Manju consumes Core's reader.
using CSharpTSFInput.Core.Configuration;

namespace CSharpTSFInput.Configuration
{
    /// <summary>
    /// Configuration store. Reads <c>Resource/Configuration/default.yaml</c> at startup
    /// and exposes typed accessors via dotted-path keys (e.g. <c>"font.path"</c>).
    ///
    /// Design intent:
    ///   there is exactly one configuration anyone ever sees - single source of truth, with no
    ///   system/default/user split.
    ///   The shipped default.yaml IS the configuration; users edit it in place; the built-in
    ///   default lives as a literal fallback inside this class only as a last-resort safety net.
    /// </summary>
    public sealed class ConfigurationStore
    {
        private MiniYamlReader.Node _root = new MiniYamlReader.Node();
        private bool _loaded;
        private string _loadedPath = string.Empty;

        public string LoadedPath => _loadedPath;

        /// <summary>Load configuration from the given directory. Looks for configuration.yaml.</summary>
        public void Load(string configurationDirectory)
        {
            if (string.IsNullOrEmpty(configurationDirectory)) return;
            string path = Path.Combine(configurationDirectory, "configuration.yaml");
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                _root = MiniYamlReader.ParseFile(path);
                _loaded = true;
                _loadedPath = path;
            }
            catch (Exception)
            {
                _loaded = false;
            }
        }

        /// <summary> A store read from the file at <paramref name="path"/>, or null with
        /// <paramref name="error"/> saying why the file could not be read.</summary>
        public static ConfigurationStore? TryRead(string path, out string? error)
        {
            try
            {
                var store = new ConfigurationStore { _root = MiniYamlReader.ParseFile(path), _loaded = true, _loadedPath = path };
                error = null;
                return store;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary> Takes over everything <paramref name="other"/> holds.</summary>
        public void ReplaceWith(ConfigurationStore other)
        {
            _root = other._root;
            _loaded = other._loaded;
            _loadedPath = other._loadedPath;
        }

        /// <summary> The value at a dotted path as written in the file, or null when the file has none.</summary>
        public string? GetRaw(string dottedKey) => Resolve(dottedKey)?.Scalar;

        /// <summary>Get a string value by dotted path. Returns fallback if not present.</summary>
        public string GetString(string dottedKey, string fallback)
        {
            var node = Resolve(dottedKey);
            if (node != null && node.Scalar != null) return node.Scalar;
            return fallback;
        }


        /// <summary>Get a boolean value by dotted path. Returns fallback if not present.</summary>
        public bool GetBool(string dottedKey, bool fallback)
        {
            var node = Resolve(dottedKey);
            if (node == null || node.Scalar == null) return fallback;
            string s = node.Scalar.Trim().ToLowerInvariant();
            return s == "true" || s == "yes" || s == "on" || s == "1";
        }

        private MiniYamlReader.Node? Resolve(string dottedKey)
        {
            if (string.IsNullOrEmpty(dottedKey)) return null;
            var current = _root;
            foreach (var part in dottedKey.Split('.'))
            {
                if (current?.Mapping == null) return null;
                if (!current.Mapping.TryGetValue(part, out var child)) return null;
                current = child;
            }
            return current;
        }
    }
}
