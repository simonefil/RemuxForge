namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Uso di uno stile ASS/SSA nelle righe Dialogue di uno o più file, con gli indizi affidabili del tipo: karaoke o Effect del
    /// templater per le canzoni, posizione o disegni per i cartelli. Il nome dello stile non conta, perché non è uno standard
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
            this.Lines = 0;
            this.SongLines = 0;
            this.SignLines = 0;
            this.Example = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome dello stile, come nel campo Style delle righe
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Righe Dialogue dello stile
        /// </summary>
        public int Lines { get; set; }

        /// <summary>
        /// Righe con tag karaoke (\k, \K, \kf, \ko) o con Effect kara, fx, template o code
        /// </summary>
        public int SongLines { get; set; }

        /// <summary>
        /// Righe con \pos, \move, \org o un disegno
        /// </summary>
        public int SignLines { get; set; }

        /// <summary>
        /// Testo della prima riga con testo leggibile, senza tag e con gli a capo come spazi; vuoto se non ce n'è
        /// </summary>
        public string Example { get; set; }

        /// <summary>
        /// Tipo proposto: canzone se la maggior parte delle righe ha karaoke o Effect del templater, altrimenti cartello se la
        /// maggior parte ha posizione o disegni, altrimenti dialogo
        /// </summary>
        public TranslationStyleKind SuggestedKind
        {
            get
            {
                if (this.SongLines * 2 > this.Lines)
                    return TranslationStyleKind.Song;
                return this.SignLines * 2 > this.Lines ? TranslationStyleKind.Sign : TranslationStyleKind.Dialogue;
            }
        }

        #endregion
    }
}
