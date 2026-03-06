using System;

namespace MergeLanguageTracks
{
    public static class ConsoleHelper
    {
        #region Variabili statiche

        /// <summary>
        /// Callback per redirect output verso TUI o buffer
        /// </summary>
        private static Action<string, ConsoleColor> s_logCallback;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive una riga di testo in colore rosso.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteRed(string text)
        {
            WriteColored(text, ConsoleColor.Red);
        }

        /// <summary>
        /// Scrive una riga di testo in colore verde.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteGreen(string text)
        {
            WriteColored(text, ConsoleColor.Green);
        }

        /// <summary>
        /// Scrive una riga di testo in colore giallo.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteYellow(string text)
        {
            WriteColored(text, ConsoleColor.Yellow);
        }

        /// <summary>
        /// Scrive una riga di testo in colore ciano.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteCyan(string text)
        {
            WriteColored(text, ConsoleColor.Cyan);
        }

        /// <summary>
        /// Scrive una riga di testo in colore magenta.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteMagenta(string text)
        {
            WriteColored(text, ConsoleColor.Magenta);
        }

        /// <summary>
        /// Scrive una riga di testo in colore bianco.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteWhite(string text)
        {
            WriteColored(text, ConsoleColor.White);
        }

        /// <summary>
        /// Scrive una riga di testo in colore grigio scuro.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteDarkGray(string text)
        {
            WriteColored(text, ConsoleColor.DarkGray);
        }

        /// <summary>
        /// Scrive una riga di testo in colore rosso scuro.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteDarkRed(string text)
        {
            WriteColored(text, ConsoleColor.DarkRed);
        }

        /// <summary>
        /// Scrive una riga di testo in colore giallo scuro.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteDarkYellow(string text)
        {
            WriteColored(text, ConsoleColor.DarkYellow);
        }

        /// <summary>
        /// Scrive una riga di testo in colore ciano scuro.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WriteDarkCyan(string text)
        {
            WriteColored(text, ConsoleColor.DarkCyan);
        }

        /// <summary>
        /// Scrive una riga di testo senza formattazione colore.
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        public static void WritePlain(string text)
        {
            if (s_logCallback != null)
            {
                s_logCallback(text, ConsoleColor.Gray);
            }
            else
            {
                Console.WriteLine(text);
            }
        }

        /// <summary>
        /// Scrive un messaggio di avviso in giallo, con prefisso etichetta.
        /// </summary>
        /// <param name="text">Il messaggio di avviso.</param>
        public static void WriteWarning(string text)
        {
            WriteColored("ATTENZIONE: " + text, ConsoleColor.Yellow);
        }

        /// <summary>
        /// Imposta un callback per il redirect dell'output
        /// </summary>
        /// <param name="callback">Callback che riceve testo e colore</param>
        public static void SetLogCallback(Action<string, ConsoleColor> callback)
        {
            s_logCallback = callback;
        }

        /// <summary>
        /// Rimuove il callback di redirect e ripristina l'output su console
        /// </summary>
        public static void ClearLogCallback()
        {
            s_logCallback = null;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Scrive testo con il colore specificato e ripristina il colore originale
        /// </summary>
        /// <param name="text">Il testo da scrivere.</param>
        /// <param name="color">Il colore di primo piano da usare.</param>
        private static void WriteColored(string text, ConsoleColor color)
        {
            if (s_logCallback != null)
            {
                s_logCallback(text, color);
            }
            else
            {
                ConsoleColor original = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(text);
                Console.ForegroundColor = original;
            }
        }

        #endregion
    }
}
