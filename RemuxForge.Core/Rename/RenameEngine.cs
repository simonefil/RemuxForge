using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Rename
{
    /// <summary>
    /// Engine Advanced Rename portato da Bivium
    /// </summary>
    public static class RenameEngine
    {
        #region Variabili di classe

        /// <summary>
        /// Generatore numeri casuali per tag Rand
        /// </summary>
        private static readonly Random s_random = new Random();

        /// <summary>
        /// Caratteri non validi nei nomi file
        /// </summary>
        private static readonly char[] s_invalidChars = Path.GetInvalidFileNameChars();

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Genera preview applicando tutti i metodi in sequenza
        /// </summary>
        /// <param name="entries">File da rinominare</param>
        /// <param name="methodStack">Stack ordinato metodi</param>
        /// <returns>Preview rename</returns>
        public static List<RenamePreviewItem> GeneratePreview(List<RenameFileEntry> entries, List<RenameMethod> methodStack)
        {
            List<RenamePreviewItem> items = new List<RenamePreviewItem>();

            if (entries == null)
                return items;

            for (int i = 0; i < entries.Count; i++)
            {
                RenameFileEntry entry = entries[i];
                string currentName = entry.Name;
                string directory = Path.GetDirectoryName(entry.FullPath);
                string parentFolder = Path.GetFileName(directory) ?? "";

                if (methodStack != null)
                {
                    for (int m = 0; m < methodStack.Count; m++)
                    {
                        currentName = ApplyMethod(currentName, methodStack[m], i, entry.LastModified, parentFolder);
                    }
                }

                RenamePreviewItem item = new RenamePreviewItem();
                item.OriginalName = entry.Name;
                item.OriginalFullPath = entry.FullPath;
                item.NewName = currentName;
                item.TargetFullPath = Path.Combine(directory, currentName);
                ValidateItem(item);
                items.Add(item);
            }

            DetectConflicts(items);
            DetectExistingTargetConflicts(items);

            return items;
        }

        /// <summary>
        /// Applica un singolo metodo a un nome file
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <param name="fileIndex">Indice file</param>
        /// <param name="lastModified">Data modifica</param>
        /// <param name="parentFolder">Cartella padre</param>
        /// <returns>Nome file modificato</returns>
        public static string ApplyMethod(string fileName, RenameMethod method, int fileIndex, DateTime lastModified, string parentFolder)
        {
            string result = fileName;

            if (method == null)
                return result;

            if (method.MethodType == RenameMethodType.Replace)
            {
                result = ApplyReplace(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.Add)
            {
                result = ApplyAdd(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.Remove)
            {
                result = ApplyRemove(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.NewCase)
            {
                result = ApplyNewCase(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.NewName)
            {
                result = ApplyNewName(fileName, method, fileIndex, lastModified, parentFolder);
            }
            else if (method.MethodType == RenameMethodType.Trim)
            {
                result = ApplyTrim(fileName, method);
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Divide nome file ed estensione senza punto
        /// </summary>
        /// <param name="fileName">Nome file completo</param>
        /// <param name="name">Nome senza estensione</param>
        /// <param name="ext">Estensione senza punto</param>
        private static void SplitFileName(string fileName, out string name, out string ext)
        {
            string extension = Path.GetExtension(fileName);
            if (extension.Length > 0)
            {
                ext = extension.Substring(1);
                name = fileName.Substring(0, fileName.Length - extension.Length);
            }
            else
            {
                ext = "";
                name = fileName;
            }
        }

        /// <summary>
        /// Ricompone nome file ed estensione
        /// </summary>
        /// <param name="name">Nome senza estensione</param>
        /// <param name="ext">Estensione senza punto</param>
        /// <returns>Nome file completo</returns>
        private static string JoinFileName(string name, string ext)
        {
            if (string.IsNullOrEmpty(ext))
                return name;

            return name + "." + ext;
        }

        /// <summary>
        /// Applica metodo Replace
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <returns>Nome file modificato</returns>
        private static string ApplyReplace(string fileName, RenameMethod method)
        {
            string result = fileName;

            if (string.IsNullOrEmpty(method.SearchText))
                return result;

            if (method.UseRegex)
            {
                try
                {
                    RegexOptions options = RegexOptions.None;
                    if (!method.CaseSensitive)
                        options = RegexOptions.IgnoreCase;

                    string replacement = method.ReplaceText ?? "";
                    result = Regex.Replace(fileName, method.SearchText, replacement, options);
                }
                catch
                {
                    result = fileName;
                }
            }
            else
            {
                if (method.CaseSensitive)
                {
                    result = fileName.Replace(method.SearchText, method.ReplaceText);
                }
                else
                {
                    result = ReplaceCaseInsensitive(fileName, method.SearchText, method.ReplaceText);
                }
            }

            return result;
        }

        /// <summary>
        /// Sostituisce testo ignorando maiuscole e minuscole
        /// </summary>
        /// <param name="input">Testo sorgente</param>
        /// <param name="search">Testo da cercare</param>
        /// <param name="replacement">Testo sostitutivo</param>
        /// <returns>Testo modificato</returns>
        private static string ReplaceCaseInsensitive(string input, string search, string replacement)
        {
            string result = input;
            int index = 0;

            while (true)
            {
                int found = result.IndexOf(search, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                result = result.Substring(0, found) + replacement + result.Substring(found + search.Length);
                index = found + replacement.Length;
            }

            return result;
        }

        /// <summary>
        /// Applica metodo Add
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <returns>Nome file modificato</returns>
        private static string ApplyAdd(string fileName, RenameMethod method)
        {
            string result = fileName;

            if (string.IsNullOrEmpty(method.InsertText))
                return result;

            SplitFileName(fileName, out string name, out string ext);

            int pos = method.InsertPosition;
            if (method.FromEnd)
                pos = name.Length - pos;

            if (pos < 0)
                pos = 0;

            if (pos > name.Length)
                pos = name.Length;

            name = name.Insert(pos, method.InsertText);
            result = JoinFileName(name, ext);

            return result;
        }

        /// <summary>
        /// Applica metodo Remove
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <returns>Nome file modificato</returns>
        private static string ApplyRemove(string fileName, RenameMethod method)
        {
            string result = fileName;

            SplitFileName(fileName, out string name, out string ext);

            if (method.RemoveByPattern)
            {
                if (string.IsNullOrEmpty(method.RemovePattern))
                    return result;

                if (method.RemovePatternUseRegex)
                {
                    try
                    {
                        RegexOptions options = RegexOptions.None;
                        if (!method.RemovePatternCaseSensitive)
                            options = RegexOptions.IgnoreCase;

                        name = Regex.Replace(name, method.RemovePattern, "", options);
                    }
                    catch
                    {
                        return result;
                    }
                }
                else if (method.RemovePatternCaseSensitive)
                {
                    name = name.Replace(method.RemovePattern, "");
                }
                else
                {
                    name = ReplaceCaseInsensitive(name, method.RemovePattern, "");
                }
            }
            else
            {
                int start = method.RemoveStartIndex;
                int count = method.RemoveCount;

                if (method.RemoveFromEnd)
                    start = name.Length - start - count;

                if (start < 0)
                {
                    count = count + start;
                    start = 0;
                }
                if (start >= name.Length || count <= 0)
                    return result;

                if (start + count > name.Length)
                    count = name.Length - start;

                name = name.Remove(start, count);
            }

            result = JoinFileName(name, ext);
            return result;
        }

        /// <summary>
        /// Applica metodo New Case
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <returns>Nome file modificato</returns>
        private static string ApplyNewCase(string fileName, RenameMethod method)
        {
            SplitFileName(fileName, out string name, out string ext);

            if (method.CaseScope == 0 || method.CaseScope == 2)
                name = ChangeCase(name, method.CaseMode);

            if (method.CaseScope == 1 || method.CaseScope == 2)
                ext = ChangeCase(ext, method.CaseMode);

            return JoinFileName(name, ext);
        }

        /// <summary>
        /// Cambia maiuscole/minuscole del testo secondo la modalità richiesta
        /// </summary>
        /// <param name="text">Testo sorgente</param>
        /// <param name="mode">Modalità case</param>
        /// <returns>Testo modificato</returns>
        private static string ChangeCase(string text, int mode)
        {
            string result = text;

            if (string.IsNullOrEmpty(text))
                return result;

            if (mode == 0)
            {
                result = text.ToLowerInvariant();
            }
            else if (mode == 1)
            {
                result = text.ToUpperInvariant();
            }
            else if (mode == 2)
            {
                TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
                result = textInfo.ToTitleCase(text.ToLowerInvariant());
            }

            return result;
        }

        /// <summary>
        /// Applica metodo New Name con tag
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <param name="fileIndex">Indice file</param>
        /// <param name="lastModified">Data ultima modifica</param>
        /// <param name="parentFolder">Cartella padre</param>
        /// <returns>Nome file modificato</returns>
        private static string ApplyNewName(string fileName, RenameMethod method, int fileIndex, DateTime lastModified, string parentFolder)
        {
            SplitFileName(fileName, out string originalName, out string originalExt);

            string pattern = method.NamePattern;
            if (string.IsNullOrEmpty(pattern))
                return fileName;

            string result = "";
            int pos = 0;

            while (pos < pattern.Length)
            {
                int tagStart = pattern.IndexOf('<', pos);
                if (tagStart < 0)
                {
                    result = result + pattern.Substring(pos);
                    break;
                }

                if (tagStart > pos)
                    result = result + pattern.Substring(pos, tagStart - pos);

                int tagEnd = pattern.IndexOf('>', tagStart);
                if (tagEnd < 0)
                {
                    result = result + pattern.Substring(tagStart);
                    break;
                }

                string tagContent = pattern.Substring(tagStart + 1, tagEnd - tagStart - 1);
                string tagValue = ResolveTag(tagContent, originalName, originalExt, fileIndex, lastModified, parentFolder);
                result = result + tagValue;

                pos = tagEnd + 1;
            }

            return result;
        }

        /// <summary>
        /// Risolve un tag New Name
        /// </summary>
        /// <param name="tagContent">Contenuto tag senza parentesi angolari</param>
        /// <param name="originalName">Nome originale senza estensione</param>
        /// <param name="originalExt">Estensione originale senza punto</param>
        /// <param name="fileIndex">Indice file</param>
        /// <param name="lastModified">Data ultima modifica</param>
        /// <param name="parentFolder">Cartella padre</param>
        /// <returns>Valore tag</returns>
        private static string ResolveTag(string tagContent, string originalName, string originalExt, int fileIndex, DateTime lastModified, string parentFolder)
        {
            string result = "<" + tagContent + ">";

            if (tagContent == "Name")
            {
                result = originalName;
            }
            else if (tagContent == "Ext")
            {
                result = originalExt;
            }
            else if (tagContent == "Folder")
            {
                result = parentFolder;
            }
            else if (tagContent.StartsWith("Inc:", StringComparison.Ordinal))
            {
                result = ResolveIncTag(tagContent, fileIndex);
            }
            else if (tagContent.StartsWith("Date:", StringComparison.Ordinal))
            {
                result = ResolveDateTag(tagContent, lastModified);
            }
            else if (tagContent.StartsWith("Rand:", StringComparison.Ordinal))
            {
                result = ResolveRandTag(tagContent);
            }

            return result;
        }

        /// <summary>
        /// Risolve tag incrementale Inc
        /// </summary>
        /// <param name="tagContent">Contenuto tag</param>
        /// <param name="fileIndex">Indice file</param>
        /// <returns>Valore incrementale formattato</returns>
        private static string ResolveIncTag(string tagContent, int fileIndex)
        {
            string parameters = tagContent.Substring(4);
            string[] parts = parameters.Split(':');

            int start = 1;
            int step = 1;
            int pad = 1;

            if (parts.Length >= 1 && int.TryParse(parts[0], out int parsedStart))
                start = parsedStart;

            if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedStep))
                step = parsedStep;

            if (parts.Length >= 3 && int.TryParse(parts[2], out int parsedPad))
                pad = parsedPad;

            int value = start + (fileIndex * step);
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0');
        }

        /// <summary>
        /// Risolve tag data
        /// </summary>
        /// <param name="tagContent">Contenuto tag</param>
        /// <param name="lastModified">Data ultima modifica</param>
        /// <returns>Data formattata o tag originale se il formato non è valido</returns>
        private static string ResolveDateTag(string tagContent, DateTime lastModified)
        {
            string result;
            string format = tagContent.Substring(5);

            try
            {
                result = lastModified.ToString(format, CultureInfo.InvariantCulture);
            }
            catch
            {
                result = "<" + tagContent + ">";
            }

            return result;
        }

        /// <summary>
        /// Risolve tag casuale Rand
        /// </summary>
        /// <param name="tagContent">Contenuto tag</param>
        /// <returns>Valore casuale formattato</returns>
        private static string ResolveRandTag(string tagContent)
        {
            string parameters = tagContent.Substring(5);
            string[] parts = parameters.Split(':');

            int min = 0;
            int max = 100;

            if (parts.Length >= 1 && int.TryParse(parts[0], out int parsedMin))
                min = parsedMin;

            if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedMax))
                max = parsedMax;

            if (max <= min)
                max = min + 1;

            return s_random.Next(min, max + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Applica metodo Trim
        /// </summary>
        /// <param name="fileName">Nome file corrente</param>
        /// <param name="method">Metodo rename</param>
        /// <returns>Nome file modificato</returns>
        private static string ApplyTrim(string fileName, RenameMethod method)
        {
            SplitFileName(fileName, out string name, out string ext);

            string trimSource = method.TrimCharacters ?? " ";
            char[] trimChars = trimSource.ToCharArray();
            if (trimChars.Length == 0)
                trimChars = new char[] { ' ' };

            if (method.TrimScope == 0 || method.TrimScope == 2)
                name = TrimString(name, trimChars, method.TrimLocation);

            if (method.TrimScope == 1 || method.TrimScope == 2)
                ext = TrimString(ext, trimChars, method.TrimLocation);

            return JoinFileName(name, ext);
        }

        /// <summary>
        /// Applica trim in base alla posizione richiesta
        /// </summary>
        /// <param name="text">Testo sorgente</param>
        /// <param name="chars">Caratteri da rimuovere</param>
        /// <param name="location">Posizione trim</param>
        /// <returns>Testo modificato</returns>
        private static string TrimString(string text, char[] chars, int location)
        {
            string result = text;

            if (location == 0)
            {
                result = text.TrimStart(chars);
            }
            else if (location == 1)
            {
                result = text.TrimEnd(chars);
            }
            else
            {
                result = text.Trim(chars);
            }

            return result;
        }

        /// <summary>
        /// Valida un elemento preview
        /// </summary>
        /// <param name="item">Elemento preview</param>
        private static void ValidateItem(RenamePreviewItem item)
        {
            if (item.NewName.IndexOfAny(s_invalidChars) >= 0)
            {
                item.HasError = true;
                item.ErrorMessage = AppText.T("rename.error.invalidFileNameChars");
            }
            else if (string.IsNullOrWhiteSpace(item.NewName))
            {
                item.HasError = true;
                item.ErrorMessage = AppText.T("rename.error.emptyFileName");
            }
        }

        /// <summary>
        /// Rileva conflitti tra destinazioni generate nello stesso batch
        /// </summary>
        /// <param name="items">Elementi preview</param>
        private static void DetectConflicts(List<RenamePreviewItem> items)
        {
            StringComparison comparison = GetFileNameComparison();

            for (int i = 0; i < items.Count; i++)
            {
                items[i].HasConflict = false;
            }

            for (int i = 0; i < items.Count; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    string dirI = Path.GetDirectoryName(items[i].OriginalFullPath);
                    string dirJ = Path.GetDirectoryName(items[j].OriginalFullPath);

                    if (string.Equals(dirI, dirJ, comparison) && string.Equals(items[i].NewName, items[j].NewName, comparison))
                    {
                        items[i].HasConflict = true;
                        items[j].HasConflict = true;
                    }
                }
            }
        }

        /// <summary>
        /// Rileva conflitti con file già presenti su disco
        /// </summary>
        /// <param name="items">Elementi preview</param>
        private static void DetectExistingTargetConflicts(List<RenamePreviewItem> items)
        {
            StringComparison comparison = GetFileNameComparison();

            for (int i = 0; i < items.Count; i++)
            {
                RenamePreviewItem item = items[i];
                if (item.HasError)
                    continue;

                if (string.Equals(item.OriginalFullPath, item.TargetFullPath, comparison))
                    continue;

                if (!File.Exists(item.TargetFullPath))
                    continue;

                if (IsTargetMovedInsideBatch(items, item.TargetFullPath, comparison))
                    continue;

                item.HasError = true;
                item.ErrorMessage = AppText.T("rename.error.targetExists");
            }
        }

        /// <summary>
        /// Indica se la destinazione è liberata da un altro rename dello stesso batch
        /// </summary>
        /// <param name="items">Elementi preview</param>
        /// <param name="targetPath">Percorso destinazione</param>
        /// <param name="comparison">Comparazione path</param>
        /// <returns>Vero se la destinazione viene liberata nel batch</returns>
        private static bool IsTargetMovedInsideBatch(List<RenamePreviewItem> items, string targetPath, StringComparison comparison)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].OriginalFullPath, targetPath, comparison) &&
                    !string.Equals(items[i].OriginalName, items[i].NewName, comparison))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Restituisce comparazione nomi file coerente con il filesystem corrente
        /// </summary>
        /// <returns>Comparazione stringhe per nomi file</returns>
        private static StringComparison GetFileNameComparison()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                return StringComparison.OrdinalIgnoreCase;

            return StringComparison.Ordinal;
        }

        #endregion
    }
}
