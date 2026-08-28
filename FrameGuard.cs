using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsVaoGc
{
    public class FrameGuard : IRenderer
    {
        const float HitchMs = 80f;
        const int Window = 240;

        readonly ICoreClientAPI capi;
        readonly string metricsPath;
        readonly float[] samples = new float[Window];
        readonly float[] scratch = new float[Window];
        readonly Stopwatch sinceCorrect = Stopwatch.StartNew();
        readonly Stopwatch sinceWrite = Stopwatch.StartNew();
        readonly object writeGate = new object();

        int idx;
        int count;
        int hitchCount;
        int corrections;
        int writeQueued;

        public double RenderOrder => 0.91;
        public int RenderRange => 0;

        public FrameGuard(ICoreClientAPI capi)
        {
            this.capi = capi;
            metricsPath = Path.Combine(capi.GetOrCreateDataPath("Logs"), "frameguard.json");
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Ortho) return;
            float ms = dt * 1000f;
            if (ms < 0.1f || ms > 5000f) return;

            samples[idx] = ms;
            idx = (idx + 1) % Window;
            if (count < Window) count++;

            bool hitch = ms >= HitchMs;
            if (hitch)
            {
                hitchCount++;
                Correct();
            }

            // write metrics off this thread. don't sort or hit disk here, it hitchs at high fps.
            if (sinceWrite.ElapsedMilliseconds >= 1000)
            {
                sinceWrite.Restart();
                ScheduleMetricsWrite(InstantFps(), Average(), Percentile(0.95f), ms, hitch);
            }
        }

        void Correct()
        {
            if (sinceCorrect.ElapsedMilliseconds < 400) return;
            sinceCorrect.Restart();
            corrections++;
            // extra drain only. GC.Collect on the render thread is a stall.
            VaoGcMod.Drain(256);
        }

        float InstantFps()
        {
            float last = samples[(idx + Window - 1) % Window];
            return last <= 0 ? 0 : 1000f / last;
        }

        float Average()
        {
            if (count == 0) return 0;
            float s = 0;
            for (int i = 0; i < count; i++) s += samples[i];
            return s / count;
        }

        float Percentile(float p)
        {
            if (count == 0) return 0;
            Array.Copy(samples, scratch, count);
            Array.Sort(scratch, 0, count);
            return scratch[(int)((count - 1) * p)];
        }

        void ScheduleMetricsWrite(float fps, float avg, float p95, float lastMs, bool hitch)
        {
            // if a write is already going, skip. next one will be newer anyway.
            if (Interlocked.CompareExchange(ref writeQueued, 1, 0) != 0) return;

            int hitches = hitchCount;
            int corr = corrections;
            int queued = VaoGcMod.Queued;
            int reclaimed = VaoGcMod.Reclaimed;
            int pending = VaoGcMod.PendingCount;
            string path = metricsPath;
            string ver = VaoGcMod.Version;
            string ts = DateTime.Now.ToString("o");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var sb = new StringBuilder(256);
                    sb.Append('{');
                    sb.Append("\"ts\":\"").Append(ts).Append("\",");
                    sb.Append("\"ver\":\"").Append(ver).Append("\",");
                    sb.Append("\"fps\":").Append(fps.ToString("0.0")).Append(',');
                    sb.Append("\"avgMs\":").Append(avg.ToString("0.00")).Append(',');
                    sb.Append("\"p95Ms\":").Append(p95.ToString("0.00")).Append(',');
                    sb.Append("\"lastMs\":").Append(lastMs.ToString("0.00")).Append(',');
                    sb.Append("\"hitch\":").Append(hitch ? "true" : "false").Append(',');
                    sb.Append("\"hitches\":").Append(hitches).Append(',');
                    sb.Append("\"corrections\":").Append(corr).Append(',');
                    sb.Append("\"gcPulses\":0,");
                    sb.Append("\"queued\":").Append(queued).Append(',');
                    sb.Append("\"reclaimed\":").Append(reclaimed).Append(',');
                    sb.Append("\"pending\":").Append(pending);
                    sb.Append('}');

                    lock (writeGate)
                    {
                        File.WriteAllText(path, sb.ToString());
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref writeQueued, 0);
                }
            });
        }

        public void Dispose() { }
    }
}

