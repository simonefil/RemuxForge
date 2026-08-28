using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Pipelines;
using RemuxForge.Vulkan.Scheduling;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace RemuxForge.Vulkan.Runtime
{
    /// <summary>
    /// Owns the Vulkan loader, instance, device, queue and runtime-wide shared objects
    /// </summary>
    internal sealed unsafe class VulkanRuntimeContext : IDisposable
    {
        #region Costanti

        /// <summary>
        /// Size in bytes of the private pipeline-cache envelope
        /// </summary>
        private const int PIPELINE_CACHE_HEADER_SIZE = 72;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Vulkan instance owned by this runtime context
        /// </summary>
        private VkInstance _instance;

        /// <summary>
        /// Function table used to access instance-level Vulkan commands
        /// </summary>
        private VkInstanceApi _instanceApi;

        /// <summary>
        /// Physical device selected during initialization
        /// </summary>
        private VkPhysicalDevice _physicalDevice;

        /// <summary>
        /// Logical device owned by this runtime context
        /// </summary>
        private VkDevice _device;

        /// <summary>
        /// Function table used to access device-level Vulkan commands
        /// </summary>
        private VkDeviceApi _deviceApi;

        /// <summary>
        /// Compute queue obtained from the selected queue family
        /// </summary>
        private VkQueue _queue;

        /// <summary>
        /// Index of the queue family from which <see cref="_queue"/> was obtained
        /// </summary>
        private uint _queueFamilyIndex;

        /// <summary>
        /// Timeline semaphore shared by the work scheduler for submission completion
        /// </summary>
        private VkSemaphore _timelineSemaphore;

        /// <summary>
        /// Pipeline cache owned by the logical device
        /// </summary>
        private VkPipelineCache _pipelineCache;

        /// <summary>
        /// Validation messenger registered with the Vulkan instance when validation is enabled
        /// </summary>
        private VkDebugUtilsMessengerEXT _debugMessenger;

        /// <summary>
        /// GC handle that keeps this context alive while Vulkan invokes the validation callback
        /// </summary>
        private GCHandle _debugUserData;

        /// <summary>
        /// Thread-safe queue receiving validation messages from the unmanaged callback
        /// </summary>
        private readonly ConcurrentQueue<ValidationMessage> _validationMessages = new ConcurrentQueue<ValidationMessage>();

        /// <summary>
        /// Indicates that disposal has completed or failed and will not be retried
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Initializes the Vulkan loader, instance, device and shared runtime resources
        /// </summary>
        /// <param name="options">Options controlling device selection, memory, validation and pipeline-cache loading</param>
        public VulkanRuntimeContext(VulkanVisionOptions options)
        {
            long initializationStart = Stopwatch.GetTimestamp();
            this.Options = options;
            this.InitializeLoader();
            try
            {
                this.CreateInstance();
                this.SelectPhysicalDevice(options.DeviceIndex);
                this.CreateDevice();
                this.CreateTimelineSemaphore();
                this.ShaderLoader = new VulkanShaderResourceLoader();
                this.CreatePipelineCache(options.InitialPipelineCache);
                this.PipelineLibrary = new VulkanComputePipelineLibrary(this);
                this.Allocator = new VulkanMemoryAllocator(this, options.MaximumVramBytes);
                this.Capabilities.MemoryPressureThresholdBytes = this.Allocator.GetStatistics().PressureThreshold;
                this.ResourcePool = new VulkanResourcePool(this.Allocator);
                this.Scheduler = new VulkanWorkScheduler(this, options.MaximumInFlightWorkloads);
                this.InitializationTicks = Stopwatch.GetTimestamp() - initializationStart;
            }
            catch
            {
                this.Dispose();
                throw;
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Exports the pipeline cache together with the identity data required for compatibility validation
        /// </summary>
        /// <returns>An opaque cache envelope that can be supplied to a later runtime initialization</returns>
        public byte[] GetPipelineCacheData()
        {
            this.ThrowIfDisposed();
            nuint size = 0;
            this._deviceApi.vkGetPipelineCacheData(this._pipelineCache, &size, null).CheckResult();
            if (size == 0)
                return this.WrapPipelineCache(Array.Empty<byte>());
            byte[] data = new byte[size];
            fixed (byte* pointer = data)
                this._deviceApi.vkGetPipelineCacheData(this._pipelineCache, &size, pointer).CheckResult();
            if ((nuint)data.Length != size)
                Array.Resize(ref data, checked((int)size));
            return this.WrapPipelineCache(data);
        }

        /// <summary>
        /// Releases all Vulkan and managed resources owned by this context
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            try
            {
                if (this._deviceApi != null && this._device.IsNotNull)
                    this._deviceApi.vkDeviceWaitIdle();
                this.Scheduler?.Dispose();
                this.ResourcePool?.Dispose();
                this.Allocator?.Dispose();
                this.PipelineLibrary?.Dispose();
                if (this._deviceApi != null && this._pipelineCache.IsNotNull)
                    this._deviceApi.vkDestroyPipelineCache(this._pipelineCache);
                if (this._deviceApi != null && this._timelineSemaphore.IsNotNull)
                    this._deviceApi.vkDestroySemaphore(this._timelineSemaphore);
                if (this._deviceApi != null && this._device.IsNotNull)
                    this._deviceApi.vkDestroyDevice();
                if (this._instanceApi != null && this._debugMessenger.IsNotNull)
                    this._instanceApi.vkDestroyDebugUtilsMessengerEXT(this._debugMessenger);
                if (this._debugUserData.IsAllocated)
                    this._debugUserData.Free();
                if (this._instanceApi != null && this._instance.IsNotNull)
                    this._instanceApi.vkDestroyInstance();
            }
            finally
            {
                this._disposed = true;
            }
        }

        #endregion

        #region Metodi internal

        /// <summary>
        /// Transfers queued validation messages into workload diagnostics
        /// </summary>
        /// <param name="diagnostics">Diagnostics object that receives messages and severity counters</param>
        internal void DrainValidationMessages(VulkanVisionDiagnostics diagnostics)
        {
            while (this._validationMessages.TryDequeue(out ValidationMessage message))
            {
                diagnostics.ValidationMessages.Add(message.Text);
                if ((message.Severity & VkDebugUtilsMessageSeverityFlagsEXT.Error) != 0)
                    diagnostics.ValidationErrorCount++;
                else if ((message.Severity & VkDebugUtilsMessageSeverityFlagsEXT.Warning) != 0)
                    diagnostics.ValidationWarningCount++;
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Initializes the Vulkan loader and resolves the MoltenVK fallback on macOS
        /// </summary>
        private void InitializeLoader()
        {
            VkResult result;
            string configuredLibrary = Environment.GetEnvironmentVariable("REMUXFORGE_VULKAN_LIBRARY");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_ICD_FILENAMES")))
            {
                string[] candidates = new string[] { "/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json", "/usr/local/etc/vulkan/icd.d/MoltenVK_icd.json" };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i]))
                    {
                        Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", candidates[i]);
                        break;
                    }
                }
            }
            result = string.IsNullOrEmpty(configuredLibrary) ? vkInitialize() : vkInitialize(configuredLibrary);
            if (result != VkResult.Success && string.IsNullOrEmpty(configuredLibrary) && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string[] candidates = new string[]
                {
                    "/opt/homebrew/lib/libMoltenVK.dylib",
                    "/usr/local/lib/libMoltenVK.dylib",
                    "/opt/homebrew/lib/libvulkan.1.dylib",
                    "/usr/local/lib/libvulkan.1.dylib"
                };
                for (int i = 0; i < candidates.Length && result != VkResult.Success; i++)
                {
                    if (File.Exists(candidates[i]))
                        result = vkInitialize(candidates[i]);
                }
            }
            if (result != VkResult.Success)
                throw new VulkanBackendUnavailableException("Vulkan loader initialization failed: " + result);
        }

        /// <summary>
        /// Creates the Vulkan instance, portability extensions and validation messenger
        /// </summary>
        private void CreateInstance()
        {
            List<VkUtf8String> extensions = new List<VkUtf8String>();
            bool portability = this.HasInstanceExtension(VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME);
            if (portability)
                extensions.Add(VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME);
            bool validation = this.Options.EnableValidation && this.HasInstanceLayer(VK_LAYER_KHRONOS_VALIDATION_EXTENSION_NAME);
            bool debugUtils = validation && this.HasInstanceExtension(VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
            if (debugUtils)
                extensions.Add(VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
            List<VkUtf8String> layers = new List<VkUtf8String>();
            if (validation)
                layers.Add(VK_LAYER_KHRONOS_VALIDATION_EXTENSION_NAME);
            using (VkStringArray extensionNames = new VkStringArray(extensions))
            using (VkStringArray layerNames = new VkStringArray(layers))
            {
                VkUtf8ReadOnlyString applicationName = "RemuxForge"u8;
                VkApplicationInfo applicationInfo = new VkApplicationInfo
                {
                    pApplicationName = applicationName,
                    pEngineName = applicationName,
                    apiVersion = VkVersion.Version_1_2
                };
                VkInstanceCreateInfo createInfo = new VkInstanceCreateInfo
                {
                    flags = portability ? VkInstanceCreateFlags.EnumeratePortabilityKHR : VkInstanceCreateFlags.None,
                    pApplicationInfo = &applicationInfo,
                    enabledExtensionCount = extensionNames.Length,
                    ppEnabledExtensionNames = extensionNames,
                    enabledLayerCount = layerNames.Length,
                    ppEnabledLayerNames = layerNames
                };
                VkResult result = vkCreateInstance(&createInfo, out this._instance);
                if (result != VkResult.Success)
                    throw new VulkanBackendUnavailableException("Vulkan 1.2 instance creation failed: " + result);
            }
            this._instanceApi = GetApi(this._instance);
            if (debugUtils)
            {
                this._debugUserData = GCHandle.Alloc(this);
                VkDebugUtilsMessengerCreateInfoEXT debugInfo = this.CreateDebugMessengerInfo();
                this._instanceApi.vkCreateDebugUtilsMessengerEXT(&debugInfo, null, out this._debugMessenger).CheckResult();
            }
        }

        /// <summary>
        /// Selects a compatible physical device deterministically
        /// </summary>
        /// <param name="requestedIndex">Original Vulkan enumeration index to force, or a negative value to use the ranked candidate</param>
        private void SelectPhysicalDevice(int requestedIndex)
        {
            uint count = 0;
            this._instanceApi.vkEnumeratePhysicalDevices(&count, null).CheckResult();
            if (count == 0)
                throw new VulkanBackendUnavailableException("No Vulkan device is available.");
            VkPhysicalDevice[] devices = new VkPhysicalDevice[count];
            this._instanceApi.vkEnumeratePhysicalDevices(devices).CheckResult();
            List<DeviceCandidate> candidates = new List<DeviceCandidate>();
            for (int i = 0; i < devices.Length; i++)
            {
                if (!this.TryFindComputeQueue(devices[i], out uint queueFamilyIndex))
                    continue;
                VulkanDeviceCapabilities capabilities = this.Probe(devices[i], i, queueFamilyIndex);
                if (capabilities.Tier != VulkanCapabilityTier.Unsupported)
                    candidates.Add(new DeviceCandidate(devices[i], capabilities));
            }
            if (candidates.Count == 0)
                throw new VulkanCapabilityUnsupportedException("No device satisfies the Vulkan Base tier.");
            candidates.Sort((left, right) => CompareCandidates(left.Capabilities, right.Capabilities));
            DeviceCandidate selected;
            if (requestedIndex >= 0)
            {
                selected = candidates.Find(candidate => candidate.Capabilities.EnumerationIndex == requestedIndex);
                if (selected == null)
                    throw new VulkanCapabilityUnsupportedException("The requested Vulkan device does not satisfy the Base tier.");
            }
            else
            {
                selected = candidates[0];
            }
            this._physicalDevice = selected.Device;
            this._queueFamilyIndex = selected.Capabilities.ComputeQueueFamilyIndex;
            this.Capabilities = selected.Capabilities;
        }

        /// <summary>
        /// Probes the capabilities, limits and identity of a physical device
        /// </summary>
        /// <param name="device">Physical device to inspect</param>
        /// <param name="index">Original index returned by physical-device enumeration</param>
        /// <param name="queueFamilyIndex">Compute queue-family index selected for the device</param>
        /// <returns>A capability snapshot used for device ranking and runtime configuration</returns>
        private VulkanDeviceCapabilities Probe(VkPhysicalDevice device, int index, uint queueFamilyIndex)
        {
            this._instanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties properties);
            VkPhysicalDeviceShaderIntegerDotProductProperties integerDotProperties = new VkPhysicalDeviceShaderIntegerDotProductProperties();
            VkPhysicalDeviceSubgroupProperties subgroupProperties = new VkPhysicalDeviceSubgroupProperties();
            subgroupProperties.pNext = &integerDotProperties;
            VkPhysicalDeviceProperties2 properties2 = new VkPhysicalDeviceProperties2 { pNext = &subgroupProperties };
            this._instanceApi.vkGetPhysicalDeviceProperties2(device, &properties2);
            VkPhysicalDeviceShaderIntegerDotProductFeatures integerDotFeatures = new VkPhysicalDeviceShaderIntegerDotProductFeatures();
            VkPhysicalDeviceCooperativeMatrixFeaturesKHR cooperativeMatrixFeatures = new VkPhysicalDeviceCooperativeMatrixFeaturesKHR();
            VkPhysicalDeviceVulkan12Features features12 = new VkPhysicalDeviceVulkan12Features { pNext = &integerDotFeatures };
            integerDotFeatures.pNext = &cooperativeMatrixFeatures;
            VkPhysicalDeviceFeatures2 features2 = new VkPhysicalDeviceFeatures2 { pNext = &features12 };
            this._instanceApi.vkGetPhysicalDeviceFeatures2(device, &features2);
            uint cooperativeMatrixPropertyCount = 0;
            bool cooperativeMatrix = cooperativeMatrixFeatures.cooperativeMatrix && features12.storageBuffer8BitAccess && (properties.apiVersion >= VkVersion.Version_1_4 || this.HasDeviceExtension(device, VK_KHR_COOPERATIVE_MATRIX_EXTENSION_NAME));
            uint cooperativeMatrixMSize = 0;
            uint cooperativeMatrixNSize = 0;
            uint cooperativeMatrixKSize = 0;
            if (cooperativeMatrix)
            {
                this._instanceApi.vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR(device, &cooperativeMatrixPropertyCount, null).CheckResult();
                VkCooperativeMatrixPropertiesKHR* cooperativeMatrixProperties = stackalloc VkCooperativeMatrixPropertiesKHR[(int)cooperativeMatrixPropertyCount];
                for (int propertyIndex = 0; propertyIndex < cooperativeMatrixPropertyCount; propertyIndex++)
                    cooperativeMatrixProperties[propertyIndex] = new VkCooperativeMatrixPropertiesKHR();
                this._instanceApi.vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR(device, &cooperativeMatrixPropertyCount, cooperativeMatrixProperties).CheckResult();
                for (int propertyIndex = 0; propertyIndex < cooperativeMatrixPropertyCount; propertyIndex++)
                {
                    VkCooperativeMatrixPropertiesKHR property = cooperativeMatrixProperties[propertyIndex];
                    if (property.scope == VkScopeKHR.Subgroup && property.AType == VkComponentTypeKHR.Uint8 && property.BType == VkComponentTypeKHR.Uint8 && property.CType == VkComponentTypeKHR.Uint32 && property.ResultType == VkComponentTypeKHR.Uint32 && !property.saturatingAccumulation)
                    {
                        cooperativeMatrixMSize = property.MSize;
                        cooperativeMatrixNSize = property.NSize;
                        cooperativeMatrixKSize = property.KSize;
                        break;
                    }
                }
                cooperativeMatrix = cooperativeMatrixMSize > 0 && cooperativeMatrixNSize > 0 && cooperativeMatrixKSize > 0;
            }
            byte* name = properties.deviceName;
            bool timeline = properties.apiVersion >= VkVersion.Version_1_2;
            bool subgroupBallot = (subgroupProperties.supportedStages & VkShaderStageFlags.Compute) != 0 && (subgroupProperties.supportedOperations & VkSubgroupFeatureFlags.Ballot) != 0;
            byte[] pipelineCacheUuid = new byte[16];
            byte* uuid = properties.pipelineCacheUUID;
            Marshal.Copy((IntPtr)uuid, pipelineCacheUuid, 0, pipelineCacheUuid.Length);
            VulkanDeviceCapabilities result = new VulkanDeviceCapabilities
            {
                EnumerationIndex = index,
                DeviceName = Marshal.PtrToStringUTF8((IntPtr)name) ?? "Vulkan GPU",
                ApiVersion = FormatApiVersion(properties.apiVersion),
                DriverVersion = properties.driverVersion.ToString(),
                DriverVersionRaw = properties.driverVersion,
                VendorId = properties.vendorID,
                DeviceId = properties.deviceID,
                DeviceType = (uint)properties.deviceType,
                ComputeQueueFamilyIndex = queueFamilyIndex,
                MaximumStorageBufferRange = properties.limits.maxStorageBufferRange,
                MinimumStorageBufferOffsetAlignment = properties.limits.minStorageBufferOffsetAlignment,
                MaximumComputeWorkGroupInvocations = properties.limits.maxComputeWorkGroupInvocations,
                MaximumComputeSharedMemorySize = properties.limits.maxComputeSharedMemorySize,
                MaximumComputeWorkGroupCountX = properties.limits.maxComputeWorkGroupCount[0],
                MaximumComputeWorkGroupCountY = properties.limits.maxComputeWorkGroupCount[1],
                SubgroupSize = subgroupProperties.subgroupSize,
                SubgroupBallot = subgroupBallot,
                IntegerDotProduct = integerDotFeatures.shaderIntegerDotProduct && integerDotProperties.integerDotProduct4x8BitPackedUnsignedAccelerated,
                CooperativeMatrix = cooperativeMatrix,
                CooperativeMatrixMSize = cooperativeMatrixMSize,
                CooperativeMatrixNSize = cooperativeMatrixNSize,
                CooperativeMatrixKSize = cooperativeMatrixKSize,
                TimelineSemaphore = timeline,
                PortabilitySubset = this.HasDeviceExtension(device, VK_KHR_PORTABILITY_SUBSET_EXTENSION_NAME),
                MemoryBudget = this.HasDeviceExtension(device, VK_EXT_MEMORY_BUDGET_EXTENSION_NAME),
                TimestampQueries = properties.limits.timestampComputeAndGraphics,
                TimestampPeriodNanoseconds = properties.limits.timestampPeriod,
                PipelineCacheUuid = pipelineCacheUuid,
                Tier = !timeline ? VulkanCapabilityTier.Unsupported : subgroupBallot ? VulkanCapabilityTier.Subgroup : VulkanCapabilityTier.Base
            };
            return result;
        }

        /// <summary>
        /// Creates the logical device and compute queue for the selected capabilities
        /// </summary>
        private void CreateDevice()
        {
            float priority = 1.0f;
            VkDeviceQueueCreateInfo queueInfo = new VkDeviceQueueCreateInfo { queueFamilyIndex = this._queueFamilyIndex, queueCount = 1, pQueuePriorities = &priority };
            List<VkUtf8String> extensions = new List<VkUtf8String>();
            if (this.Capabilities.PortabilitySubset)
                extensions.Add(VK_KHR_PORTABILITY_SUBSET_EXTENSION_NAME);
            VkPhysicalDeviceShaderIntegerDotProductFeatures integerDotFeatures = new VkPhysicalDeviceShaderIntegerDotProductFeatures { shaderIntegerDotProduct = this.Capabilities.IntegerDotProduct };
            VkPhysicalDeviceCooperativeMatrixFeaturesKHR cooperativeMatrixFeatures = new VkPhysicalDeviceCooperativeMatrixFeaturesKHR { cooperativeMatrix = this.Capabilities.CooperativeMatrix };
            VkPhysicalDeviceVulkan12Features features12 = new VkPhysicalDeviceVulkan12Features { timelineSemaphore = true, storageBuffer8BitAccess = this.Capabilities.CooperativeMatrix };
            if (this.Capabilities.IntegerDotProduct)
            {
                extensions.Add(VK_KHR_SHADER_INTEGER_DOT_PRODUCT_EXTENSION_NAME);
                features12.pNext = &integerDotFeatures;
            }
            if (this.Capabilities.CooperativeMatrix)
            {
                if (this.HasDeviceExtension(this._physicalDevice, VK_KHR_COOPERATIVE_MATRIX_EXTENSION_NAME))
                    extensions.Add(VK_KHR_COOPERATIVE_MATRIX_EXTENSION_NAME);
                if (this.Capabilities.IntegerDotProduct)
                    integerDotFeatures.pNext = &cooperativeMatrixFeatures;
                else
                    features12.pNext = &cooperativeMatrixFeatures;
            }
            using (VkStringArray extensionNames = new VkStringArray(extensions))
            {
                VkDeviceCreateInfo createInfo = new VkDeviceCreateInfo
                {
                    pNext = &features12,
                    queueCreateInfoCount = 1,
                    pQueueCreateInfos = &queueInfo,
                    enabledExtensionCount = extensionNames.Length,
                    ppEnabledExtensionNames = extensionNames
                };
                this._instanceApi.vkCreateDevice(this._physicalDevice, &createInfo, null, out this._device).CheckResult();
            }
            this._deviceApi = GetApi(this._instance, this._device);
            this._deviceApi.vkGetDeviceQueue(this._queueFamilyIndex, 0, out this._queue);
        }

        /// <summary>
        /// Creates the timeline semaphore shared by the scheduler
        /// </summary>
        private void CreateTimelineSemaphore()
        {
            VkSemaphoreTypeCreateInfo typeInfo = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
            VkSemaphoreCreateInfo createInfo = new VkSemaphoreCreateInfo { pNext = &typeInfo };
            this._deviceApi.vkCreateSemaphore(&createInfo, null, out this._timelineSemaphore).CheckResult();
        }

        /// <summary>
        /// Initializes the pipeline cache from optional compatible data
        /// </summary>
        /// <param name="initialData">Previously exported cache envelope, or <see langword="null"/> when no cache is available</param>
        private void CreatePipelineCache(byte[] initialData)
        {
            VkPipelineCacheCreateInfo createInfo = new VkPipelineCacheCreateInfo();
            byte[] payload = this.UnwrapPipelineCache(initialData);
            if (payload.Length == 0)
            {
                this._deviceApi.vkCreatePipelineCache(&createInfo, null, out this._pipelineCache).CheckResult();
                return;
            }
            fixed (byte* pointer = payload)
            {
                createInfo.initialDataSize = (nuint)payload.Length;
                createInfo.pInitialData = pointer;
                VkResult result = this._deviceApi.vkCreatePipelineCache(&createInfo, null, out this._pipelineCache);
                if (result != VkResult.Success)
                {
                    createInfo.initialDataSize = 0;
                    createInfo.pInitialData = null;
                    this._deviceApi.vkCreatePipelineCache(&createInfo, null, out this._pipelineCache).CheckResult();
                }
            }
        }

        /// <summary>
        /// Adds device and shader identity data to a pipeline-cache payload
        /// </summary>
        /// <param name="payload">Raw Vulkan pipeline-cache data returned by the driver</param>
        /// <returns>A private envelope containing the compatibility header and the unchanged driver payload</returns>
        private byte[] WrapPipelineCache(byte[] payload)
        {
            byte[] result = new byte[checked(PIPELINE_CACHE_HEADER_SIZE + payload.Length)];
            result[0] = (byte)'R'; result[1] = (byte)'F'; result[2] = (byte)'V'; result[3] = (byte)'K';
            result[4] = (byte)'P'; result[5] = (byte)'C'; result[6] = 0; result[7] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), this.Capabilities.VendorId);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), this.Capabilities.DeviceId);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), this.Capabilities.DriverVersionRaw);
            this.Capabilities.PipelineCacheUuid.Span.CopyTo(result.AsSpan(20, 16));
            this.ShaderLoader.ManifestHash.CopyTo(result, 36);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(68, 4), payload.Length);
            payload.CopyTo(result, PIPELINE_CACHE_HEADER_SIZE);
            return result;
        }

        /// <summary>
        /// Extracts a driver pipeline-cache payload when the persisted envelope matches this runtime
        /// </summary>
        /// <param name="data">Persisted pipeline-cache envelope to validate</param>
        /// <returns>The raw driver payload, or an empty array when the envelope is invalid or incompatible</returns>
        private byte[] UnwrapPipelineCache(byte[] data)
        {
            if (data == null || data.Length < PIPELINE_CACHE_HEADER_SIZE)
                return Array.Empty<byte>();
            if (data[0] != (byte)'R' || data[1] != (byte)'F' || data[2] != (byte)'V' || data[3] != (byte)'K' || data[4] != (byte)'P' || data[5] != (byte)'C' || data[6] != 0 || data[7] != 0)
                return Array.Empty<byte>();
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)) != this.Capabilities.VendorId || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)) != this.Capabilities.DeviceId || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4)) != this.Capabilities.DriverVersionRaw)
                return Array.Empty<byte>();
            if (!data.AsSpan(20, 16).SequenceEqual(this.Capabilities.PipelineCacheUuid.Span) || !data.AsSpan(36, 32).SequenceEqual(this.ShaderLoader.ManifestHash))
                return Array.Empty<byte>();
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(68, 4));
            if (payloadLength < 0 || data.Length != PIPELINE_CACHE_HEADER_SIZE + payloadLength)
                return Array.Empty<byte>();
            return data.AsSpan(PIPELINE_CACHE_HEADER_SIZE, payloadLength).ToArray();
        }

        /// <summary>
        /// Determines whether the Vulkan loader exposes a named instance extension
        /// </summary>
        /// <param name="name">UTF-8 extension name to locate</param>
        /// <returns><see langword="true"/> when the extension is advertised; otherwise, <see langword="false"/></returns>
        private bool HasInstanceExtension(VkUtf8ReadOnlyString name)
        {
            uint count = 0;
            vkEnumerateInstanceExtensionProperties(&count, null).CheckResult();
            VkExtensionProperties[] properties = new VkExtensionProperties[count];
            vkEnumerateInstanceExtensionProperties(properties).CheckResult();
            for (int i = 0; i < properties.Length; i++)
            {
                fixed (byte* current = properties[i].extensionName)
                {
                    if (name == current)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines whether the Vulkan loader exposes a named instance layer
        /// </summary>
        /// <param name="name">UTF-8 layer name to locate</param>
        /// <returns><see langword="true"/> when the layer is advertised; otherwise, <see langword="false"/></returns>
        private bool HasInstanceLayer(VkUtf8ReadOnlyString name)
        {
            uint count = 0;
            vkEnumerateInstanceLayerProperties(&count, null).CheckResult();
            VkLayerProperties[] properties = new VkLayerProperties[count];
            vkEnumerateInstanceLayerProperties(properties).CheckResult();
            for (int i = 0; i < properties.Length; i++)
            {
                fixed (byte* current = properties[i].layerName)
                {
                    if (name == current)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Builds the validation-messenger creation structure for this context
        /// </summary>
        /// <returns>A creation structure whose callback points to <see cref="DebugCallback"/></returns>
        private VkDebugUtilsMessengerCreateInfoEXT CreateDebugMessengerInfo()
        {
            return new VkDebugUtilsMessengerCreateInfoEXT
            {
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation | VkDebugUtilsMessageTypeFlagsEXT.Performance,
                pfnUserCallback = &DebugCallback,
                pUserData = (void*)GCHandle.ToIntPtr(this._debugUserData)
            };
        }

        /// <summary>
        /// Receives a Vulkan validation message and queues it for later diagnostic consumption
        /// </summary>
        /// <param name="severity">Severity flags assigned by the validation layer</param>
        /// <param name="messageType">Message-category flags assigned by the validation layer</param>
        /// <param name="callbackData">Native callback payload, which may be <see langword="null"/></param>
        /// <param name="userData">Pointer to the context handle supplied during messenger creation</param>
        /// <returns>Zero to allow Vulkan to continue processing the message</returns>
        [UnmanagedCallersOnly]
        private static uint DebugCallback(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT messageType, VkDebugUtilsMessengerCallbackDataEXT* callbackData, void* userData)
        {
            if (userData != null)
            {
                VulkanRuntimeContext runtime = GCHandle.FromIntPtr((IntPtr)userData).Target as VulkanRuntimeContext;
                if (runtime != null)
                {
                    string text = callbackData == null ? "Vulkan validation message has no payload." : Marshal.PtrToStringUTF8((IntPtr)callbackData->pMessage) ?? "Vulkan validation message is empty.";
                    runtime._validationMessages.Enqueue(new ValidationMessage(severity, messageType + ": " + text));
                }
            }
            return 0;
        }

        /// <summary>
        /// Determines whether the selected physical device exposes a named device extension
        /// </summary>
        /// <param name="device">Physical device whose extensions are inspected</param>
        /// <param name="name">UTF-8 extension name to locate</param>
        /// <returns><see langword="true"/> when the extension is advertised; otherwise, <see langword="false"/></returns>
        private bool HasDeviceExtension(VkPhysicalDevice device, VkUtf8ReadOnlyString name)
        {
            this._instanceApi.vkEnumerateDeviceExtensionProperties(device, out uint count).CheckResult();
            VkExtensionProperties[] properties = new VkExtensionProperties[count];
            this._instanceApi.vkEnumerateDeviceExtensionProperties(device, properties).CheckResult();
            for (int i = 0; i < properties.Length; i++)
            {
                fixed (byte* current = properties[i].extensionName)
                {
                    if (name == current)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Selects a compute-capable queue family, preferring one without graphics responsibilities
        /// </summary>
        /// <param name="device">Physical device whose queue families are inspected</param>
        /// <param name="queueFamilyIndex">Receives the preferred queue-family index, or zero when no compute family exists</param>
        /// <returns><see langword="true"/> when a compute-capable queue family is available; otherwise, <see langword="false"/></returns>
        private bool TryFindComputeQueue(VkPhysicalDevice device, out uint queueFamilyIndex)
        {
            uint count = 0;
            this._instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
            VkQueueFamilyProperties[] properties = new VkQueueFamilyProperties[count];
            this._instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, properties);
            int fallback = -1;
            for (int i = 0; i < properties.Length; i++)
            {
                if ((properties[i].queueFlags & VkQueueFlags.Compute) == 0)
                    continue;
                if ((properties[i].queueFlags & VkQueueFlags.Graphics) == 0)
                {
                    queueFamilyIndex = (uint)i;
                    return true;
                }
                fallback = i;
            }
            queueFamilyIndex = fallback >= 0 ? (uint)fallback : 0;
            return fallback >= 0;
        }

        /// <summary>
        /// Formats a Vulkan packed API version as major, minor and patch components
        /// </summary>
        /// <param name="version">Packed Vulkan version value</param>
        /// <returns>Version text in <c>major.minor.patch</c> form</returns>
        private static string FormatApiVersion(uint version)
        {
            uint major = (version >> 22) & 0x7Fu;
            uint minor = (version >> 12) & 0x3FFu;
            uint patch = version & 0xFFFu;
            return major.ToString() + "." + minor.ToString() + "." + patch.ToString();
        }

        /// <summary>
        /// Compares device candidates using the runtime's stable preference order
        /// </summary>
        /// <param name="left">First candidate to compare</param>
        /// <param name="right">Second candidate to compare</param>
        /// <returns>A negative value when <paramref name="left"/> precedes <paramref name="right"/>, zero when equal, or a positive value otherwise</returns>
        private static int CompareCandidates(VulkanDeviceCapabilities left, VulkanDeviceCapabilities right)
        {
            int leftScore = left.DeviceType == (uint)VkPhysicalDeviceType.DiscreteGpu ? 2 : left.DeviceType == (uint)VkPhysicalDeviceType.IntegratedGpu ? 1 : 0;
            int rightScore = right.DeviceType == (uint)VkPhysicalDeviceType.DiscreteGpu ? 2 : right.DeviceType == (uint)VkPhysicalDeviceType.IntegratedGpu ? 1 : 0;
            int score = rightScore.CompareTo(leftScore);
            if (score != 0)
                return score;
            score = left.VendorId.CompareTo(right.VendorId);
            if (score != 0)
                return score;
            score = left.DeviceId.CompareTo(right.DeviceId);
            if (score != 0)
                return score;
            return left.EnumerationIndex.CompareTo(right.EnumerationIndex);
        }

        /// <summary>
        /// Throws when the runtime context is no longer usable
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanRuntimeContext));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Options used to initialize this runtime context
        /// </summary>
        public VulkanVisionOptions Options { get; }

        /// <summary>
        /// Capabilities detected for the selected physical device
        /// </summary>
        public VulkanDeviceCapabilities Capabilities { get; private set; }

        /// <summary>
        /// Loader for embedded shader resources and their manifest hash
        /// </summary>
        public VulkanShaderResourceLoader ShaderLoader { get; private set; }

        /// <summary>
        /// Allocator that owns Vulkan device-memory blocks for this runtime
        /// </summary>
        public VulkanMemoryAllocator Allocator { get; private set; }

        /// <summary>
        /// Pool that owns reusable Vulkan resources allocated by the runtime
        /// </summary>
        public VulkanResourcePool ResourcePool { get; private set; }

        /// <summary>
        /// Scheduler that serializes and tracks compute submissions
        /// </summary>
        public VulkanWorkScheduler Scheduler { get; private set; }

        /// <summary>
        /// Library that owns cached compute pipelines for this device
        /// </summary>
        public VulkanComputePipelineLibrary PipelineLibrary { get; private set; }

        /// <summary>
        /// Host stopwatch ticks spent initializing the runtime
        /// </summary>
        public long InitializationTicks { get; private set; }

        /// <summary>
        /// Instance-level Vulkan command table used by dependent runtime components
        /// </summary>
        internal VkInstanceApi InstanceApi { get { return this._instanceApi; } }

        /// <summary>
        /// Selected physical device handle
        /// </summary>
        internal VkPhysicalDevice PhysicalDevice { get { return this._physicalDevice; } }

        /// <summary>
        /// Device-level Vulkan command table used by dependent runtime components
        /// </summary>
        internal VkDeviceApi DeviceApi { get { return this._deviceApi; } }

        /// <summary>
        /// Compute queue selected for runtime submissions
        /// </summary>
        internal VkQueue Queue { get { return this._queue; } }

        /// <summary>
        /// Queue-family index used by the compute queue
        /// </summary>
        internal uint QueueFamilyIndex { get { return this._queueFamilyIndex; } }

        /// <summary>
        /// Timeline semaphore used to track submitted work
        /// </summary>
        internal VkSemaphore TimelineSemaphore { get { return this._timelineSemaphore; } }

        /// <summary>
        /// Pipeline cache owned by the logical device
        /// </summary>
        internal VkPipelineCache PipelineCache { get { return this._pipelineCache; } }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Associates an enumerated physical device with its detected capabilities
        /// </summary>
        private sealed class DeviceCandidate
        {
            /// <summary>
            /// Initializes a device candidate
            /// </summary>
            /// <param name="device">Physical device represented by the candidate</param>
            /// <param name="capabilities">Capabilities detected for the physical device</param>
            public DeviceCandidate(VkPhysicalDevice device, VulkanDeviceCapabilities capabilities)
            {
                this.Device = device;
                this.Capabilities = capabilities;
            }

            /// <summary>
            /// Physical device represented by this candidate
            /// </summary>
            public VkPhysicalDevice Device { get; }

            /// <summary>
            /// Capability snapshot associated with <see cref="Device"/>
            /// </summary>
            public VulkanDeviceCapabilities Capabilities { get; }
        }

        /// <summary>
        /// Carries the text and severity of a validation-layer message
        /// </summary>
        private readonly struct ValidationMessage
        {
            /// <summary>
            /// Initializes a validation message value
            /// </summary>
            /// <param name="severity">Severity flags assigned by the validation layer</param>
            /// <param name="text">Formatted validation message text</param>
            public ValidationMessage(VkDebugUtilsMessageSeverityFlagsEXT severity, string text)
            {
                this.Severity = severity;
                this.Text = text;
            }

            /// <summary>
            /// Severity flags captured from the validation callback
            /// </summary>
            public VkDebugUtilsMessageSeverityFlagsEXT Severity { get; }

            /// <summary>
            /// Message text captured from the validation callback
            /// </summary>
            public string Text { get; }
        }

        #endregion
    }
}
