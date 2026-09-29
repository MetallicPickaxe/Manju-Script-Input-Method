using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ManjuInstaller
{
    /// <summary>
    /// WRITING `install.language_identifier` INTO THE SETTINGS FILE BESIDE THE DLL.
    ///
    /// Only the value of that one line changes. Every other byte of the file stays as it was: the
    /// comments, the order, the line endings, and a byte order mark when the file has one. A file
    /// without the line gets it under its `install:` key, or a new `install:` block at the end.
    /// `Install_MANJU_IME.ps1` does the same edit (`Set-ManjuLangIdSetting`).
    /// </summary>
    public static class SettingsFile
    {
        private static readonly Regex LangIdLine = new(@"(?m)^([ \t]*language_identifier[ \t]*:[ \t]*)[^\r\n]*");
        private static readonly Regex InstallKey = new(@"(?m)^install[ \t]*:[^\r\n]*");

        /// <summary>Carries out a `WriteSetting` step: <paramref name="setting"/> is `install.language_identifier=XXXX`.</summary>
        public static void Write(string path, string setting)
        {
            int eq = setting.IndexOf('=');
            string key = eq > 0 ? setting[..eq] : setting;
            if (key != Ids.LangIdSetting)
                throw new InvalidOperationException($"setup writes only {Ids.LangIdSetting}, not {key}");
            WriteLangId(path, setting[(eq + 1)..]);
        }

        /// <summary>Sets `install.language_identifier` in the file at <paramref name="path"/>, creating the file
        /// when there is none.</summary>
        public static void WriteLangId(string path, string langId)
        {
            byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var utf8 = new UTF8Encoding(false);
            string text = utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            string updated = WithLangId(text, langId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF });
            fs.Write(utf8.GetBytes(updated));
        }

        /// <summary>The text of a settings file with `install.language_identifier` set to <paramref name="langId"/>.</summary>
        public static string WithLangId(string text, string langId)
        {
            if (langId.Length != 4 || !Array.TrueForAll(langId.ToCharArray(), Uri.IsHexDigit))
                throw new ArgumentException($"a LANGID is four hex digits, not '{langId}'", nameof(langId));
            string value = "\"" + langId.ToUpperInvariant() + "\"";
            string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            Match line = LangIdLine.Match(text);
            if (line.Success)
                return text[..line.Index] + line.Groups[1].Value + value + text[(line.Index + line.Length)..];

            Match install = InstallKey.Match(text);
            if (install.Success)
            {
                int end = install.Index + install.Length;
                return text[..end] + nl + "  language_identifier: " + value + text[end..];
            }

            string lead = text.Length == 0 || text.EndsWith('\n') ? "" : nl;
            return text + lead + "install:" + nl + "  language_identifier: " + value + nl;
        }
    }
}
