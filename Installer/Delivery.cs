using System;
using System.IO;
using System.Linq;

namespace ManjuInstaller
{
    /// <summary>
    /// THE DELIVERY AS FILES: its three folders, and copying them into the folder chosen on the
    /// location page.
    ///
    /// Everything the copy writes is under the chosen folder. It creates the three folders there and
    /// copies file by file; it never deletes, and it follows no junction or symbolic link out of the
    /// delivery.
    /// </summary>
    public static class Delivery
    {
        /// <summary>The folders a delivery consists of.</summary>
        public static readonly string[] Folders = { "Runtime", "Installer", "Script" };

        /// <summary>The full form of a path, without a trailing separator.</summary>
        public static string Full(string path)
        {
            string full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }

        public static bool SamePath(string a, string b) =>
            string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies inside it.</summary>
        public static bool IsUnder(string path, string root)
        {
            string p = Full(path).TrimEnd('\\') + "\\", r = Full(root).TrimEnd('\\') + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether the delivery at <paramref name="source"/>, whose DLL is <paramref name="dll"/>, can be
        /// installed into <paramref name="target"/>. Null when it can; otherwise the sentence the
        /// location page shows.
        /// </summary>
        /// <param name="installed">The copy Windows has registered, or null.</param>
        public static string? CheckTarget(string source, string dll, string target, Installation? installed)
        {
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target.Trim()))
                return "Enter a full path, such as D:\\Manju IME.";
            target = target.Trim();
            if (target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || target.IndexOf('*') >= 0 || target.IndexOf('?') >= 0)
                return "The folder name contains characters Windows does not allow.";
            if (SamePath(source, target)) return null;

            foreach (string f in Folders)
                if (!Directory.Exists(Path.Combine(source, f)))
                    return $"This copy has no {f} folder, so it can only be installed where it is.";
            if (!IsUnder(dll, source))
                return "The input method of this copy is not inside it, so it can only be installed where it is.";
            foreach (string f in Folders)
                if (IsUnder(target, Path.Combine(source, f)))
                    return $"That folder is inside the {f} folder of this copy. Choose a folder outside it.";
            if (installed is { Registered: true } && IsUnder(installed.Dll, target))
                return "Manju IME is installed in that folder, and Windows may still be using its files. "
                       + "Choose another folder, or install where this copy is.";
            if (File.Exists(target))
                return "That is a file, not a folder.";
            return null;
        }

        /// <summary>Copies the three folders of <paramref name="source"/> into <paramref name="target"/>,
        /// replacing files of the same name. Returns the number of files copied.</summary>
        public static int Copy(string source, string target)
        {
            string root = Full(target);
            int n = 0;
            foreach (string f in Folders)
                n += CopyTree(Path.Combine(source, f), Path.Combine(root, f), root);
            return n;
        }

        private static int CopyTree(string from, string to, string root)
        {
            // Every folder and file written is under the chosen folder; a path that would leave it
            // stops the copy.
            if (!IsUnder(to, root))
                throw new InvalidOperationException($"{to} is outside {root}; nothing is written there.");
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (string file in Directory.EnumerateFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
                n++;
            }
            foreach (string dir in Directory.EnumerateDirectories(from)
                                            .Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
                n += CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), root);
            return n;
        }
    }
}
