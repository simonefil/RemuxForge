using RemuxForge.Vulkan.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Scheduling
{
    /// <summary>
    /// Identifies a GPU phase that can be measured with timestamp queries
    /// </summary>
    internal enum VulkanGpuPhase
    {
        /// <summary>No measured phase</summary>
        None,
        /// <summary>Transfer of input data to the device</summary>
        Upload,
        /// <summary>Input normalization</summary>
        Normalize,
        /// <summary>Gaussian pyramid construction</summary>
        GaussianPyramid,
        /// <summary>Extrema detection and refinement</summary>
        Extrema,
        /// <summary>Keypoint orientation assignment</summary>
        Orientation,
        /// <summary>Descriptor construction</summary>
        Descriptor,
        /// <summary>Feature matching</summary>
        Matching,
        /// <summary>Geometric RANSAC validation</summary>
        Ransac,
        /// <summary>Transfer of results back to the host</summary>
        Readback
    }

    /// <summary>
    /// Associates two timestamp queries with a measured GPU phase
    /// </summary>
    internal readonly struct VulkanGpuTimestampSpan
    {
        /// <summary>
        /// Creates a timestamp span for a GPU phase
        /// </summary>
        /// <param name="startQuery">Index of the query written at the phase start</param>
        /// <param name="endQuery">Index of the query written at the phase end</param>
        /// <param name="phase">Phase represented by the query pair</param>
        public VulkanGpuTimestampSpan(uint startQuery, uint endQuery, VulkanGpuPhase phase)
        {
            this.StartQuery = startQuery;
            this.EndQuery = endQuery;
            this.Phase = phase;
        }

        /// <summary>Index of the query written at the phase start</summary>
        public uint StartQuery { get; }

        /// <summary>Index of the query written at the phase end</summary>
        public uint EndQuery { get; }

        /// <summary>GPU phase represented by this query pair</summary>
        public VulkanGpuPhase Phase { get; }
    }

    /// <summary>
    /// Manages command buffers, concurrent submissions, a timeline semaphore and GPU timestamps
    /// </summary>
    internal sealed unsafe class VulkanWorkScheduler : IDisposable
    {
        #region Variabili statiche

        /// <summary>
        /// Holds the slot currently recording commands on the calling thread
        /// </summary>
        [ThreadStatic]
        private static VulkanExecutionSlot s_recordingSlot;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Runtime context whose device, queue and timeline semaphore are used by the scheduler
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        /// <summary>
        /// Protects dequeue and enqueue operations on the available-slot queue
        /// </summary>
        private readonly object _slotLock;

        /// <summary>
        /// Serializes queue submissions and assignment of monotonically increasing timeline values
        /// </summary>
        private readonly object _submitLock;

        /// <summary>
        /// Stores slots that are not currently owned by a submission
        /// </summary>
        private readonly Queue<VulkanExecutionSlot> _availableSlots;

        /// <summary>
        /// Limits the number of slots acquired concurrently
        /// </summary>
        private readonly SemaphoreSlim _availability;

        /// <summary>
        /// Owns every slot created by this scheduler for disposal at shutdown
        /// </summary>
        private readonly List<VulkanExecutionSlot> _allSlots;

        /// <summary>
        /// Last timeline value allocated for a queue submission
        /// </summary>
        private long _nextTimelineValue;

        /// <summary>
        /// Indicates that no further work may be submitted
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates the configured set of reusable execution slots
        /// </summary>
        /// <param name="runtime">Runtime context that owns the Vulkan device and queue</param>
        /// <param name="slotCount">Maximum number of submissions that may be in flight</param>
        public VulkanWorkScheduler(VulkanRuntimeContext runtime, int slotCount)
        {
            this._runtime = runtime;
            this._slotLock = new object();
            this._submitLock = new object();
            this._availableSlots = new Queue<VulkanExecutionSlot>();
            this._availability = new SemaphoreSlim(slotCount, slotCount);
            this._allSlots = new List<VulkanExecutionSlot>(slotCount);
            this.Capacity = slotCount;
            for (int i = 0; i < slotCount; i++)
            {
                VulkanExecutionSlot slot = this.CreateSlot();
                this._allSlots.Add(slot);
                this._availableSlots.Enqueue(slot);
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Records and submits a workload while respecting slot ownership and cancellation
        /// </summary>
        /// <param name="record">Callback that records commands into the supplied command buffer</param>
        /// <param name="cancellationToken">Token used while acquiring a slot and waiting for prior work</param>
        /// <param name="diagnostics">Diagnostics object updated with submission, wait and timestamp data</param>
        /// <param name="submissionPhase">Optional phase covering the complete submission</param>
        /// <returns>A disposable submission representing the in-flight GPU work</returns>
        public VulkanSubmission Execute(Action<VkCommandBuffer> record, VulkanVisionDiagnostics diagnostics, VulkanGpuPhase submissionPhase, CancellationToken cancellationToken)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            this.ThrowIfDisposed();
            this._availability.Wait(cancellationToken);
            VulkanExecutionSlot slot;
            lock (this._slotLock)
                slot = this._availableSlots.Dequeue();
            try
            {
                if (slot.CompletionValue > 0)
                    this.Wait(slot.CompletionValue, diagnostics, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                this._runtime.DeviceApi.vkResetCommandPool(slot.CommandPool, VkCommandPoolResetFlags.None).CheckResult();
                VkCommandBufferBeginInfo beginInfo = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
                this._runtime.DeviceApi.vkBeginCommandBuffer(slot.CommandBuffer, &beginInfo).CheckResult();
                if (slot.TimestampQueryPool.IsNotNull)
                {
                    this._runtime.DeviceApi.vkCmdResetQueryPool(slot.CommandBuffer, slot.TimestampQueryPool, 0, VulkanExecutionSlot.TIMESTAMP_QUERY_CAPACITY);
                    this._runtime.DeviceApi.vkCmdWriteTimestamp(slot.CommandBuffer, VkPipelineStageFlags.TopOfPipe, slot.TimestampQueryPool, 0);
                    slot.NextTimestampQuery = 2;
                    slot.TimestampSpans.Clear();
                    slot.SubmissionPhase = submissionPhase;
                }
                s_recordingSlot = slot;
                try
                {
                    record(slot.CommandBuffer);
                }
                finally
                {
                    s_recordingSlot = null;
                }
                if (slot.TimestampQueryPool.IsNotNull)
                {
                    this._runtime.DeviceApi.vkCmdWriteTimestamp(slot.CommandBuffer, VkPipelineStageFlags.BottomOfPipe, slot.TimestampQueryPool, 1);
                    slot.TimestampPending = true;
                }
                this._runtime.DeviceApi.vkEndCommandBuffer(slot.CommandBuffer).CheckResult();
                ulong completionValue;
                lock (this._submitLock)
                {
                    completionValue = checked((ulong)Interlocked.Increment(ref this._nextTimelineValue));
                    VkSemaphore semaphore = this._runtime.TimelineSemaphore;
                    VkTimelineSemaphoreSubmitInfo timelineInfo = new VkTimelineSemaphoreSubmitInfo
                    {
                        signalSemaphoreValueCount = 1,
                        pSignalSemaphoreValues = &completionValue
                    };
                    VkCommandBuffer commandBuffer = slot.CommandBuffer;
                    VkSubmitInfo submitInfo = new VkSubmitInfo
                    {
                        pNext = &timelineInfo,
                        commandBufferCount = 1,
                        pCommandBuffers = &commandBuffer,
                        signalSemaphoreCount = 1,
                        pSignalSemaphores = &semaphore
                    };
                    this._runtime.DeviceApi.vkQueueSubmit(this._runtime.Queue, submitInfo, VkFence.Null).CheckResult();
                }
                slot.CompletionValue = completionValue;
                diagnostics.SubmitCount++;
                diagnostics.TimelineSignalCount++;
                return new VulkanSubmission(this, slot, completionValue);
            }
            catch
            {
                this.Return(slot);
                throw;
            }
        }

        /// <summary>
        /// Waits for the latest timeline value issued by the scheduler
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the host-side wait</param>
        /// <param name="diagnostics">Diagnostics object updated with wait data</param>
        public void Drain(VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            ulong value = checked((ulong)Interlocked.Read(ref this._nextTimelineValue));
            if (value > 0)
                this.Wait(value, diagnostics, cancellationToken);
        }

        /// <summary>
        /// Writes the beginning timestamp for a named GPU phase in the active recording slot
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the timestamp command</param>
        /// <param name="phase">Phase associated with the timestamp pair</param>
        /// <returns>A non-negative query token for <see cref="EndGpuPhase(VkCommandBuffer, int)"/>, or -1 when timestamps cannot be recorded</returns>
        public int BeginGpuPhase(VkCommandBuffer commandBuffer, VulkanGpuPhase phase)
        {
            VulkanExecutionSlot slot = s_recordingSlot;
            if (slot == null || slot.CommandBuffer != commandBuffer || slot.TimestampQueryPool.IsNull || slot.NextTimestampQuery + 1 >= VulkanExecutionSlot.TIMESTAMP_QUERY_CAPACITY)
                return -1;
            uint query = slot.NextTimestampQuery;
            slot.NextTimestampQuery += 2;
            this._runtime.DeviceApi.vkCmdWriteTimestamp(commandBuffer, VkPipelineStageFlags.TopOfPipe, slot.TimestampQueryPool, query);
            slot.TimestampSpans.Add(new VulkanGpuTimestampSpan(query, query + 1, phase));
            return checked((int)query);
        }

        /// <summary>
        /// Writes the ending timestamp for a previously started GPU phase
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the timestamp command</param>
        /// <param name="token">Query token returned by <see cref="BeginGpuPhase(VkCommandBuffer, VulkanGpuPhase)"/></param>
        public void EndGpuPhase(VkCommandBuffer commandBuffer, int token)
        {
            VulkanExecutionSlot slot = s_recordingSlot;
            if (token < 0 || slot == null || slot.CommandBuffer != commandBuffer || slot.TimestampQueryPool.IsNull)
                return;
                this._runtime.DeviceApi.vkCmdWriteTimestamp(commandBuffer, VkPipelineStageFlags.BottomOfPipe, slot.TimestampQueryPool, checked((uint)token + 1));
        }

        /// <summary>
        /// Releases all resources owned by the scheduler after draining submitted work
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            VulkanVisionDiagnostics diagnostics = new VulkanVisionDiagnostics();
            try
            {
                this.Drain(diagnostics, CancellationToken.None);
            }
            finally
            {
                for (int i = this._allSlots.Count - 1; i >= 0; i--)
                    this._allSlots[i].Dispose();
                this._allSlots.Clear();
                this._availability.Dispose();
            }
        }

        #endregion

        #region Metodi internal

        /// <summary>
        /// Completes a submission, resolves its timestamps and returns its slot to the scheduler
        /// </summary>
        /// <param name="slot">Execution slot associated with the submission</param>
        /// <param name="value">Timeline value signaled by the submission</param>
        /// <param name="cancellationToken">Token used for the initial wait</param>
        /// <param name="diagnostics">Diagnostics object updated with wait and timestamp data</param>
        internal void Complete(VulkanExecutionSlot slot, ulong value, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            try
            {
                try
                {
                    this.Wait(value, diagnostics, cancellationToken);
                    this.ResolveTimestamps(slot, diagnostics);
                }
                catch (OperationCanceledException)
                {
                    this.Wait(value, diagnostics, CancellationToken.None);
                    this.ResolveTimestamps(slot, diagnostics);
                    throw;
                }
            }
            finally
            {
                slot.CompletionValue = 0;
                this.Return(slot);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Creates one command pool, command buffer and optional timestamp query pool
        /// </summary>
        /// <returns>A fully initialized execution slot</returns>
        private VulkanExecutionSlot CreateSlot()
        {
            VkCommandPoolCreateInfo poolInfo = new VkCommandPoolCreateInfo
            {
                flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
                queueFamilyIndex = this._runtime.QueueFamilyIndex
            };
            this._runtime.DeviceApi.vkCreateCommandPool(&poolInfo, null, out VkCommandPool commandPool).CheckResult();
            try
            {
                this._runtime.DeviceApi.vkAllocateCommandBuffer(commandPool, out VkCommandBuffer commandBuffer).CheckResult();
                VkQueryPool queryPool = VkQueryPool.Null;
                if (this._runtime.Capabilities.TimestampQueries)
                {
                    VkQueryPoolCreateInfo queryInfo = new VkQueryPoolCreateInfo { queryType = VkQueryType.Timestamp, queryCount = VulkanExecutionSlot.TIMESTAMP_QUERY_CAPACITY };
                    VkResult queryResult = this._runtime.DeviceApi.vkCreateQueryPool(&queryInfo, null, out queryPool);
                    if (queryResult != VkResult.Success)
                        this._runtime.Capabilities.TimestampQueries = false;
                }
                return new VulkanExecutionSlot(this._runtime.DeviceApi, commandPool, commandBuffer, queryPool);
            }
            catch
            {
                this._runtime.DeviceApi.vkDestroyCommandPool(commandPool);
                throw;
            }
        }

        /// <summary>
        /// Waits for a timeline value while polling for cancellation
        /// </summary>
        /// <param name="value">Timeline value that must be reached</param>
        /// <param name="cancellationToken">Token polled between timed Vulkan waits</param>
        /// <param name="diagnostics">Diagnostics object updated with wait count and elapsed ticks</param>
        private void Wait(ulong value, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            VkSemaphore semaphore = this._runtime.TimelineSemaphore;
            VkSemaphoreWaitInfo waitInfo = new VkSemaphoreWaitInfo
            {
                semaphoreCount = 1,
                pSemaphores = &semaphore,
                pValues = &value
            };
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                VkResult result = this._runtime.DeviceApi.vkWaitSemaphores(&waitInfo, 100000000);
                diagnostics.WaitCount++;
                if (result == VkResult.Success)
                    break;
                if (result != VkResult.Timeout)
                    result.CheckResult();
                cancellationToken.ThrowIfCancellationRequested();
            }
            diagnostics.HostWaitTicks += stopwatch.ElapsedTicks;
        }

        /// <summary>
        /// Reads completed timestamp queries and aggregates their durations into diagnostics
        /// </summary>
        /// <param name="slot">Execution slot containing the pending timestamp queries</param>
        /// <param name="diagnostics">Diagnostics object receiving aggregate and per-phase timings</param>
        private void ResolveTimestamps(VulkanExecutionSlot slot, VulkanVisionDiagnostics diagnostics)
        {
            if (!slot.TimestampPending || slot.TimestampQueryPool.IsNull)
                return;
            uint queryCount = Math.Max(2u, slot.NextTimestampQuery);
            ulong[] values = new ulong[queryCount];
            fixed (ulong* pointer = values)
            {
                VkResult result = this._runtime.DeviceApi.vkGetQueryPoolResults(slot.TimestampQueryPool, 0, queryCount, checked((nuint)(queryCount * sizeof(ulong))), pointer, sizeof(ulong), VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait);
                result.CheckResult();
            }
            diagnostics.GpuExecutionNanoseconds += this.ToNanoseconds(values[0], values[1]);
            if (slot.SubmissionPhase != VulkanGpuPhase.None)
                AddPhaseTime(diagnostics, slot.SubmissionPhase, this.ToNanoseconds(values[0], values[1]));
            for (int i = 0; i < slot.TimestampSpans.Count; i++)
            {
                VulkanGpuTimestampSpan span = slot.TimestampSpans[i];
                AddPhaseTime(diagnostics, span.Phase, this.ToNanoseconds(values[span.StartQuery], values[span.EndQuery]));
            }
            diagnostics.TimestampQueryCount += checked((int)queryCount);
            slot.TimestampPending = false;
        }

        /// <summary>
        /// Converts a timestamp counter interval to nanoseconds with saturation on overflow
        /// </summary>
        /// <param name="start">Timestamp counter value at the interval start</param>
        /// <param name="end">Timestamp counter value at the interval end</param>
        /// <returns>Rounded elapsed nanoseconds, or zero when the interval is invalid</returns>
        private ulong ToNanoseconds(ulong start, ulong end)
        {
            if (end < start)
                return 0;
            double result = (end - start) * this._runtime.Capabilities.TimestampPeriodNanoseconds;
            return result >= ulong.MaxValue ? ulong.MaxValue : (ulong)Math.Round(result);
        }

        /// <summary>
        /// Adds a phase duration to the matching diagnostics counter
        /// </summary>
        /// <param name="diagnostics">Diagnostics object receiving the phase duration</param>
        /// <param name="phase">Phase whose counter must be updated</param>
        /// <param name="nanoseconds">Duration to add in nanoseconds</param>
        private static void AddPhaseTime(VulkanVisionDiagnostics diagnostics, VulkanGpuPhase phase, ulong nanoseconds)
        {
            if (phase == VulkanGpuPhase.Upload) diagnostics.GpuUploadNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Normalize) diagnostics.GpuNormalizeNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.GaussianPyramid) diagnostics.GpuGaussianPyramidNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Extrema) diagnostics.GpuExtremaNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Orientation) diagnostics.GpuOrientationNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Descriptor) diagnostics.GpuDescriptorNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Matching) diagnostics.GpuMatchingNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Ransac) diagnostics.GpuRansacNanoseconds += nanoseconds;
            else if (phase == VulkanGpuPhase.Readback) diagnostics.GpuReadbackNanoseconds += nanoseconds;
        }

        /// <summary>
        /// Returns a completed or abandoned slot to the available-slot pool
        /// </summary>
        /// <param name="slot">Slot whose ownership is being returned</param>
        private void Return(VulkanExecutionSlot slot)
        {
            lock (this._slotLock)
                this._availableSlots.Enqueue(slot);
            this._availability.Release();
        }

        /// <summary>
        /// Rejects use of the scheduler after disposal
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanWorkScheduler));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Maximum number of execution slots that may be in flight concurrently
        /// </summary>
        public int Capacity { get; }

        #endregion
    }

    /// <summary>
    /// Represents an in-flight submission and its completion state
    /// </summary>
    internal sealed class VulkanSubmission : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Scheduler that owns the execution slot and performs completion handling
        /// </summary>
        private VulkanWorkScheduler _scheduler;

        /// <summary>
        /// Execution slot retained until the submission is completed
        /// </summary>
        private VulkanExecutionSlot _slot;

        /// <summary>
        /// Timeline value signaled by this submission
        /// </summary>
        private readonly ulong _value;

        /// <summary>
        /// Indicates that the slot has already been returned to the scheduler
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates a handle for an in-flight scheduler submission
        /// </summary>
        /// <param name="scheduler">Scheduler responsible for completion and slot ownership</param>
        /// <param name="slot">Execution slot used by the submission</param>
        /// <param name="value">Timeline value signaled by the submission</param>
        public VulkanSubmission(VulkanWorkScheduler scheduler, VulkanExecutionSlot slot, ulong value)
        {
            this._scheduler = scheduler;
            this._slot = slot;
            this._value = value;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Waits for completion, resolves timestamps and releases the associated slot
        /// </summary>
        /// <param name="cancellationToken">Token used for the initial host-side wait</param>
        /// <param name="diagnostics">Diagnostics object receiving wait and timestamp data</param>
        public void Wait(VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if (this._disposed)
                return;
            this._scheduler.Complete(this._slot, this._value, diagnostics, cancellationToken);
            this._slot = null;
            this._scheduler = null;
            this._disposed = true;
        }

        /// <summary>
        /// Completes the submission without cancellation and releases its slot
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this.Wait(new VulkanVisionDiagnostics(), CancellationToken.None);
        }

        #endregion
    }
}
