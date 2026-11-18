using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Uso di una combinazione di stile, attore ed effetto nelle righe Dialogue con testo leggibile di uno o più file, coi testi e
    /// gli indizi misurati che il riconoscimento del tipo riceve: karaoke o Effect del templater, posizione, righe sovrapposte nel
    /// tempo. I disegni restano fuori
    /// </summary>
    public class SubtitleStyleUsage
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome dello stile</param>
        /// <param name="actor">Campo Name (attore) delle righe</param>
        /// <param name="effect">Campo Effect delle righe</param>
        public SubtitleStyleUsage(string name, string actor, string effect)
        {
            this.Name = name ?? "";
            this.Actor = actor ?? "";
            this.Effect = effect ?? "";
            this.KaraokeLines = 0;
            this.PositionLines = 0;
            this.OverlappingLines = 0;
            this.MaxConcurrent = 0;
            this.Texts = new List<string>();
            this.Positions = new List<int>();
            this.Continued = new List<bool>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// True se l'uso è della combinazione data
        /// </summary>
        /// <param name="name">Nome dello stile</param>
        /// <param name="actor">Attore</param>
        /// <param name="effect">Effetto</param>
        /// <returns>True con stile, attore ed effetto uguali</returns>
        public bool Is(string name, string actor, string effect)
        {
            return this.Name == name && this.Actor == actor && this.Effect == effect;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome dello stile, come nel campo Style delle righe
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Campo Name (attore) delle righe, senza spazi ai bordi
        /// </summary>
        public string Actor { get; }

        /// <summary>
        /// Campo Effect delle righe, senza spazi ai bordi
        /// </summary>
        public string Effect { get; }

        /// <summary>
        /// Testo leggibile di ogni riga nell'ordine del file, senza tag e con gli a capo come spazi
        /// </summary>
        public List<string> Texts { get; }

        /// <summary>
        /// Posizione nel file della battuta di ogni testo, nell'ordine di Texts
        /// </summary>
        public List<int> Positions { get; }

        /// <summary>
        /// True per ogni testo che continua una riga già contata, nell'ordine di Texts: stesso testo negli stessi tempi o
        /// dall'istante in cui la riga contata finisce
        /// </summary>
        public List<bool> Continued { get; }

        /// <summary>
        /// Righe contate di più di un carattere con tag karaoke (\k, \K, \kf, \ko) o con Effect kara, fx, template o code
        /// </summary>
        public int KaraokeLines { get; set; }

        /// <summary>
        /// Righe contate di più di un carattere con \pos, \move o \org
        /// </summary>
        public int PositionLines { get; set; }

        /// <summary>
        /// Righe contate di più di un carattere che si sovrappongono nel tempo a un'altra di più di un carattere della stessa combinazione nello stesso file
        /// </summary>
        public int OverlappingLines { get; set; }

        /// <summary>
        /// Massimo di righe contate di più di un carattere della combinazione sullo schermo nello stesso istante, in un file
        /// </summary>
        public int MaxConcurrent { get; set; }

        #endregion
    }
}
