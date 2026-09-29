using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ManjuInstaller
{
    /// <summary>
    /// THE COMMAND LINE OF BOTH PROGRAMS. Every option has a long name and, when it is one a user
    /// types, a short one. An argument the program does not know, a misspelled option included, is an
    /// error: the program prints which one, lists the options it has, and changes nothing. Ignored, an
    /// unknown option such as `--langid 0419` would print the plan for the default language as if
    /// nothing had been asked.
    ///
    /// Names are compared without regard to case. `--root` is for the tests that run the programs
    /// against a delivery elsewhere: it is accepted and not listed.
    /// </summary>
    public sealed class CommandLine
    {
        public sealed record Option(string Long, string? Short, string? Value, string Help)
        {
            public bool TakesValue => Value != null;
            public bool Listed => Short != null;
            public bool Matches(string arg) =>
                arg.Equals(Long, StringComparison.OrdinalIgnoreCase)
                || Short != null && arg.Equals(Short, StringComparison.OrdinalIgnoreCase);
        }

        public static readonly Option WhatIf = new("--whatif", "-w", null,
            "Print the steps, one per line, and change nothing. Needs no administrator rights.");
        public static readonly Option LanguageIdentifier = new("--language-identifier", "-l", "XXXX",
            "With --whatif: plan for this language, given as the four hexadecimal digits of its language identifier.");
        public static readonly Option Location = new("--location", "-p", "folder",
            "With --whatif: plan an install into this folder, given as a full path.");
        public static readonly Option Root = new("--root", null, "folder", "");

        public static IReadOnlyList<Option> InstallerOptions { get; } = new[] { WhatIf, LanguageIdentifier, Location, Root };
        public static IReadOnlyList<Option> UninstallerOptions { get; } = new[] { WhatIf, Root };

        private readonly Dictionary<Option, string?> _given = new();

        /// <summary>Null when the arguments are all known and complete; otherwise what is wrong with them.</summary>
        public string? Error { get; private set; }

        public bool Has(Option o) => _given.ContainsKey(o);
        public string? ValueOf(Option o) => _given.TryGetValue(o, out var v) ? v : null;

        public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlyList<Option> options)
        {
            var cl = new CommandLine();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                var o = options.FirstOrDefault(x => x.Matches(a));
                if (o == null) { cl.Error = $"Unknown option: {a}"; return cl; }
                if (cl._given.ContainsKey(o)) { cl.Error = $"{o.Long} is given twice"; return cl; }
                string? value = null;
                if (o.TakesValue)
                {
                    if (i + 1 >= args.Count || options.Any(x => x.Matches(args[i + 1])))
                    { cl.Error = $"{a} needs a value: {o.Long} {o.Value}"; return cl; }
                    value = args[++i];
                }
                cl._given[o] = value;
            }
            return cl;
        }

        /// <summary>The listed options of one program, one per line, short and long names side by side.</summary>
        public static string Usage(string program, IReadOnlyList<Option> options)
        {
            var sb = new StringBuilder();
            sb.Append("Usage: \"").Append(program).Append("\" [options]").AppendLine();
            sb.AppendLine("Without options the wizard opens. The options are:");
            foreach (var o in options.Where(x => x.Listed))
            {
                string names = $"{o.Short}, {o.Long}" + (o.TakesValue ? $" {o.Value}" : "");
                sb.Append("  ").Append(names.PadRight(34)).Append(o.Help).AppendLine();
            }
            return sb.ToString();
        }
    }
}
