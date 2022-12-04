﻿using HarmonyLib.Public.Patching;
using Il2CppGen.Runtime.InteropTypes;
using WorldLoader.Il2CppGen.Internal.Host;

namespace WorldLoader.Il2CppGen.HarmonySupport;

public static class HarmonySupport
{
    public static T AddHarmonySupport<T>(this T host) where T : BaseHost
    {
        host.AddComponent(new HarmonySupportComponent());
        return host;
    }
}

internal class HarmonySupportComponent : IHostComponent
{
    public void Dispose() => PatchManager.ResolvePatcher -= TryResolve;

    public void Start() => PatchManager.ResolvePatcher += TryResolve;

    private static void TryResolve(object sender, PatchManager.PatcherResolverEventArgs args)
    {
        if (args.Original.DeclaringType?.IsSubclassOf(typeof(Il2CppObjectBase)) != true)
            return;

        var backend = new Il2CppDetourMethodPatcher(args.Original);
        if (backend.IsValid)
            args.MethodPatcher = backend;
        
    }
}