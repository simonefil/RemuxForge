using System;

namespace RemuxForge.Vulkan
{
    /// <summary>Base exception for controlled failures reported by the Vulkan vision library</summary>
    public abstract class VulkanVisionException : Exception
    {
        /// <summary>Initializes a new exception with the specified message</summary>
        /// <param name="message">Message that describes the failure</param>
        protected VulkanVisionException(string message) : base(message)
        {
        }

        /// <summary>Initializes a new exception with a message and its original cause</summary>
        /// <param name="message">Message that describes the failure</param>
        /// <param name="innerException">Exception that caused the failure</param>
        protected VulkanVisionException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>Indicates that no usable Vulkan backend is available</summary>
    public sealed class VulkanBackendUnavailableException : VulkanVisionException
    {
        /// <summary>Initializes a new exception with the specified message</summary>
        /// <param name="message">Message that describes the backend availability failure</param>
        public VulkanBackendUnavailableException(string message) : base(message)
        {
        }

        /// <summary>Initializes a new exception with a message and its original cause</summary>
        /// <param name="message">Message that describes the backend availability failure</param>
        /// <param name="innerException">Exception that caused the backend availability failure</param>
        public VulkanBackendUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>Indicates that the selected device does not expose a required capability</summary>
    public sealed class VulkanCapabilityUnsupportedException : VulkanVisionException
    {
        /// <summary>Initializes a new exception with the specified message</summary>
        /// <param name="message">Message that describes the unsupported capability</param>
        public VulkanCapabilityUnsupportedException(string message) : base(message)
        {
        }
    }

    /// <summary>Indicates that the available memory budget cannot satisfy the workload</summary>
    public sealed class VulkanResourceExhaustedException : VulkanVisionException
    {
        /// <summary>Initializes a new exception with the specified message</summary>
        /// <param name="message">Message that describes the resource exhaustion</param>
        public VulkanResourceExhaustedException(string message) : base(message)
        {
        }
    }

    /// <summary>Indicates an incompatibility between a shader, its manifest and the host ABI</summary>
    public sealed class VulkanShaderIncompatibleException : VulkanVisionException
    {
        /// <summary>Initializes a new exception with the specified message</summary>
        /// <param name="message">Message that describes the shader incompatibility</param>
        public VulkanShaderIncompatibleException(string message) : base(message)
        {
        }

        /// <summary>Initializes a new exception with a message and its original cause</summary>
        /// <param name="message">Message that describes the shader incompatibility</param>
        /// <param name="innerException">Exception that caused the shader incompatibility failure</param>
        public VulkanShaderIncompatibleException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>Indicates that the device was lost or produced a structurally incomplete GPU result</summary>
    public sealed class VulkanDeviceLostException : VulkanVisionException
    {
        /// <summary>Initializes a new exception with the specified message</summary>
        /// <param name="message">Message that describes the device or GPU-result failure</param>
        public VulkanDeviceLostException(string message) : base(message)
        {
        }

        /// <summary>Initializes a new exception with a message and its original cause</summary>
        /// <param name="message">Message that describes the device or GPU-result failure</param>
        /// <param name="innerException">Exception that caused the device or GPU-result failure</param>
        public VulkanDeviceLostException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
