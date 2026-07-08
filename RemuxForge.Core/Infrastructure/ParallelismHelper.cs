using System;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Policy condivise per il parallelismo operativo
    /// </summary>
    public static class ParallelismHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Calcola il numero massimo di worker lasciando liberi due thread per sistema, UI e processi esterni
        /// </summary>
        /// <returns>Numero massimo di worker paralleli</returns>
        public static int ResolveDefaultMaxDegree()
        {
            int result = Environment.ProcessorCount - 2;

            if (result < 1)
                result = 1;

            return result;
        }

        #endregion
    }
}
