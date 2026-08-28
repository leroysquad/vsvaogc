using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VsVaoGc
{
    public class VaoGcMod : ModSystem
    {
        public const string HarmonyId = "vsvaogc";
        public const string Version = "1.1.4";

        // dispose more per frame if the queue is getting long
        const int BaseDisposePerFrame = 16;
        const int ElevatedDisposePerFrame = 32;
        const int HighDisposePerFrame = 64;
        const int ElevatedPending = 64;
        const int HighPending = 256;

        static readonly ConcurrentQueue<VAO> Pending = new ConcurrentQueue<VAO>();
        static int queued;
        static int reclaimed;
        static ILogger log;
        Harmony harmony;
        FrameGuard guard;
        ICoreClientAPI capiForDispose;

        public static int Queued { get { return queued; } }
        public static int Reclaimed { get { return reclaimed; } }
        public static int PendingCount { get { return Pending.Count; } }

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void Start(ICoreAPI api)
        {
            api.Logger.Notification("[vsvaogc] " + Version + " loaded. dll only, don't drop src into Mods.");
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            log = capi.Logger;
            capiForDispose = capi;
            log.Notification("[vsvaogc] " + Version + " client start, applying patches.");

            try
            {
                var finalize = AccessTools.Method(typeof(VAO), "Finalize");
                var drain = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.ExecuteMainThreadTasks));
                if (finalize == null) throw new InvalidOperationException("VAO.Finalize not found");
                if (drain == null) throw new InvalidOperationException("ClientMain.ExecuteMainThreadTasks not found");

                harmony = new Harmony(HarmonyId);
                harmony.Patch(finalize, prefix: new HarmonyMethod(typeof(VaoFinalizePatch), nameof(VaoFinalizePatch.Prefix)));
                harmony.Patch(drain, postfix: new HarmonyMethod(typeof(VaoDrainPatch), nameof(VaoDrainPatch.Postfix)));
                log.Notification("[vsvaogc] Harmony patches applied.");
            }
            catch (Exception e)
            {
                log.Error("[vsvaogc] Harmony patch failed, leak fixer off. FrameGuard still arms. {0}", e);
            }

            guard = new FrameGuard(capi);
            capi.Event.RegisterRenderer(guard, EnumRenderStage.Ortho, "vsvaogc.frameguard");

            capi.ChatCommands.Create("vaogc")
                .WithDescription("Show client MeshRef leak-fixer stats")
                .HandleWith(_ =>
                {
                    return TextCommandResult.Success(
                        "[vaogc] v" + Version
                        + "  queued=" + queued
                        + "  reclaimed=" + reclaimed
                        + "  pending=" + Pending.Count);
                });

            log.Notification("[vsvaogc] " + Version + " running. no GC.Collect.");
        }

        public override void Dispose()
        {
            if (guard != null)
            {
                try { capiForDispose?.Event.UnregisterRenderer(guard, EnumRenderStage.Ortho); } catch { }
            }
            Drain(int.MaxValue);
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            guard = null;
            log = null;
            capiForDispose = null;
        }

        internal static bool TryQueue(VAO vao)
        {
            if (vao == null || vao.Disposed) return false;
            Pending.Enqueue(vao);
            System.Threading.Interlocked.Increment(ref queued);
            return true;
        }

        internal static int BudgetForPending(int pending)
        {
            if (pending >= HighPending) return HighDisposePerFrame;
            if (pending >= ElevatedPending) return ElevatedDisposePerFrame;
            return BaseDisposePerFrame;
        }

        internal static void Drain(int budget)
        {
            int n = 0;
            while (n < budget && Pending.TryDequeue(out VAO vao))
            {
                try
                {
                    if (vao != null && !vao.Disposed)
                    {
                        vao.Dispose();
                        System.Threading.Interlocked.Increment(ref reclaimed);
                    }
                }
                catch (Exception e)
                {
                    log?.Warning("[vsvaogc] Dispose failed: {0}", e.Message);
                }
                n++;
            }
        }

        static class VaoFinalizePatch
        {
            // true = let vanilla Finalize run. false = we queued it.
            public static bool Prefix(object __instance)
            {
                try
                {
                    VAO vao = __instance as VAO;
                    if (vao == null || vao.Disposed) return true;
                    if (ScreenManager.Platform != null && ScreenManager.Platform.IsShuttingDown) return true;
                    return !TryQueue(vao);
                }
                catch (Exception e)
                {
                    // don't crash the gc thread. just let vanilla Finalize run.
                    log?.Warning("[vsvaogc] Finalize prefix failed, falling back to vanilla: {0}", e.Message);
                    return true;
                }
            }
        }

        static class VaoDrainPatch
        {
            public static void Postfix()
            {
                // this method also runs entity loads and gui. if we throw, vs lists every harmony id on it.
                try
                {
                    Drain(BudgetForPending(Pending.Count));
                }
                catch (Exception e)
                {
                    log?.Warning("[vsvaogc] Drain postfix failed: {0}", e.Message);
                }
            }
        }
    }
}

