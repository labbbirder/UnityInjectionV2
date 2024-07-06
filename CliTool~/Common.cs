using System.Reflection;

namespace BBBirder.UnityInjection.Tools;

using static ToolsConstants;
public static class Common
{
    static string[]? s_allowedAssemblies;

    public static void Init(string[] allowedAssemblies)
    {
        s_allowedAssemblies = allowedAssemblies;
        AppDomain.CurrentDomain.AssemblyResolve += (s, args) =>
        {
            var assemblyName = new AssemblyName(args.Name)?.Name;
            if (assemblyName == null)
            {
                throw new ArgumentException($"cannot resolve assembly {assemblyName}");
            }
            return LoadAssembly(assemblyName);
        };
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static Assembly LoadAssembly(string name)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        if (assembly != null) return assembly;
        var fileName = name + ".dll";

        // directly reside in allowed assemblies
        var fullPath = s_allowedAssemblies!.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a) == name);

        // reside in save path of an allowed assembly
        if (fullPath == null)
        {

            foreach (var p in s_allowedAssemblies!.Select(Path.GetDirectoryName).Distinct())
            {
                var filePath = Path.Combine(p, fileName);
                if (File.Exists(filePath))
                {
                    fullPath = filePath;
                    break;
                }
            }
        }

        // reside in ScriptAssemblies
        if (fullPath == null)
        {
            fullPath = Path.Combine("Library\\ScriptAssemblies", fileName);
            if (!File.Exists(fullPath)) fullPath = null;
        }

        if (fullPath != null) return Assembly.Load(File.ReadAllBytes(fullPath));

        throw new ArgumentException($"cannot resolve assembly {name}");
    }
}
