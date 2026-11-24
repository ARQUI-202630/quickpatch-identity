using System.Reflection;
using System.Text.RegularExpressions;

using NetArchTest.Rules;

namespace QuickPatch.Identity.UnitTests.Architecture;

/// <summary>
/// Reglas del SDD 10.3.2 que NetArchTest no trae hechas (SCRUM-709): ciclos entre las carpetas de una misma capa y un
/// caso de uso por archivo. El grafo de carpetas sale de las dependencias reales del IL, no de los <c>using</c>.
/// </summary>
internal static partial class LayerRules
{
    /// <summary>Ciclos entre las carpetas de primer nivel de <paramref name="layerNamespace"/>, como «A -> B -> A».</summary>
    public static IReadOnlyList<string> FolderCycles(Assembly assembly, string layerNamespace)
    {
        var prefix = layerNamespace + ".";
        var folders = assembly.GetTypes()
            .Select(t => t.Namespace)
            .Where(ns => ns is not null && ns.StartsWith(prefix, StringComparison.Ordinal))
            .Select(ns => ns![prefix.Length..].Split('.')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var edges = folders.ToDictionary(f => f, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var from in folders)
        {
            foreach (var to in folders.Where(f => f != from))
            {
                var result = Types.InAssembly(assembly)
                    .That().ResideInNamespaceMatching("^" + Regex.Escape(prefix + from) + @"(\.|$)")
                    .ShouldNot().HaveDependencyOnAny(prefix + to)
                    .GetResult();
                if (!result.IsSuccessful)
                {
                    edges[from].Add(to);
                }
            }
        }

        var cycles = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        void Visit(string node)
        {
            state[node] = 1;
            path.Add(node);
            foreach (var next in edges[node])
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    cycles.Add(string.Join(" -> ", path.Skip(path.IndexOf(next)).Append(next)));
                }
                else if (state.GetValueOrDefault(next) == 0)
                {
                    Visit(next);
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }

        foreach (var folder in folders.Where(f => state.GetValueOrDefault(f) == 0))
        {
            Visit(folder);
        }

        return cycles;
    }

    /// <summary>Archivos de <paramref name="projectFolder"/> que declaran handlers de más de un caso de uso.</summary>
    public static IReadOnlyList<string> FilesWithSeveralUseCases(string projectFolder)
    {
        var root = SourceRoot();
        var folder = Path.Combine(root, "src", projectFolder);
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => (File: f, Handlers: HandlerDeclaration().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value).Distinct().ToList()))
            .Where(x => x.Handlers.Count > 1)
            .Select(x => $"{Path.GetRelativePath(root, x.File)} ({string.Join(", ", x.Handlers)})")
            .ToList();
    }

    // La carpeta del repositorio es la que tiene el .slnx; las pruebas corren desde tests/unit/.../bin/<config>/<tfm>.
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.slnx").Length == 0)
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (*.slnx).");
    }

    [GeneratedRegex(@"\b(?:class|record)\s+(\w+Handler)\b")]
    private static partial Regex HandlerDeclaration();
}