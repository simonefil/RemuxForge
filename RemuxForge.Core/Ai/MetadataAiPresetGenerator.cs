using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Ciclo di generazione preset Metadata: il modello interroga i file e invia il preset finché la validazione lo accetta
    /// </summary>
    public class MetadataAiPresetGenerator
    {
        #region Costanti

        /// <summary>
        /// Giri massimi predefiniti prima di arrendersi
        /// </summary>
        public const int DEFAULT_MAX_ROUNDS = 20;

        /// <summary>
        /// Valore minimo ammesso per i giri massimi
        /// </summary>
        public const int MIN_MAX_ROUNDS = 1;

        /// <summary>
        /// Valore massimo ammesso per i giri massimi
        /// </summary>
        public const int MAX_MAX_ROUNDS = 50;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Client della Responses API
        /// </summary>
        private readonly OpenAiResponsesClient _client;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="client">Client della Responses API</param>
        public MetadataAiPresetGenerator(OpenAiResponsesClient client)
        {
            this._client = client;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Genera un preset dalla richiesta dell'utente sui file caricati
        /// </summary>
        /// <param name="model">Modello</param>
        /// <param name="userRequest">Richiesta dell'utente</param>
        /// <param name="records">Record dei file caricati, letti e mai modificati</param>
        /// <param name="maxRounds">Giri massimi, ricondotti all'intervallo ammesso</param>
        /// <param name="progress">Passi notificati al wizard</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Esito della generazione</returns>
        public async Task<AiPresetGenerationResult> GenerateAsync(string model, string userRequest, List<MkvMetadataRecord> records, int maxRounds, IProgress<AiPresetGenerationStep> progress, CancellationToken cancellationToken)
        {
            int roundLimit = Math.Clamp(maxRounds, MIN_MAX_ROUNDS, MAX_MAX_ROUNDS);
            MetadataAiTools tools = new MetadataAiTools(records);
            JsonArray toolDefinitions = MetadataAiTools.BuildToolDefinitions();
            string instructions = MetadataAiContextBuilder.BuildInstructions();
            JsonArray input = new JsonArray();
            List<string> lastErrors = new List<string>();

            input.Add(CreateUserMessage(MetadataAiContextBuilder.BuildUserMessage(userRequest, tools.BuildOverview())));

            for (int round = 1; round <= roundLimit; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, AiPresetGenerationStepKind.RequestSent, round, "", null);

                List<JsonObject> output = await this._client.CreateResponseAsync(model, instructions, input, toolDefinitions, cancellationToken);
                List<JsonObject> calls = new List<JsonObject>();
                StringBuilder messageText = new StringBuilder();

                // Ogni item torna invariato al giro successivo: senza stato lato server è l'unica memoria del modello
                for (int i = 0; i < output.Count; i++)
                {
                    string type = AiOAuthFlow.GetString(output[i], "type");
                    input.Add(output[i].DeepClone());
                    if (type == "function_call")
                        calls.Add(output[i]);
                    else if (type == "message")
                        AppendMessageText(output[i], messageText);
                }

                if (calls.Count == 0)
                {
                    Report(progress, AiPresetGenerationStepKind.MissingSubmission, round, messageText.ToString().Trim(), null);
                    input.Add(CreateUserMessage(MetadataAiContextBuilder.BuildMissingSubmissionMessage()));
                    continue;
                }

                for (int i = 0; i < calls.Count; i++)
                {
                    string name = AiOAuthFlow.GetString(calls[i], "name");
                    string arguments = AiOAuthFlow.GetString(calls[i], "arguments");
                    string result;

                    if (name == MetadataAiTools.QUERY_FILES_TOOL)
                    {
                        result = tools.QueryFiles(arguments);
                        Report(progress, AiPresetGenerationStepKind.FilesQueried, round, arguments, null);
                    }
                    else if (name == MetadataAiTools.SUBMIT_PRESET_TOOL)
                    {
                        AiPresetGenerationResult outcome;
                        result = tools.SubmitPreset(arguments, out outcome);
                        if (outcome.Success)
                        {
                            outcome.Rounds = round;
                            Report(progress, AiPresetGenerationStepKind.PresetAccepted, round, "", outcome.Warnings);
                            return outcome;
                        }

                        lastErrors = outcome.Errors;
                        Report(progress, AiPresetGenerationStepKind.PresetRejected, round, "", outcome.Errors);
                    }
                    else
                    {
                        result = new JsonObject { ["valid"] = false, ["errors"] = new JsonArray("Unknown tool '" + name + "'.") }.ToJsonString();
                    }

                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = AiOAuthFlow.GetString(calls[i], "call_id"),
                        ["output"] = result
                    });
                }
            }

            AiPresetGenerationResult failed = new AiPresetGenerationResult();
            failed.Rounds = roundLimit;
            failed.Errors = lastErrors;
            return failed;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Crea un messaggio utente per l'input
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Item messaggio</returns>
        private static JsonObject CreateUserMessage(string text)
        {
            return new JsonObject { ["role"] = "user", ["content"] = text };
        }

        /// <summary>
        /// Accoda il testo di un item messaggio del modello
        /// </summary>
        /// <param name="message">Item messaggio</param>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendMessageText(JsonObject message, StringBuilder text)
        {
            JsonArray content = message["content"] as JsonArray;
            if (content == null)
                return;

            for (int i = 0; i < content.Count; i++)
            {
                JsonObject part = content[i] as JsonObject;
                if (part != null && AiOAuthFlow.GetString(part, "type") == "output_text")
                    text.AppendLine(AiOAuthFlow.GetString(part, "text"));
            }
        }

        /// <summary>
        /// Notifica un passo al wizard
        /// </summary>
        /// <param name="progress">Destinatario</param>
        /// <param name="kind">Tipo di passo</param>
        /// <param name="round">Giro corrente</param>
        /// <param name="detail">Dettaglio</param>
        /// <param name="errors">Errori o avvisi del passo</param>
        private static void Report(IProgress<AiPresetGenerationStep> progress, AiPresetGenerationStepKind kind, int round, string detail, List<string> errors)
        {
            if (progress == null)
                return;

            AiPresetGenerationStep step = new AiPresetGenerationStep();
            step.Kind = kind;
            step.Round = round;
            step.Detail = detail != null ? detail : "";
            if (errors != null)
                step.Errors = new List<string>(errors);

            progress.Report(step);
        }

        #endregion
    }
}
