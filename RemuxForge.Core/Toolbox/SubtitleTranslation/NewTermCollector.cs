using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Termini nuovi del job: le segnalazioni di tutti i file consolidate per termine, senza i termini e i personaggi già nel
    /// glossario, e la voce del glossario di un termine aggiunto
    /// </summary>
    public static class NewTermCollector
    {
        #region Metodi pubblici

        /// <summary>
        /// Consolida le segnalazioni dei file: una per chiamata, raggruppate per termine senza maiuscole né articolo iniziale. La
        /// resa proposta è la più segnalata, a pari conteggio la prima comparsa
        /// </summary>
        /// <param name="glossary">Glossario del job</param>
        /// <param name="files">Stati dei file, nell'ordine del job</param>
        /// <returns>Termini da decidere, nell'ordine della prima comparsa</returns>
        public static List<NewTermDecision> Collect(Glossary glossary, IReadOnlyList<TranslationJobFileState> files)
        {
            string language = LanguageValidator.NormalizeToIso6392(glossary.SourceLanguage);
            HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
            foreach (GlossaryTerm term in glossary.Terms)
                known.Add(TranslationValidator.StripArticle(term.Source, language).ToLowerInvariant());
            foreach (GlossaryCharacter character in glossary.Characters)
                known.Add(TranslationValidator.StripArticle(character.Name, language).ToLowerInvariant());

            List<NewTermDecision> decisions = new List<NewTermDecision>();
            Dictionary<string, NewTermDecision> byKey = new Dictionary<string, NewTermDecision>(StringComparer.Ordinal);
            foreach (TranslationJobFileState file in files)
            {
                foreach (CollectedNewTerm report in file.CollectedNewTerms)
                {
                    string key = TranslationValidator.StripArticle(report.Source, language).ToLowerInvariant();
                    if (string.IsNullOrEmpty(key) || known.Contains(key))
                        continue;

                    NewTermDecision decision;
                    if (!byKey.TryGetValue(key, out decision))
                    {
                        decision = new NewTermDecision { Source = report.Source, Kind = report.Kind, Invariable = report.Invariable };
                        byKey[key] = decision;
                        decisions.Add(decision);
                    }
                    decision.Invariable &= report.Invariable;

                    NewTermRendering rendering = decision.Renderings.Find(r => string.Equals(r.Target, report.Target, StringComparison.Ordinal));
                    if (rendering == null)
                    {
                        rendering = new NewTermRendering { Target = report.Target };
                        decision.Renderings.Add(rendering);
                    }
                    rendering.Count++;
                    if (!rendering.FileIds.Contains(file.FileId))
                        rendering.FileIds.Add(file.FileId);
                }
            }

            foreach (NewTermDecision decision in decisions)
            {
                NewTermRendering proposed = decision.Renderings[0];
                foreach (NewTermRendering rendering in decision.Renderings)
                {
                    if (rendering.Count > proposed.Count)
                        proposed = rendering;
                }
                decision.ChosenTarget = proposed.Target;
            }

            return decisions;
        }

        /// <summary>
        /// Voce del glossario di un termine aggiunto: resa scelta, tipo, invariabile, episodi delle segnalazioni e prima nota del
        /// modello
        /// </summary>
        /// <param name="decision">Termine deciso con «Aggiungi»</param>
        /// <param name="glossary">Glossario del job, per la lingua d'origine</param>
        /// <param name="files">Stati dei file, per le note delle segnalazioni</param>
        /// <returns>Voce da aggiungere</returns>
        public static GlossaryTerm CreateTerm(NewTermDecision decision, Glossary glossary, IReadOnlyList<TranslationJobFileState> files)
        {
            GlossaryTerm term = new GlossaryTerm();
            term.Source = decision.Source;
            term.Target = decision.ChosenTarget.Trim();
            term.Kind = decision.Kind;
            term.Invariable = decision.Invariable;
            term.Origin = GlossaryOrigin.NewTerm;
            foreach (NewTermRendering rendering in decision.Renderings)
            {
                foreach (string fileId in rendering.FileIds)
                {
                    if (!term.Episodes.Contains(fileId))
                        term.Episodes.Add(fileId);
                }
            }

            string language = LanguageValidator.NormalizeToIso6392(glossary.SourceLanguage);
            string key = TranslationValidator.StripArticle(decision.Source, language).ToLowerInvariant();
            foreach (TranslationJobFileState file in files)
            {
                CollectedNewTerm noted = file.CollectedNewTerms.Find(r => !string.IsNullOrWhiteSpace(r.Note)
                    && TranslationValidator.StripArticle(r.Source, language).ToLowerInvariant() == key);
                if (noted != null)
                {
                    term.Note = noted.Note.Trim();
                    break;
                }
            }

            return term;
        }

        #endregion
    }
}
