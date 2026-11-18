using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Uso di uno stile ASS/SSA nelle righe Dialogue con testo leggibile di uno o più file, coi testi e gli indizi misurati che il
    /// riconoscimento del tipo riceve: karaoke o Effect del templater, posizione. I disegni restano fuori
    /// </summary>
    public class SubtitleStyleUsage
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome dello stile</param>
        public SubtitleStyleUsage(string name)
        {
            this.Name = name ?? "";
            this.KaraokeLines = 0;
            this.PositionLines = 0;
            this.Texts = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome dello stile, come nel campo Style delle righe
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Testo leggibile di ogni riga dello stile nell'ordine del file, senza tag e con gli a capo come spazi
        /// </summary>
        public List<string> Texts { get; }

        /// <summary>
        /// Righe con tag karaoke (\k, \K, \kf, \ko) o con Effect kara, fx, template o code
        /// </summary>
        public int KaraokeLines { get; set; }

        /// <summary>
        /// Righe con \pos, \move o \org
        /// </summary>
        public int PositionLines { get; set; }

        #endregion
    }
}
