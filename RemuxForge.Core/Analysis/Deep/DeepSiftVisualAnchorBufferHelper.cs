using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Fornisce l'operazione per rilasciare i buffer dei frame trattenuti dalle ancore visuali SIFT
    /// </summary>
    internal static class DeepSiftVisualAnchorBufferHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Rilascia i buffer dei frame delle ancore senza alterarne PTS, geometria o metadati
        /// </summary>
        /// <param name="anchors">Ancore i cui buffer dei frame non sono più necessari</param>
        public static void ReleaseFrames(IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            if (anchors == null)
                return;

            // Le ancore restano disponibili per PTS, geometria e metadati, ma non devono più trattenere i dati immagine
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
                anchors[anchorIndex].Frame = Array.Empty<byte>();
        }

        #endregion
    }
}
