using System.Reflection;
using WorldLoader.Il2CppGen.Generator.Contexts;
using WorldLoader.Il2CppGen.Generator.Utils;
using Mono.Cecil;
using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace WorldLoader.Il2CppGen.Generator.Passes;

internal static class Pass90WriteToDisk
{
    internal static void DoPass(RewriteGlobalContext context, GeneratorOptions options)
    {
        var registerMethod =
            typeof(DefaultAssemblyResolver).GetMethod("RegisterAssembly",
                BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var asmContext in context.Assemblies)
        {
            var module = asmContext.NewAssembly.MainModule;
            if (module.AssemblyResolver is DefaultAssemblyResolver resolver)
            {
                foreach (var reference in module.AssemblyReferences)
                {
                    // TODO: Instead of a hack, set correctly initially via source generator
                    if (reference.Name == "System.Private.CoreLib")
                    {
                        CorlibReferences.RewriteReferenceToMscorlib(reference);
                        continue;
                    }

                    var match = context.Assemblies.FirstOrDefault(f => f.NewAssembly.FullName == reference.FullName);
                    if (match != null)
                    {
                        registerMethod!.Invoke(resolver, new object[] { match.NewAssembly });
                    }
                }
            }
        }

        var assembliesToProcess = context.Assemblies
            .Where(it => !options.AdditionalAssembliesBlacklist.Contains(it.NewAssembly.Name.Name));

        void Processor(UnhollowedAssemblyContext assemblyContext)
        {
            assemblyContext.NewAssembly.Write(
                Path.Combine(options.OutputDir ?? ".", $"{assemblyContext.NewAssembly.Name.Name}.dll"));
        }

        if (options.Parallel)
        {
            Parallel.ForEach(assembliesToProcess, Processor);
        }
        else
        {
            foreach (var assemblyRewriteContext in assembliesToProcess)
            {
                Processor(assemblyRewriteContext);
            }
        }
    }
}
