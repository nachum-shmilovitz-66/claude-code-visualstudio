#if VS2017 || VS2019
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// VS 2017/2019 ship System.Text.Json 4.x, and devenv's binding redirects stop at that
    /// version, so our 9.x reference fails the normal fusion bind. This resolver serves the
    /// copies we pack into the VSIX. It only ever fires after VS's own bind has already
    /// failed, and only for the closed list below, so VS components that bind the 4.x
    /// versions are unaffected. VS 2022+ ship these in-box, so the type is compiled out there.
    /// </summary>
    internal static class AssemblyResolver
    {
        private static readonly HashSet<string> Shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Text.Json",
            "System.Text.Encodings.Web",
            "System.IO.Pipelines",
            "System.Memory",
            "System.Buffers",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Threading.Tasks.Extensions",
            "System.Numerics.Vectors",
            "System.ValueTuple",
            "Microsoft.Bcl.AsyncInterfaces",
        };

        private static int _installed;

        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; }
            catch (Exception) { return null; }

            if (!Shipped.Contains(name)) return null;

            var dir = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;

            var path = Path.Combine(dir, name + ".dll");
            try { return File.Exists(path) ? Assembly.LoadFrom(path) : null; }
            catch (Exception) { return null; }
        }
    }
}
#endif
