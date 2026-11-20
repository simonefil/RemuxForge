using RemuxForge.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Politica di retry delle richieste AI con backoff esponenziale, Retry-After e una pausa «non prima di» condivisa da tutte le
    /// richieste che usano la stessa istanza: un limite di frequenza ferma le richieste nuove e ritentate, non quelle in volo
    /// </summary>
    public class AiRetryPolicy
    {
        #region Costanti

        /// <summary>
        /// Tentativi massimi per richiesta della politica predefinita
        /// </summary>
        private const int DEFAULT_MAX_ATTEMPTS = 5;

        /// <summary>
        /// Variazione casuale massima del backoff, in frazione dell'attesa
        /// </summary>
        private const double JITTER_FRACTION = 0.2;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Tentativi massimi per richiesta
        /// </summary>
        private readonly int _maxAttempts;

        /// <summary>
        /// Attesa del primo ritentativo
        /// </summary>
        private readonly TimeSpan _baseDelay;

        /// <summary>
        /// Tetto dell'attesa calcolata col backoff
        /// </summary>
        private readonly TimeSpan _maxDelay;

        /// <summary>
        /// Tetto dell'attesa chiesta con Retry-After
        /// </summary>
        private readonly TimeSpan _maxRetryAfter;

        /// <summary>
        /// Protegge l'istante della pausa condivisa
        /// </summary>
        private readonly object _pauseLock;

        /// <summary>
        /// Istante UTC prima del quale nessuna richiesta parte
        /// </summary>
        private DateTime _notBeforeUtc;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="maxAttempts">Tentativi massimi per richiesta, almeno 1</param>
        /// <param name="baseDelay">Attesa del primo ritentativo</param>
        /// <param name="maxDelay">Tetto dell'attesa calcolata col backoff</param>
        /// <param name="maxRetryAfter">Tetto dell'attesa chiesta dal servizio con Retry-After</param>
        private AiRetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan maxRetryAfter)
        {
            this._maxAttempts = Math.Max(1, maxAttempts);
            this._baseDelay = baseDelay;
            this._maxDelay = maxDelay;
            this._maxRetryAfter = maxRetryAfter;
            this._pauseLock = new object();
            this._notBeforeUtc = DateTime.MinValue;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea la politica predefinita di un job: 5 tentativi, backoff da 2 s fino a 60 s, Retry-After fino a 5 minuti
        /// </summary>
        /// <returns>Politica con la sua pausa condivisa</returns>
        public static AiRetryPolicy CreateDefault()
        {
            return new AiRetryPolicy(DEFAULT_MAX_ATTEMPTS, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Esegue una richiesta ritentando gli errori transitori; prima di ogni tentativo attende la pausa condivisa
        /// </summary>
        /// <typeparam name="T">Tipo del risultato</typeparam>
        /// <param name="attempt">Tentativo da eseguire, riceve il numero del tentativo da 1</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultato del primo tentativo riuscito</returns>
        public async Task<T> ExecuteAsync<T>(Func<int, Task<T>> attempt, CancellationToken cancellationToken)
        {
            for (int number = 1; ; number++)
            {
                // L'istante può spostarsi in avanti mentre si aspetta: si ricontrolla a ogni risveglio
                TimeSpan pause = this.GetRemainingPause();
                while (pause > TimeSpan.Zero)
                {
                    await Task.Delay(pause, cancellationToken);
                    pause = this.GetRemainingPause();
                }

                try
                {
                    return await attempt(number);
                }
                catch (AiServiceException ex)
                {
                    bool rateLimit = ex.IsRateLimit;
                    if (rateLimit)
                    {
                        // Il limite di frequenza vale per l'accesso, anche all'ultimo tentativo: si fermano tutte le richieste che
                        // partono da qui in poi e la pausa si sposta solo in avanti
                        DateTime notBeforeUtc = DateTime.UtcNow + this.GetDelay(number, ex);
                        lock (this._pauseLock)
                        {
                            if (notBeforeUtc > this._notBeforeUtc)
                                this._notBeforeUtc = notBeforeUtc;
                        }
                    }

                    if (number >= this._maxAttempts || !ex.IsTransient)
                        throw;

                    if (!rateLimit)
                        await Task.Delay(this.GetDelay(number, ex), cancellationToken);
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola l'attesa prima del ritentativo: Retry-After positivo entro il suo tetto, altrimenti il backoff con variazione casuale
        /// </summary>
        /// <param name="attempt">Tentativo fallito, da 1</param>
        /// <param name="error">Errore del tentativo</param>
        /// <returns>Attesa</returns>
        private TimeSpan GetDelay(int attempt, AiServiceException error)
        {
            return HttpRetryDelay.Compute(attempt - 1, error.RetryAfter, this._baseDelay, this._maxDelay, this._maxRetryAfter, JITTER_FRACTION);
        }

        /// <summary>
        /// Restituisce il tempo che manca alla fine della pausa condivisa
        /// </summary>
        /// <returns>Attesa residua, zero o negativa se la pausa è finita</returns>
        private TimeSpan GetRemainingPause()
        {
            lock (this._pauseLock)
            {
                return this._notBeforeUtc - DateTime.UtcNow;
            }
        }

        #endregion
    }
}
