using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Disposizione dei segmenti nell'editor di taglio: corsie della timeline, colori ed etichette brevi dei file di uscita
    /// </summary>
    public static class SplitEditorLayout
    {
        #region Variabili statiche

        /// <summary>
        /// Colori dei file di uscita, assegnati in ordine e ripetuti oltre l'ultimo
        /// </summary>
        private static readonly string[] OUTPUT_COLORS = { "#ff6d41", "#3b9fd5", "#a98bf0", "#2cc8c8", "#e6b84a", "#7cc46b", "#e57ba6", "#9aa7b0" };

        /// <summary>
        /// Separatori ammessi fra il nome del sorgente e la parte distintiva del nome di uscita
        /// </summary>
        private static readonly char[] NAME_SEPARATORS = { '.', ' ', '_', '-' };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Assegna a ogni intervallo la prima corsia libera, nell'ordine ricevuto: gli intervalli che si sovrappongono
        /// finiscono su corsie diverse, quelli che si toccano restano sulla stessa
        /// </summary>
        /// <param name="ranges">Intervalli in frame con fine esclusiva, nell'ordine dei file e dei segmenti</param>
        /// <returns>Corsia di ogni intervallo, a partire da zero</returns>
        public static List<int> AssignLanes(IReadOnlyList<(int Start, int End)> ranges)
        {
            List<int> lanes = new List<int>();
            List<List<(int Start, int End)>> occupied = new List<List<(int Start, int End)>>();

            foreach ((int Start, int End) range in ranges)
            {
                int lane = occupied.FindIndex(items => !items.Exists(item => range.Start < item.End && item.Start < range.End));
                if (lane < 0)
                {
                    occupied.Add(new List<(int Start, int End)>());
                    lane = occupied.Count - 1;
                }
                occupied[lane].Add(range);
                lanes.Add(lane);
            }
            return lanes;
        }

        /// <summary>
        /// Colore del file di uscita nella posizione indicata
        /// </summary>
        /// <param name="index">Posizione del file nell'elenco</param>
        /// <returns>Colore CSS</returns>
        public static string OutputColor(int index)
        {
            return OUTPUT_COLORS[Math.Abs(index) % OUTPUT_COLORS.Length];
        }

        /// <summary>
        /// Nome breve di un file di uscita: senza cartelle, senza estensione e senza il nome del sorgente quando lo ripete
        /// </summary>
        /// <param name="fileName">Nome del file di uscita</param>
        /// <param name="sourcePath">Percorso del sorgente</param>
        /// <returns>Nome breve, il nome intero senza estensione se non c'è una parte distintiva</returns>
        public static string ShortName(string fileName, string sourcePath)
        {
            string name = Path.GetFileNameWithoutExtension((fileName ?? "").Replace('\\', '/').Split('/')[^1]);
            string source = Path.GetFileNameWithoutExtension(sourcePath ?? "");

            if (source.Length > 0 && name.Length > source.Length + 1 && name.StartsWith(source, StringComparison.OrdinalIgnoreCase)
                && Array.IndexOf(NAME_SEPARATORS, name[source.Length]) >= 0)
                return name.Substring(source.Length + 1);
            return name;
        }

        #endregion
    }
}
