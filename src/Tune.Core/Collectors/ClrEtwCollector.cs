﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

namespace Tune.Core.Collectors
{
    // https://github.com/Microsoft/dotnet-samples/blob/master/Microsoft.Diagnostics.Tracing/TraceEvent/TraceEvent/40_SimpleTraceLog.cs
    class ClrEtwCollector : IDisposable
    {
        private readonly List<ClrEtwGcData> gcData = new List<ClrEtwGcData>();
        private readonly List<ClrEtwHeapStatsData> heapStatsData = new List<ClrEtwHeapStatsData>();
        private readonly List<ClrEtwGenerationData>[] generationsData = new List<ClrEtwGenerationData>[]
        {
            new List<ClrEtwGenerationData>(),
            new List<ClrEtwGenerationData>(),
            new List<ClrEtwGenerationData>(),
            new List<ClrEtwGenerationData>()
        };
        private const string providerName = "Microsoft-Windows-DotNETRuntime";
        private const string sessionName = "Tune-DotNetRuntimeSession";

        private bool stopped;
        private TraceEventSession session;
        private TraceEventSession kernelSession;

        public void Start()
        {
            RunAsync();
        }

        public void Stop()
        {
            using (var rundownSession = new TraceEventSession(sessionName + "Rundown", "data.clrRundown.etl"))
            {
                rundownSession.EnableProvider(ClrRundownTraceEventParser.ProviderGuid, TraceEventLevel.Verbose,
                    (ulong) ClrRundownTraceEventParser.Keywords.Default);
                // Poll until 2 second goes by without growth.  
                for (var prevLength = new FileInfo("data.clrRundown.etl").Length;;)
                {
                    Thread.Sleep(2000);
                    var newLength = new FileInfo("data.clrRundown.etl").Length;
                    if (newLength == prevLength) break;
                    prevLength = newLength;
                }
            }

            // TODO: Currenty not aware of any more sophisticated control, when hosting sub-process it will wait for timeout without new events after sub-process ends
            //Thread.Sleep(4000);
            stopped = true;
            session?.Dispose();
            kernelSession?.Dispose();
            TraceEventSession.MergeInPlace("data.etl", TextWriter.Null);

            //var traceLog = TraceLog.OpenOrConvert("data.etl");
            //var simpleTraceLogProcess = traceLog.Processes.LastProcessWithID(Process.GetCurrentProcess().Id);
            //foreach (var data in simpleTraceLogProcess.EventsInProcess)
            //{
            //}

            using (var source = new ETWTraceEventSource("data.etl"))
            {
                source.Clr.GCHeapStats += ClrOnGcHeapStats;
                source.Clr.GCStart += ClrOnGcStart;
                source.Clr.GCStop += ClrOnGcStop;
                source.Clr.GCGenerationRange += ClrOnGcGenerationRange;
                source.Process();
            }
        }

        public List<ClrEtwHeapStatsData> HeapStatsData => this.heapStatsData;
        public List<ClrEtwGcData> GcData => this.gcData;
        public List<ClrEtwGenerationData>[] GenerationsData => this.generationsData;

        private void RunAsync()
        {
            var elevated = TraceEventSession.IsElevated();

            var eventSourceGuid = TraceEventProviders.GetProviderGuidByName(providerName);
            session = new TraceEventSession(sessionName, "data.etl");
            kernelSession = new TraceEventSession(KernelTraceEventParser.KernelSessionName, "data.kernel.etl");

            kernelSession.EnableKernelProvider(KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);

            var optionsWithStacks = new TraceEventProviderOptions() { StacksEnabled = true };
            session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)ClrTraceEventParser.Keywords.Default);
            session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)ClrTraceEventParser.Keywords.GC, optionsWithStacks);
        }

        public static bool CanCollect => TraceEventSession.IsElevated().GetValueOrDefault();

        private void ClrOnGcGenerationRange(GCGenerationRangeTraceData evt)
        {
            if (!IsTargetProcess(evt)) return;

            this.generationsData[evt.Generation].Add(new ClrEtwGenerationData()
            {
                Index = evt.EventIndex,
                TimeStamp = evt.TimeStamp,
                Start = evt.RangeStart,
                Used = evt.RangeUsedLength,
                Reserved = evt.RangeReservedLength,
            });
        }

        private void ClrOnGcStop(GCEndTraceData evt)
        {
            if (!IsTargetProcess(evt)) return;

        }

        private void ClrOnGcStart(GCStartTraceData evt)
        {
            if (!IsTargetProcess(evt)) return;

            gcData.Add(new ClrEtwGcData()
            {
                Index = evt.EventIndex,
                TimeStamp = evt.TimeStamp,
                Generation = evt.Depth,
                Reason = evt.Reason,
                Type = evt.Type,
                Description = $"Count: {evt.Count}"
            });
        }

        private void ClrOnGcHeapStats(GCHeapStatsTraceData evt)
        {
            if (!IsTargetProcess(evt)) return;

            heapStatsData.Add(new ClrEtwHeapStatsData()
            {
                Index = evt.EventIndex,
                TimeStamp = evt.TimeStamp,
                GenerationSize0 = evt.GenerationSize0,
                GenerationSize1 = evt.GenerationSize1,
                GenerationSize2 = evt.GenerationSize2,
                GenerationSize3 = evt.GenerationSize3,
                Description = $"TID: {evt.ThreadID}"
            });
        }

        private bool IsTargetProcess(TraceEvent evt)
        {
            return Process.GetCurrentProcess().Id == evt.ProcessID;
        }

        public void Dispose()
        {
            if (!stopped)
            {
                Stop();
            }
        }
    }
}
