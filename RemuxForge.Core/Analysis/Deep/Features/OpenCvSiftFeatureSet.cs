using OpenCvSharp;
using System;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Insieme di keypoint e descriptor SIFT estratti con il backend OpenCvSharp
    /// </summary>
    public sealed class OpenCvSiftFeatureSet : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Matrice OpenCV contenente i descriptor SIFT posseduta dall'istanza
        /// </summary>
        private Mat _descriptors;

        /// <summary>
        /// Indica se la matrice dei descriptor è già stata rilasciata
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza un insieme di feature SIFT associato a un backend e alle dimensioni del frame
        /// </summary>
        /// <param name="backendName">Identificativo del backend che ha estratto le feature</param>
        /// <param name="width">Larghezza del frame associato alle feature</param>
        /// <param name="height">Altezza del frame associato alle feature</param>
        /// <param name="keypoints">Keypoint SIFT estratti dal frame</param>
        /// <param name="descriptors">Matrice OpenCV contenente i descriptor SIFT</param>
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
        /// Rilascia la matrice OpenCV dei descriptor e le risorse native associate
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
        /// Identificativo del backend che ha estratto le feature
        /// </summary>
        public string BackendName { get; private set; }

        /// <summary>
        /// Larghezza del frame associato alle feature
        /// </summary>
        public int Width { get; private set; }

        /// <summary>
        /// Altezza del frame associato alle feature
        /// </summary>
        public int Height { get; private set; }

        /// <summary>
        /// Numero di keypoint SIFT disponibili
        /// </summary>
        public int KeypointCount { get { return this.Keypoints.Length; } }

        /// <summary>
        /// Array dei keypoint SIFT estratti, accessibile ai componenti interni del backend
        /// </summary>
        internal KeyPoint[] Keypoints { get; private set; }

        /// <summary>
        /// Matrice OpenCV dei descriptor SIFT, accessibile ai componenti interni del backend
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
