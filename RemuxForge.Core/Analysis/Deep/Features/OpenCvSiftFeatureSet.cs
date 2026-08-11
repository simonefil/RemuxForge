using OpenCvSharp;
using System;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Feature SIFT e descriptor posseduti dal backend OpenCvSharp
    /// </summary>
    public sealed class OpenCvSiftFeatureSet : IDisposable
    {
        #region Variabili di classe

        private Mat _descriptors;
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public OpenCvSiftFeatureSet(string backendName, int width, int height, KeyPoint[] keypoints, Mat descriptors)
        {
            this.BackendName = backendName;
            this.Width = width;
            this.Height = height;
            this.Keypoints = keypoints ?? new KeyPoint[0];
            this._descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
            this._disposed = false;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rilascia la memoria nativa dei descriptor
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;

            this._descriptors.Dispose();
            this._disposed = true;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo backend
        /// </summary>
        public string BackendName { get; private set; }

        /// <summary>
        /// Larghezza frame
        /// </summary>
        public int Width { get; private set; }

        /// <summary>
        /// Altezza frame
        /// </summary>
        public int Height { get; private set; }

        /// <summary>
        /// Numero keypoint
        /// </summary>
        public int KeypointCount { get { return this.Keypoints.Length; } }

        /// <summary>
        /// Keypoint OpenCV interni al backend
        /// </summary>
        internal KeyPoint[] Keypoints { get; private set; }

        /// <summary>
        /// Descriptor OpenCV interni al backend
        /// </summary>
        internal Mat Descriptors
        {
            get
            {
                if (this._disposed)
                    throw new ObjectDisposedException(nameof(OpenCvSiftFeatureSet));

                return this._descriptors;
            }
        }
        #endregion
    }
}
