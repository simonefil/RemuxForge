using System;
using System.Net.Http.Headers;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Attesa prima di ripetere una richiesta HTTP: quella chiesta dal servizio con Retry-After entro un tetto, altrimenti un
    /// backoff che raddoppia a ogni ritentativo
    /// </summary>
    public static class HttpRetryDelay
    {
        #region Metodi pubblici

        /// <summary>
        /// Legge l'attesa chiesta dall'header Retry-After, in secondi o come data
        /// </summary>
        /// <param name="header">Header della risposta, anche null</param>
        /// <returns>Attesa, zero per una data già passata, null se l'header manca</returns>
        public static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue header)
        {
            if (header != null && header.Delta.HasValue)
                return header.Delta.Value;

            if (header != null && header.Date.HasValue)
                return header.Date.Value > DateTimeOffset.UtcNow ? header.Date.Value - DateTimeOffset.UtcNow : TimeSpan.Zero;

            return null;
        }

        /// <summary>
        /// Calcola l'attesa prima di un ritentativo: Retry-After positivo entro il suo tetto, altrimenti il backoff con variazione
        /// casuale
        /// </summary>
        /// <param name="retry">Ritentativi già fatti, da 0</param>
        /// <param name="retryAfter">Attesa chiesta dal servizio, null se non indicata</param>
        /// <param name="baseDelay">Attesa del primo ritentativo</param>
        /// <param name="maxDelay">Tetto dell'attesa calcolata col backoff</param>
        /// <param name="maxRetryAfter">Tetto dell'attesa chiesta dal servizio</param>
        /// <param name="jitterFraction">Variazione casuale massima del backoff, in frazione dell'attesa; zero per nessuna</param>
        /// <returns>Attesa</returns>
        public static TimeSpan Compute(int retry, TimeSpan? retryAfter, TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan maxRetryAfter, double jitterFraction)
        {
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
                return retryAfter.Value < maxRetryAfter ? retryAfter.Value : maxRetryAfter;

            double backoffMs = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, retry));
            double factor = 1.0 - jitterFraction + Random.Shared.NextDouble() * 2.0 * jitterFraction;
            return TimeSpan.FromMilliseconds(backoffMs * factor);
        }

        #endregion
    }
}
