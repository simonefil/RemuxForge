using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Divide le battute traducibili di un file nei blocchi che il modello traduce in una chiamata: tagli sulle pause più lunghe
    /// </summary>
    public class SubtitleBlockPlanner
    {
        #region Costanti

        /// <summary>
        /// Battute per blocco cercate
        /// </summary>
        public const int DEFAULT_TARGET = 60;

        /// <summary>
        /// Battute massime per blocco
        /// </summary>
        public const int DEFAULT_CAP = 80;

        /// <summary>
        /// Pausa che separa due scene
        /// </summary>
        private const long SCENE_PAUSE_MS = 2000;

        /// <summary>
        /// Pausa oltre la quale due tagli valgono uguale e decide la distanza dall'obiettivo
        /// </summary>
        private const long MAX_SCORED_PAUSE_MS = 3 * SCENE_PAUSE_MS;

        /// <summary>
        /// Frazione dell'obiettivo sotto la quale un blocco non si chiude
        /// </summary>
        private const double MIN_FRACTION = 0.6;

        /// <summary>
        /// Sesti del blocco da dimezzare: la fascia centrale va da mezzo sesto prima a mezzo sesto dopo la metà
        /// </summary>
        private const int SPLIT_BANDS = 6;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Battute traducibili in ordine di id
        /// </summary>
        private readonly List<SubtitleCue> _cues;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="cues">Battute del file; si tengono le sole traducibili</param>
        public SubtitleBlockPlanner(IReadOnlyList<SubtitleCue> cues)
        {
            this._cues = new List<SubtitleCue>();
            foreach (SubtitleCue cue in cues)
            {
                if (cue.Id.HasValue)
                    this._cues.Add(cue);
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Divide le battute in blocchi: ogni blocco si chiude sul taglio con la pausa più lunga fra il minimo e il tetto,
        /// a pari pausa sul più vicino all'obiettivo; le ultime battute fino al tetto vanno tutte nell'ultimo blocco
        /// </summary>
        /// <param name="target">Battute per blocco cercate</param>
        /// <param name="cap">Battute massime per blocco</param>
        /// <returns>Blocchi in ordine di id</returns>
        public IReadOnlyList<IReadOnlyList<SubtitleCue>> PlanBlocks(int target, int cap)
        {
            List<IReadOnlyList<SubtitleCue>> blocks = new List<IReadOnlyList<SubtitleCue>>();
            int minimum = Math.Max(1, (int)Math.Floor(MIN_FRACTION * target));
            int start = 0;
            while (start < this._cues.Count)
            {
                if (this._cues.Count - start <= cap)
                {
                    blocks.Add(this._cues.GetRange(start, this._cues.Count - start));
                    break;
                }

                // Taglio fra j-1 e j: prima la pausa fino al limite, poi la vicinanza all'obiettivo
                int best = -1;
                long bestPause = 0;
                int bestDistance = 0;
                int last = Math.Min(start + cap, this._cues.Count - 1);
                for (int j = start + minimum; j <= last; j++)
                {
                    long pause = Math.Min(Pause(this._cues[j - 1], this._cues[j]), MAX_SCORED_PAUSE_MS);
                    int distance = Math.Abs(j - start - target);
                    if (best < 0 || pause > bestPause || (pause == bestPause && distance < bestDistance))
                    {
                        best = j;
                        bestPause = pause;
                        bestDistance = distance;
                    }
                }

                blocks.Add(this._cues.GetRange(start, best - start));
                start = best;
            }

            return blocks;
        }

        /// <summary>
        /// Divide in due un blocco tornato incompleto: taglio più vicino al centro a fasce di mezzo sesto per lato, nella
        /// fascia la pausa più lunga, così un confine di scena vicino alla metà vince
        /// </summary>
        /// <param name="block">Blocco da dividere</param>
        /// <returns>Due blocchi, o il blocco stesso se ha una battuta sola</returns>
        public static IReadOnlyList<IReadOnlyList<SubtitleCue>> SplitBlock(IReadOnlyList<SubtitleCue> block)
        {
            int mid = block.Count / 2;
            int bucket = Math.Max(1, block.Count / SPLIT_BANDS);
            int best = -1;
            int bestBand = 0;
            long bestPause = 0;
            for (int j = 1; j < block.Count; j++)
            {
                int band = 2 * Math.Abs(j - mid) / bucket;
                long pause = Pause(block[j - 1], block[j]);
                if (best < 0 || band < bestBand || (band == bestBand && pause > bestPause))
                {
                    best = j;
                    bestBand = band;
                    bestPause = pause;
                }
            }

            List<IReadOnlyList<SubtitleCue>> halves = new List<IReadOnlyList<SubtitleCue>>();
            if (best < 0)
            {
                halves.Add(block);
                return halves;
            }

            List<SubtitleCue> first = new List<SubtitleCue>();
            List<SubtitleCue> second = new List<SubtitleCue>();
            for (int i = 0; i < block.Count; i++)
            {
                if (i < best)
                {
                    first.Add(block[i]);
                }
                else
                {
                    second.Add(block[i]);
                }
            }
            halves.Add(first);
            halves.Add(second);
            return halves;
        }

        /// <summary>
        /// Divide in due un gruppo di battute come SplitBlock e restituisce gli id delle parti
        /// </summary>
        /// <param name="block">Battute da dividere</param>
        /// <returns>Id delle due parti, o di tutto il gruppo se ha una battuta sola</returns>
        public static List<List<int>> SplitIds(IReadOnlyList<SubtitleCue> block)
        {
            List<List<int>> parts = new List<List<int>>();
            foreach (IReadOnlyList<SubtitleCue> half in SplitBlock(block))
            {
                List<int> ids = new List<int>();
                foreach (SubtitleCue cue in half)
                    ids.Add(cue.Id.Value);
                parts.Add(ids);
            }

            return parts;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Pausa fra due battute consecutive, zero se si sovrappongono
        /// </summary>
        /// <param name="previous">Battuta precedente</param>
        /// <param name="next">Battuta seguente</param>
        /// <returns>Pausa in millisecondi</returns>
        private static long Pause(SubtitleCue previous, SubtitleCue next)
        {
            return Math.Max(0, next.StartMs - previous.EndMs);
        }

        #endregion
    }
}
