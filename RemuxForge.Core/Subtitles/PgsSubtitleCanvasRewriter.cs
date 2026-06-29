using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive canvas, coordinate e bitmap oggetto di sottotitoli PGS/SUP
    /// </summary>
    internal class PgsSubtitleCanvasRewriter : ISubtitleCanvasRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Indica se il rewriter gestisce la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se il codec è PGS</returns>
        public bool CanHandle(TrackInfo track)
        {
            string codec = track?.Codec != null ? track.Codec.ToLowerInvariant() : "";
            return codec.Contains("pgs") || codec.Contains("s_hdmv/pgs");
        }

        /// <summary>
        /// Restituisce l'estensione del file principale gestito dal rewriter
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Estensione SUP</returns>
        public string GetPrimaryExtension(TrackInfo track)
        {
            return ".sup";
        }

        /// <summary>
        /// Riscrive la traccia PGS nel formato comune dei rewriter canvas
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="track">Traccia sottotitoli</param>
        /// <param name="inputFile">File SUP input</param>
        /// <param name="outputFile">File SUP output</param>
        /// <param name="result">Risultato riscrittura</param>
        /// <returns>True se riscrittura riuscita</returns>
        public bool Rewrite(SubtitleCanvasRewriteContext context, TrackInfo track, string inputFile, string outputFile, out SubtitleCanvasRewriteResult result)
        {
            PgsSubtitleCanvasRewritePlan plan = new PgsSubtitleCanvasRewritePlan(context.Transform);
            PgsSubtitleCanvasRewriteReport report;
            bool ok;

            result = new SubtitleCanvasRewriteResult();
            result.Format = "PGS";
            ok = this.Rewrite(inputFile, outputFile, plan, out report);
            this.CopyReport(report, result);
            return ok;
        }

        /// <summary>
        /// Valida il SUP prodotto tramite ffmpeg
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="outputFile">File SUP output</param>
        /// <returns>True se ffmpeg lo legge</returns>
        public bool ValidateOutput(SubtitleCanvasRewriteContext context, string outputFile)
        {
            ProcessResult result = ProcessRunner.Run(context.FfmpegPath, new string[]
            {
                "-nostdin",
                "-v", "error",
                "-i", outputFile,
                "-map", "0:0",
                "-c", "copy",
                "-f", "null",
                "-"
            }, AppSettingsService.Instance.Settings.Advanced.SubtitleEdit.FfmpegTimeoutMs);

            return result != null && result.ExitCode == 0;
        }

        /// <summary>
        /// Riscrive PCS/WDS di un file SUP usando il piano indicato
        /// </summary>
        /// <param name="inputFile">File SUP di input</param>
        /// <param name="outputFile">File SUP di output</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="report">Report della riscrittura</param>
        /// <returns>True se il file riscritto è valido strutturalmente</returns>
        public bool Rewrite(string inputFile, string outputFile, PgsSubtitleCanvasRewritePlan plan, out PgsSubtitleCanvasRewriteReport report)
        {
            byte[] data = File.ReadAllBytes(inputFile);
            MemoryStream output = new MemoryStream();
            Dictionary<int, PgsObjectDefinition> epochObjects = new Dictionary<int, PgsObjectDefinition>();
            Dictionary<byte, PgsPaletteEntry> epochPalette = new Dictionary<byte, PgsPaletteEntry>();
            int pos = 0;
            int setStart;
            int setEnd;

            report = new PgsSubtitleCanvasRewriteReport();

            // Scorre il SUP per display-set completi
            while (pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE <= data.Length)
            {
                setStart = pos;
                if (!this.TryFindDisplaySetEnd(data, setStart, out setEnd))
                {
                    report.ErrorMessage = "display-set PGS incompleto";
                    return false;
                }

                report.DisplaySets++;

                // Riscrive in blocco PCS, WDS e ODS correlati dello stesso display-set
                if (!this.RewriteDisplaySet(data, setStart, setEnd, plan, output, epochObjects, epochPalette, report))
                {
                    return false;
                }

                pos = setEnd;
            }

            File.WriteAllBytes(outputFile, output.ToArray());
            return output.Length > 0 && report.PcsSegments > 0;
        }

        #endregion

        #region Metodi privati - Report comune

        /// <summary>
        /// Copia il report PGS nel risultato comune
        /// </summary>
        /// <param name="report">Report specifico PGS</param>
        /// <param name="result">Risultato comune da popolare</param>
        private void CopyReport(PgsSubtitleCanvasRewriteReport report, SubtitleCanvasRewriteResult result)
        {
            result.ErrorMessage = report != null ? report.ErrorMessage : "";
            if (report == null)
            {
                return;
            }

            // Espone i contatori formato-specifici con nomi stabili per log e diagnostica comune
            result.Set("display-set", report.DisplaySets);
            result.Set("PCS", report.PcsSegments);
            result.Set("WDS", report.WdsSegments);
            result.Set("oggetti", report.ObjectCoordinatesRewritten);
            result.Set("crop-oggetto", report.ObjectCropFieldsRewritten);
            result.Set("finestre", report.WindowDefinitionsRewritten);
            result.Set("ODS", report.OdsSegmentsRewritten);
            result.Set("bitmap-decoded", report.ObjectBitmapsDecoded);
            result.Set("bitmap-scaled", report.ObjectBitmapsScaled);
            result.Set("bitmap-encoded", report.ObjectBitmapsEncoded);
            result.Set("decode-warnings", report.DecodeWarnings);
            result.Set("scale-warnings", report.ScaleWarnings);
            result.Set("clamp", report.DisplaySetsClamped);

            // Summary compatto: PCS/WDS sempre presenti, bitmap/warning/clamp solo quando significativi
            result.Summary = "PCS=" + report.PcsSegments.ToString(CultureInfo.InvariantCulture) +
                ", WDS=" + report.WdsSegments.ToString(CultureInfo.InvariantCulture) +
                "/" + report.WindowDefinitionsRewritten.ToString(CultureInfo.InvariantCulture) +
                ", oggetti=" + report.ObjectCoordinatesRewritten.ToString(CultureInfo.InvariantCulture) +
                ", crop-oggetto=" + report.ObjectCropFieldsRewritten.ToString(CultureInfo.InvariantCulture) +
                this.FormatBitmapReport(report) +
                this.FormatWarningReport(report) +
                this.FormatClampReport(report);
        }

        /// <summary>
        /// Formatta il report clamp per log compatto
        /// </summary>
        /// <param name="report">Report specifico PGS</param>
        /// <returns>Testo clamp per summary</returns>
        private string FormatClampReport(PgsSubtitleCanvasRewriteReport report)
        {
            string result = "";
            if (report != null && report.DisplaySetsClamped > 0)
            {
                result = ", clamp=" + report.DisplaySetsClamped +
                    " (L" + report.MaxClampLeftPx.ToString(CultureInfo.InvariantCulture) +
                    " R" + report.MaxClampRightPx.ToString(CultureInfo.InvariantCulture) +
                    " U" + report.MaxClampUpPx.ToString(CultureInfo.InvariantCulture) +
                    " D" + report.MaxClampDownPx.ToString(CultureInfo.InvariantCulture) + ")";
            }

            return result;
        }

        /// <summary>
        /// Formatta report bitmap/ODS per log compatto
        /// </summary>
        /// <param name="report">Report specifico PGS</param>
        /// <returns>Testo bitmap per summary</returns>
        private string FormatBitmapReport(PgsSubtitleCanvasRewriteReport report)
        {
            string result = "";
            if (report != null && (report.OdsSegmentsRewritten > 0 || report.ObjectBitmapsScaled > 0))
            {
                result = ", ODS=" + report.OdsSegmentsRewritten.ToString(CultureInfo.InvariantCulture) +
                    ", bitmap=" + report.ObjectBitmapsDecoded.ToString(CultureInfo.InvariantCulture) +
                    "/" + report.ObjectBitmapsScaled.ToString(CultureInfo.InvariantCulture) +
                    "/" + report.ObjectBitmapsEncoded.ToString(CultureInfo.InvariantCulture);
                if (report.OdsSegmentsFragmented > 0)
                {
                    result += ", frag=" + report.OdsSegmentsFragmented.ToString(CultureInfo.InvariantCulture);
                }
            }

            return result;
        }

        /// <summary>
        /// Formatta warning non fatali di decoding/scaling bitmap
        /// </summary>
        /// <param name="report">Report specifico PGS</param>
        /// <returns>Testo warning per summary</returns>
        private string FormatWarningReport(PgsSubtitleCanvasRewriteReport report)
        {
            if (report == null || (report.DecodeWarnings == 0 && report.ScaleWarnings == 0))
            {
                return "";
            }

            return ", warnings=" + report.DecodeWarnings.ToString(CultureInfo.InvariantCulture) +
                "/" + report.ScaleWarnings.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region Metodi privati - Display-set

        /// <summary>
        /// Trova la fine del display-set PGS corrente
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset subito dopo il display-set</param>
        /// <returns>True se il display-set è completo</returns>
        private bool TryFindDisplaySetEnd(byte[] data, int start, out int end)
        {
            int pos = start;
            int packetLength;
            int segmentType;
            end = start;

            while (pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE <= data.Length)
            {
                if (!PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength))
                {
                    return false;
                }

                segmentType = data[pos + 10];
                pos += packetLength;
                if (segmentType == PgsSubtitleUtils.SEGMENT_END)
                {
                    end = pos;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Riscrive i packet rilevanti di un display-set
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="output">Output SUP</param>
        /// <param name="epochObjects">Oggetti noti nell'epoch corrente</param>
        /// <param name="epochPalette">Palette PDS nota nell'epoch corrente</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il display-set è riscritto correttamente</returns>
        private bool RewriteDisplaySet(byte[] data, int start, int end, PgsSubtitleCanvasRewritePlan plan, MemoryStream output, Dictionary<int, PgsObjectDefinition> epochObjects, Dictionary<byte, PgsPaletteEntry> epochPalette, PgsSubtitleCanvasRewriteReport report)
        {
            Dictionary<int, PgsObjectDefinition> displayObjects;
            Dictionary<int, PgsObjectSize> objectSizes;
            HashSet<int> writtenScaledObjects = new HashSet<int>();
            PgsDisplaySetAdjustment adjustment;
            int pos = start;
            int packetLength;
            int segmentType;
            byte[] packet;
            string errorMessage;

            // Nuova epoch: gli ODS precedenti non sono piu' referenziabili
            if (this.IsEpochStart(data, start, end))
            {
                epochObjects.Clear();
                epochPalette.Clear();
            }

            // La bitmap scaling usa il PDS corrente per preservare alpha e antialiasing degli indici palette
            if (plan.RequiresBitmapScaling && !PgsSubtitleUtils.CollectDisplaySetPaletteEntries(data, start, end, epochPalette, out errorMessage))
            {
                report.ErrorMessage = errorMessage;
                return false;
            }

            // Raccoglie gli ODS del display-set e li scala se il piano lo richiede
            if (!this.PrepareDisplaySetObjects(data, start, end, plan, epochObjects, epochPalette, report, out displayObjects))
            {
                return false;
            }

            // Usa lo stato epoch per conoscere le dimensioni degli oggetti referenziati dai PCS
            objectSizes = this.BuildObjectSizes(epochObjects);

            // Calcola un eventuale clamp locale per tenere il singolo display-set nel canvas finale
            if (!this.TryResolveDisplaySetAdjustment(data, start, end, plan, objectSizes, report, out adjustment))
            {
                return false;
            }

            // Copia ogni packet riscrivendo solo i segmenti sensibili a canvas e coordinate
            while (pos < end)
            {
                if (!PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) || pos + packetLength > end)
                {
                    report.ErrorMessage = "packet PGS fuori display-set";
                    return false;
                }

                packet = new byte[packetLength];
                Array.Copy(data, pos, packet, 0, packetLength);
                segmentType = packet[10];

                // PCS: canvas e coordinate oggetti
                if (segmentType == PgsSubtitleUtils.SEGMENT_PRESENTATION)
                {
                    if (!this.RewritePcsPacket(packet, plan, objectSizes, adjustment, report))
                    {
                        return false;
                    }
                }

                // WDS: finestre di clipping/rendering
                else if (segmentType == PgsSubtitleUtils.SEGMENT_WINDOW)
                {
                    if (!this.RewriteWdsPacket(packet, plan, adjustment, report))
                    {
                        return false;
                    }
                }

                // ODS: quando si scala la bitmap, sostituisce i segmenti originali con quelli ricodificati
                else if (segmentType == PgsSubtitleUtils.SEGMENT_OBJECT && plan.RequiresBitmapScaling)
                {
                    if (!this.WriteScaledObjectPackets(packet, displayObjects, writtenScaledObjects, output, report))
                    {
                        return false;
                    }

                    pos += packetLength;
                    continue;
                }

                output.Write(packet, 0, packet.Length);
                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Indica se il display-set apre una nuova epoch PGS
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <returns>True se il PCS dichiara epoch start</returns>
        private bool IsEpochStart(byte[] data, int start, int end)
        {
            int pos = start;
            int packetLength;
            int segmentLength;
            int payload;

            // Cerca nel display-set il PCS che dichiara l'epoch start
            while (pos < end && PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == PgsSubtitleUtils.SEGMENT_PRESENTATION)
                {
                    segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(data, pos + 11);
                    payload = pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
                    if (segmentLength >= 8 && payload + segmentLength <= data.Length && data[payload + 7] == 0x80)
                    {
                        return true;
                    }
                }

                pos += packetLength;
            }

            return false;
        }

        /// <summary>
        /// Prepara gli oggetti ODS del display-set e aggiorna lo stato epoch
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="epochObjects">Oggetti noti nell'epoch corrente</param>
        /// <param name="epochPalette">Palette PDS nota nell'epoch corrente</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="displayObjects">Oggetti del display-set corrente</param>
        /// <returns>True se gli oggetti sono stati raccolti e preparati</returns>
        private bool PrepareDisplaySetObjects(byte[] data, int start, int end, PgsSubtitleCanvasRewritePlan plan, Dictionary<int, PgsObjectDefinition> epochObjects, Dictionary<byte, PgsPaletteEntry> epochPalette, PgsSubtitleCanvasRewriteReport report, out Dictionary<int, PgsObjectDefinition> displayObjects)
        {
            Dictionary<int, PgsObjectDefinition> collectedObjects;
            PgsObjectDefinition rewrittenObject;

            displayObjects = new Dictionary<int, PgsObjectDefinition>();

            // Legge gli ODS completi presenti nel display-set corrente
            if (!PgsSubtitleUtils.CollectDisplaySetObjectDefinitions(data, start, end, report, out collectedObjects))
            {
                return false;
            }

            // Aggiorna lo stato epoch con gli oggetti originali o riscalati
            foreach (KeyValuePair<int, PgsObjectDefinition> kvp in collectedObjects)
            {
                if (plan.RequiresBitmapScaling)
                {
                    if (!this.TryScaleObject(kvp.Value, plan, epochPalette, report, out rewrittenObject))
                    {
                        return false;
                    }
                }
                else
                {
                    rewrittenObject = kvp.Value;
                }

                displayObjects[kvp.Key] = rewrittenObject;
                epochObjects[kvp.Key] = rewrittenObject;
            }

            return true;
        }

        /// <summary>
        /// Scala bitmap e metadati di un oggetto ODS
        /// </summary>
        /// <param name="definition">Definizione oggetto originale</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="epochPalette">Palette PDS nota nell'epoch corrente</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="rewrittenObject">Oggetto riscalato prodotto</param>
        /// <returns>True se decoding, scaling e ricodifica sono riusciti</returns>
        private bool TryScaleObject(PgsObjectDefinition definition, PgsSubtitleCanvasRewritePlan plan, Dictionary<byte, PgsPaletteEntry> epochPalette, PgsSubtitleCanvasRewriteReport report, out PgsObjectDefinition rewrittenObject)
        {
            PgsSubtitleBitmap bitmap;
            PgsSubtitleBitmap scaledBitmap;
            byte[] encoded;
            string errorMessage;
            int decodeWarnings;
            int scaleWarnings;
            int outputWidth;
            int outputHeight;

            rewrittenObject = null;

            // Decodifica la bitmap palette-indexed dall'RLE PGS originale
            if (!PgsSubtitleUtils.DecodeObjectBitmap(definition, out bitmap, out errorMessage, out decodeWarnings))
            {
                report.ErrorMessage = errorMessage;
                return false;
            }

            report.ObjectBitmapsDecoded++;
            report.DecodeWarnings += decodeWarnings;
            outputWidth = plan.Transform.MapObjectWidth(definition.Width);
            outputHeight = plan.Transform.MapObjectHeight(definition.Height);

            // Scala in spazio palette-aware quando il PDS e' disponibile, mantenendo invariati i segmenti palette originali
            scaledBitmap = PgsSubtitleUtils.ScaleBitmap(bitmap, outputWidth, outputHeight, epochPalette, out scaleWarnings);
            report.ObjectBitmapsScaled++;
            report.ScaleWarnings += scaleWarnings;

            // Ricodifica l'oggetto in RLE e mantiene header/timing del primo ODS originale
            encoded = PgsSubtitleUtils.EncodeBitmapRle(scaledBitmap);
            report.ObjectBitmapsEncoded++;
            rewrittenObject = new PgsObjectDefinition(
                definition.ObjectId,
                definition.Version,
                scaledBitmap.Width,
                scaledBitmap.Height,
                encoded,
                definition.FirstPacketHeader);
            return true;
        }

        /// <summary>
        /// Costruisce dimensioni oggetto dallo stato epoch
        /// </summary>
        /// <param name="epochObjects">Oggetti noti nell'epoch corrente</param>
        /// <returns>Mappa object id/dimensioni</returns>
        private Dictionary<int, PgsObjectSize> BuildObjectSizes(Dictionary<int, PgsObjectDefinition> epochObjects)
        {
            Dictionary<int, PgsObjectSize> result = new Dictionary<int, PgsObjectSize>();
            foreach (KeyValuePair<int, PgsObjectDefinition> kvp in epochObjects)
            {
                result[kvp.Key] = new PgsObjectSize(kvp.Value.Width, kvp.Value.Height);
            }

            return result;
        }

        /// <summary>
        /// Scrive i packet ODS scalati una sola volta per oggetto del display-set
        /// </summary>
        /// <param name="packet">Packet ODS originale</param>
        /// <param name="displayObjects">Oggetti del display-set corrente</param>
        /// <param name="writtenScaledObjects">Object id gia' scritti in output</param>
        /// <param name="output">Stream output SUP</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se i packet scalati sono stati scritti</returns>
        private bool WriteScaledObjectPackets(byte[] packet, Dictionary<int, PgsObjectDefinition> displayObjects, HashSet<int> writtenScaledObjects, MemoryStream output, PgsSubtitleCanvasRewriteReport report)
        {
            PgsObjectDefinition definition;
            List<byte[]> packets;
            string errorMessage;
            int objectId;

            if (packet.Length < PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE + 2)
            {
                report.ErrorMessage = "ODS PGS troppo corto";
                return false;
            }

            objectId = PgsSubtitleUtils.ReadUInt16BigEndian(packet, PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE);

            // Gli ODS frammentati arrivano in piu' packet, ma il nuovo oggetto va scritto una sola volta
            if (writtenScaledObjects.Contains(objectId))
            {
                return true;
            }

            if (!displayObjects.TryGetValue(objectId, out definition))
            {
                report.ErrorMessage = "ODS PGS senza oggetto ricostruito";
                return false;
            }

            // Produce uno o piu' packet ODS in base alla dimensione RLE ricodificata
            if (!PgsSubtitleUtils.BuildObjectDefinitionPackets(definition, out packets, out errorMessage))
            {
                report.ErrorMessage = errorMessage;
                return false;
            }

            for (int i = 0; i < packets.Count; i++)
            {
                output.Write(packets[i], 0, packets[i].Length);
            }

            report.OdsSegmentsRewritten += packets.Count;
            if (packets.Count > 1)
            {
                report.OdsSegmentsFragmented += packets.Count;
            }

            writtenScaledObjects.Add(objectId);
            return true;
        }

        #endregion

        #region Metodi privati - Clamp display-set

        /// <summary>
        /// Calcola il delta locale necessario a tenere il display-set dentro il canvas finale
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="objectSizes">Dimensioni oggetto note</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="adjustment">Delta locale risolto per il display-set</param>
        /// <returns>True se il delta locale e' applicabile</returns>
        private bool TryResolveDisplaySetAdjustment(byte[] data, int start, int end, PgsSubtitleCanvasRewritePlan plan, Dictionary<int, PgsObjectSize> objectSizes, PgsSubtitleCanvasRewriteReport report, out PgsDisplaySetAdjustment adjustment)
        {
            PgsDisplaySetBounds bounds;
            int deltaX = 0;
            int deltaY = 0;
            adjustment = new PgsDisplaySetAdjustment();

            // Prima prova con i bounds degli oggetti PCS, cioe' quelli realmente renderizzati
            if (!this.TryCollectDisplaySetBounds(data, start, end, plan, objectSizes, report, out bounds))
            {
                return false;
            }

            // Se il display-set non ha oggetti PCS, usa le finestre WDS come fallback
            if (!bounds.HasObjects)
            {
                if (!this.TryCollectWdsBounds(data, start, end, plan, report, out bounds))
                {
                    return false;
                }

                if (!bounds.HasObjects)
                {
                    return true;
                }
            }

            // Se la bounding box e' piu' grande del canvas finale non esiste un clamp valido
            if (bounds.Width > plan.Transform.OutputCanvasWidth || bounds.Height > plan.Transform.OutputCanvasHeight)
            {
                report.ErrorMessage = "bounding box PCS fuori canvas: " + bounds.Width + "x" + bounds.Height;
                return false;
            }

            // Sposta verso sinistra/destra solo quanto basta a rientrare nel canvas
            if (bounds.Right > plan.Transform.OutputCanvasWidth)
            {
                deltaX = plan.Transform.OutputCanvasWidth - bounds.Right;
            }
            if (bounds.Left + deltaX < 0)
            {
                deltaX += -(bounds.Left + deltaX);
            }

            // Sposta verso alto/basso solo quanto basta a rientrare nel canvas
            if (bounds.Bottom > plan.Transform.OutputCanvasHeight)
            {
                deltaY = plan.Transform.OutputCanvasHeight - bounds.Bottom;
            }
            if (bounds.Top + deltaY < 0)
            {
                deltaY += -(bounds.Top + deltaY);
            }

            // Verifica finale: il clamp locale non deve creare un nuovo sforamento
            if (bounds.Left + deltaX < 0 || bounds.Right + deltaX > plan.Transform.OutputCanvasWidth ||
                bounds.Top + deltaY < 0 || bounds.Bottom + deltaY > plan.Transform.OutputCanvasHeight)
            {
                report.ErrorMessage = "bounding box PCS non clampabile";
                return false;
            }

            adjustment.DeltaX = deltaX;
            adjustment.DeltaY = deltaY;
            if (deltaX != 0 || deltaY != 0)
            {
                this.UpdateClampReport(deltaX, deltaY, report);
            }

            return true;
        }

        /// <summary>
        /// Raccoglie la bounding box delle finestre WDS dopo offset globale
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="bounds">Bounds WDS raccolti</param>
        /// <returns>True se la raccolta e' riuscita</returns>
        private bool TryCollectWdsBounds(byte[] data, int start, int end, PgsSubtitleCanvasRewritePlan plan, PgsSubtitleCanvasRewriteReport report, out PgsDisplaySetBounds bounds)
        {
            int pos = start;
            int packetLength;
            int segmentLength;
            int payload;
            int windowCount;
            int windowPos;
            int x;
            int y;
            int width;
            int height;
            int newX;
            int newY;
            int newWidth;
            int newHeight;

            bounds = new PgsDisplaySetBounds();

            // Scorre tutte le WDS e accumula le finestre gia' trasformate nel canvas finale
            while (pos < end && PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == PgsSubtitleUtils.SEGMENT_WINDOW)
                {
                    segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(data, pos + 11);
                    payload = pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
                    if (segmentLength < 1 || payload + segmentLength > data.Length)
                    {
                        report.ErrorMessage = "WDS PGS troppo corto";
                        return false;
                    }

                    windowCount = data[payload];
                    windowPos = payload + 1;

                    // Ogni entry WDS contiene posizione e dimensione della finestra
                    for (int i = 0; i < windowCount; i++)
                    {
                        if (windowPos + 9 > payload + segmentLength)
                        {
                            report.ErrorMessage = "lista finestre WDS incompleta";
                            return false;
                        }

                        x = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 1);
                        y = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 3);
                        width = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 5);
                        height = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 7);
                        plan.Transform.ResolveWindowRect(x, y, width, height, 0, 0, out newX, out newY, out newWidth, out newHeight);
                        bounds.Include(newX, newY, newX + newWidth, newY + newHeight);
                        windowPos += 9;
                    }
                }

                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Raccoglie la bounding box degli oggetti PCS dopo offset globale
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="objectSizes">Dimensioni oggetto note</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="bounds">Bounds PCS raccolti</param>
        /// <returns>True se la raccolta e' riuscita</returns>
        private bool TryCollectDisplaySetBounds(byte[] data, int start, int end, PgsSubtitleCanvasRewritePlan plan, Dictionary<int, PgsObjectSize> objectSizes, PgsSubtitleCanvasRewriteReport report, out PgsDisplaySetBounds bounds)
        {
            int pos = start;
            int packetLength;
            int segmentLength;
            int payload;
            int objectCount;
            int objectPos;
            int objectId;
            int objectFlags;
            int x;
            int y;
            bool hasObjectSize;
            PgsObjectSize objectSize;

            bounds = new PgsDisplaySetBounds();

            // Scorre i PCS e accumula i bounds degli oggetti gia' trasformati
            while (pos < end && PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == PgsSubtitleUtils.SEGMENT_PRESENTATION)
                {
                    segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(data, pos + 11);
                    payload = pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
                    if (segmentLength < 11 || payload + segmentLength > data.Length)
                    {
                        report.ErrorMessage = "PCS PGS troppo corto";
                        return false;
                    }

                    objectCount = data[payload + 10];
                    objectPos = payload + 11;

                    // Ogni entry PCS posiziona un oggetto ODS nel display-set corrente
                    for (int i = 0; i < objectCount; i++)
                    {
                        if (objectPos + 8 > payload + segmentLength)
                        {
                            report.ErrorMessage = "lista oggetti PCS incompleta";
                            return false;
                        }

                        objectId = PgsSubtitleUtils.ReadUInt16BigEndian(data, objectPos);
                        objectFlags = data[objectPos + 3];
                        x = plan.Transform.MapObjectX(PgsSubtitleUtils.ReadUInt16BigEndian(data, objectPos + 4));
                        y = plan.Transform.MapObjectY(PgsSubtitleUtils.ReadUInt16BigEndian(data, objectPos + 6));
                        hasObjectSize = objectSizes.TryGetValue(objectId, out objectSize);

                        // Se la dimensione ODS non e' nota, valida almeno la coordinata puntuale
                        if (hasObjectSize)
                        {
                            bounds.Include(x, y, x + objectSize.Width, y + objectSize.Height);
                        }
                        else
                        {
                            bounds.Include(x, y, x + 1, y + 1);
                        }

                        objectPos += 8;
                        if ((objectFlags & 0x40) != 0)
                        {
                            if (objectPos + 8 > payload + segmentLength)
                            {
                                report.ErrorMessage = "crop oggetto PCS incompleto";
                                return false;
                            }

                            objectPos += 8;
                        }
                    }
                }

                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Aggiorna report clamp display-set
        /// </summary>
        /// <param name="deltaX">Delta orizzontale applicato</param>
        /// <param name="deltaY">Delta verticale applicato</param>
        /// <param name="report">Report aggiornato</param>
        private void UpdateClampReport(int deltaX, int deltaY, PgsSubtitleCanvasRewriteReport report)
        {
            report.DisplaySetsClamped++;
            if (deltaX < 0)
            {
                report.MaxClampLeftPx = Math.Max(report.MaxClampLeftPx, -deltaX);
            }
            else if (deltaX > 0)
            {
                report.MaxClampRightPx = Math.Max(report.MaxClampRightPx, deltaX);
            }

            if (deltaY < 0)
            {
                report.MaxClampUpPx = Math.Max(report.MaxClampUpPx, -deltaY);
            }
            else if (deltaY > 0)
            {
                report.MaxClampDownPx = Math.Max(report.MaxClampDownPx, deltaY);
            }
        }

        #endregion

        #region Metodi privati - Segmenti PGS

        /// <summary>
        /// Riscrive un Presentation Composition Segment
        /// </summary>
        /// <param name="packet">Packet PCS</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="objectSizes">Dimensioni oggetto note</param>
        /// <param name="adjustment">Delta locale del display-set</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il PCS resta coerente</returns>
        private bool RewritePcsPacket(byte[] packet, PgsSubtitleCanvasRewritePlan plan, Dictionary<int, PgsObjectSize> objectSizes, PgsDisplaySetAdjustment adjustment, PgsSubtitleCanvasRewriteReport report)
        {
            int segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(packet, 11);
            int payload = PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
            int oldWidth;
            int oldHeight;
            int objectCount;
            int pos;
            int objectId;
            int objectFlags;
            int x;
            int y;
            int newX;
            int newY;
            bool hasObjectSize;
            PgsObjectSize objectSize;
            int cropX;
            int cropY;
            int cropWidth;
            int cropHeight;
            int newCropX;
            int newCropY;
            int newCropWidth;
            int newCropHeight;

            // Verifica dimensione canvas dichiarata dal PCS originale
            if (segmentLength < 11 || payload + segmentLength > packet.Length)
            {
                report.ErrorMessage = "PCS PGS troppo corto";
                return false;
            }

            oldWidth = PgsSubtitleUtils.ReadUInt16BigEndian(packet, payload);
            oldHeight = PgsSubtitleUtils.ReadUInt16BigEndian(packet, payload + 2);
            if (oldWidth != plan.Transform.InputCanvasWidth || oldHeight != plan.Transform.InputCanvasHeight)
            {
                report.ErrorMessage = "canvas PCS inatteso " + oldWidth + "x" + oldHeight;
                return false;
            }

            PgsSubtitleUtils.WriteUInt16BigEndian(packet, payload, plan.Transform.OutputCanvasWidth);
            PgsSubtitleUtils.WriteUInt16BigEndian(packet, payload + 2, plan.Transform.OutputCanvasHeight);
            objectCount = packet[payload + 10];
            pos = payload + 11;

            // Riscrive le coordinate degli oggetti PCS una entry alla volta
            for (int i = 0; i < objectCount; i++)
            {
                if (pos + 8 > payload + segmentLength)
                {
                    report.ErrorMessage = "lista oggetti PCS incompleta";
                    return false;
                }

                objectId = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos);
                objectFlags = packet[pos + 3];
                x = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 4);
                y = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 6);
                newX = plan.Transform.MapObjectX(x) + adjustment.DeltaX;
                newY = plan.Transform.MapObjectY(y) + adjustment.DeltaY;
                hasObjectSize = objectSizes.TryGetValue(objectId, out objectSize);
                if (!this.ValidateObjectBounds(newX, newY, hasObjectSize, objectSize, plan, report))
                {
                    return false;
                }

                // Applica coordinate globali piu' eventuale clamp locale del display-set
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 4, newX);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 6, newY);
                report.ObjectCoordinatesRewritten++;
                pos += 8;

                if ((objectFlags & 0x40) != 0)
                {
                    if (pos + 8 > payload + segmentLength)
                    {
                        report.ErrorMessage = "crop oggetto PCS incompleto";
                        return false;
                    }

                    cropX = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos);
                    cropY = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 2);
                    cropWidth = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 4);
                    cropHeight = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 6);
                    newCropX = this.ScaleCropOffset(cropX, plan.Transform.ScaleX);
                    newCropY = this.ScaleCropOffset(cropY, plan.Transform.ScaleY);
                    newCropWidth = plan.Transform.MapObjectWidth(cropWidth);
                    newCropHeight = plan.Transform.MapObjectHeight(cropHeight);
                    if (hasObjectSize && !this.ValidateObjectCropBounds(newCropX, newCropY, newCropWidth, newCropHeight, objectSize, report))
                    {
                        return false;
                    }

                    PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos, newCropX);
                    PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 2, newCropY);
                    PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 4, newCropWidth);
                    PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 6, newCropHeight);
                    report.ObjectCropFieldsRewritten++;

                    pos += 8;
                }
            }

            report.PcsSegments++;
            return true;
        }

        /// <summary>
        /// Riscrive un Window Definition Segment
        /// </summary>
        /// <param name="packet">Packet WDS</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="adjustment">Delta locale del display-set</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il WDS resta coerente</returns>
        private bool RewriteWdsPacket(byte[] packet, PgsSubtitleCanvasRewritePlan plan, PgsDisplaySetAdjustment adjustment, PgsSubtitleCanvasRewriteReport report)
        {
            int segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(packet, 11);
            int payload = PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
            int windowCount;
            int pos;
            int x;
            int y;
            int width;
            int height;
            int newX;
            int newY;
            int newWidth;
            int newHeight;

            // Valida payload WDS prima di leggere il numero finestre
            if (segmentLength < 1 || payload + segmentLength > packet.Length)
            {
                report.ErrorMessage = "WDS PGS troppo corto";
                return false;
            }

            windowCount = packet[payload];
            pos = payload + 1;

            // Riscrive ogni finestra WDS nello stesso spazio coordinate del PCS
            for (int i = 0; i < windowCount; i++)
            {
                if (pos + 9 > payload + segmentLength)
                {
                    report.ErrorMessage = "lista finestre WDS incompleta";
                    return false;
                }

                x = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 1);
                y = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 3);
                width = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 5);
                height = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 7);
                plan.Transform.ResolveWindowRect(x, y, width, height, adjustment.DeltaX, adjustment.DeltaY, out newX, out newY, out newWidth, out newHeight);

                if (!this.ValidateRectBounds(newX, newY, newWidth, newHeight, plan, report, "WDS fuori canvas"))
                {
                    return false;
                }

                // Aggiorna posizione e dimensione della finestra nel packet
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 1, newX);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 3, newY);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 5, newWidth);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 7, newHeight);
                report.WindowDefinitionsRewritten++;
                pos += 9;
            }

            report.WdsSegments++;
            return true;
        }

        #endregion

        #region Metodi privati - Validazione

        /// <summary>
        /// Valida bounds di un oggetto PCS
        /// </summary>
        /// <param name="x">Coordinata X oggetto</param>
        /// <param name="y">Coordinata Y oggetto</param>
        /// <param name="hasSize">True se la dimensione oggetto e' nota</param>
        /// <param name="size">Dimensione oggetto nota</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se l'oggetto resta nel canvas</returns>
        private bool ValidateObjectBounds(int x, int y, bool hasSize, PgsObjectSize size, PgsSubtitleCanvasRewritePlan plan, PgsSubtitleCanvasRewriteReport report)
        {
            if (hasSize)
            {
                return this.ValidateRectBounds(x, y, size.Width, size.Height, plan, report, "oggetto PCS fuori canvas");
            }

            if (x < 0 || y < 0 || x >= plan.Transform.OutputCanvasWidth || y >= plan.Transform.OutputCanvasHeight)
            {
                report.ErrorMessage = "coordinate PCS fuori canvas";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Scala un offset crop oggetto PCS preservando lo zero
        /// </summary>
        /// <param name="value">Offset crop originale</param>
        /// <param name="scale">Fattore scala</param>
        /// <returns>Offset crop scalato</returns>
        private int ScaleCropOffset(int value, double scale)
        {
            return (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Valida crop object PCS dopo scaling
        /// </summary>
        /// <param name="x">Coordinata X del crop</param>
        /// <param name="y">Coordinata Y del crop</param>
        /// <param name="width">Larghezza crop</param>
        /// <param name="height">Altezza crop</param>
        /// <param name="objectSize">Dimensione bitmap oggetto</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il crop resta dentro la bitmap</returns>
        private bool ValidateObjectCropBounds(int x, int y, int width, int height, PgsObjectSize objectSize, PgsSubtitleCanvasRewriteReport report)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > objectSize.Width || y + height > objectSize.Height)
            {
                report.ErrorMessage = "crop oggetto PCS fuori bitmap";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Valida bounds di un rettangolo nel canvas finale
        /// </summary>
        /// <param name="x">Coordinata X rettangolo</param>
        /// <param name="y">Coordinata Y rettangolo</param>
        /// <param name="width">Larghezza rettangolo</param>
        /// <param name="height">Altezza rettangolo</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="errorMessage">Errore da impostare in caso di bounds invalidi</param>
        /// <returns>True se il rettangolo resta nel canvas</returns>
        private bool ValidateRectBounds(int x, int y, int width, int height, PgsSubtitleCanvasRewritePlan plan, PgsSubtitleCanvasRewriteReport report, string errorMessage)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
                x + width > plan.Transform.OutputCanvasWidth ||
                y + height > plan.Transform.OutputCanvasHeight)
            {
                report.ErrorMessage = errorMessage;
                return false;
            }

            return true;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Delta locale applicato a un display-set
        /// </summary>
        private class PgsDisplaySetAdjustment
        {
            /// <summary>
            /// Delta X locale
            /// </summary>
            public int DeltaX { get; set; }

            /// <summary>
            /// Delta Y locale
            /// </summary>
            public int DeltaY { get; set; }
        }

        /// <summary>
        /// Bounding box degli oggetti PCS di un display-set
        /// </summary>
        private class PgsDisplaySetBounds
        {
            /// <summary>
            /// True se sono stati inclusi oggetti
            /// </summary>
            public bool HasObjects { get; private set; }

            /// <summary>
            /// Coordinata sinistra inclusiva
            /// </summary>
            public int Left { get; private set; }

            /// <summary>
            /// Coordinata superiore inclusiva
            /// </summary>
            public int Top { get; private set; }

            /// <summary>
            /// Coordinata destra esclusiva
            /// </summary>
            public int Right { get; private set; }

            /// <summary>
            /// Coordinata inferiore esclusiva
            /// </summary>
            public int Bottom { get; private set; }

            /// <summary>
            /// Larghezza bounding box
            /// </summary>
            public int Width
            {
                get { return this.Right - this.Left; }
            }

            /// <summary>
            /// Altezza bounding box
            /// </summary>
            public int Height
            {
                get { return this.Bottom - this.Top; }
            }

            /// <summary>
            /// Include un rettangolo nella bounding box
            /// </summary>
            /// <param name="left">Coordinata sinistra inclusiva</param>
            /// <param name="top">Coordinata superiore inclusiva</param>
            /// <param name="right">Coordinata destra esclusiva</param>
            /// <param name="bottom">Coordinata inferiore esclusiva</param>
            public void Include(int left, int top, int right, int bottom)
            {
                if (!this.HasObjects)
                {
                    this.Left = left;
                    this.Top = top;
                    this.Right = right;
                    this.Bottom = bottom;
                    this.HasObjects = true;
                    return;
                }

                this.Left = Math.Min(this.Left, left);
                this.Top = Math.Min(this.Top, top);
                this.Right = Math.Max(this.Right, right);
                this.Bottom = Math.Max(this.Bottom, bottom);
            }
        }

        #endregion
    }
}
